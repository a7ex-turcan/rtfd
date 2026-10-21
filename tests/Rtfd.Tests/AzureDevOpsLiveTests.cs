using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Posting;
using Rtfd.Providers;
using Rtfd.Providers.AzureDevOps;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// Against Azure DevOps itself, on a scratch draft PR only (CLAUDE.md rule 4), and only with
/// <c>RTFD_LIVE_AZURE_DEVOPS=1</c>. Nothing about the organization is in this repo: the variables
/// name it. <c>RTFD_ADO_SANDBOX</c> is <c>organization/project/repository</c>,
/// <c>RTFD_ADO_PR</c> the draft PR, whose head adds <c>rtfd-sandbox/Counter.cs</c> with at least
/// ten lines; <c>RTFD_ADO_TENANT</c> the Entra tenant for the Azure CLI, or <c>ADO_TOKEN</c> a PAT.
/// Each run adds threads to that PR and leaves rtfd's vote on it.
/// </summary>
public sealed class AzureDevOpsLiveTests
{
    private const string File = "rtfd-sandbox/Counter.cs";

    private static (AzureDevOpsProvider Provider, int Pr, string Head) Sandbox()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RTFD_LIVE_AZURE_DEVOPS") == "1", "set RTFD_LIVE_AZURE_DEVOPS=1 to post to the scratch PR");
        var slug = Environment.GetEnvironmentVariable("RTFD_ADO_SANDBOX");
        var pr = Environment.GetEnvironmentVariable("RTFD_ADO_PR");
        Assert.SkipWhen(string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(pr), "RTFD_ADO_SANDBOX and RTFD_ADO_PR name the scratch PR");

        TokenSource tokens = Environment.GetEnvironmentVariable("ADO_TOKEN") is { Length: > 0 }
            ? new EnvironmentTokenSource("ADO_TOKEN", Environment.GetEnvironmentVariable)
            : new AzureCliTokenSource(Environment.GetEnvironmentVariable("RTFD_ADO_TENANT"), TimeProvider.System, AzureCliTokenSource.RealRunner(Environment.GetEnvironmentVariable));
        var provider = new AzureDevOpsProvider(slug!, tokens);
        var number = int.Parse(pr!, System.Globalization.CultureInfo.InvariantCulture);
        var head = provider.GetPullRequestAsync(number, TestContext.Current.CancellationToken).GetAwaiter().GetResult().HeadSha;
        return (provider, number, head);
    }

    [Fact]
    public async Task A_review_posted_a_thread_at_a_time_reads_back_with_the_same_hashes()
    {
        var (provider, pr, head) = Sandbox();
        var cancel = TestContext.Current.CancellationToken;
        var stamp = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var me = await provider.WhoAmIAsync(cancel);

        var summaryBody = $"Live test summary {stamp}.\n\n{Poster.Marker(head, null, 1)}";
        var summary = await provider.StartThreadAsync(new ThreadPost(pr, head, summaryBody, null, null), cancel);
        var inlineBody = $"Should: live test comment {stamp}.";
        var inline = await provider.StartThreadAsync(new ThreadPost(pr, head, inlineBody, new ReviewPostComment(File, 3, DiffSide.New, inlineBody), summary.Id), cancel);

        var review = await provider.GetReviewAsync(pr, summary.Id, cancel);
        Assert.NotNull(review);
        Assert.Equal(me, review.Author, ignoreCase: true);
        Assert.Equal(Poster.Hash(summaryBody), Poster.Hash(review.Body));
        var comment = Assert.Single(review.Comments, c => c.Id == inline.Id);
        Assert.Equal((File, Poster.Hash(inlineBody)), (comment.Path, Poster.Hash(comment.Body)));

        var listed = await provider.ListReviewCommentsAsync(pr, cancel);
        Assert.Equal(summary.Id, Assert.Single(listed, c => c.Id == inline.Id).ReviewId);
        Assert.Contains(await provider.ListReviewsAsync(pr, cancel), r => r.Id == summary.Id);

        var thread = Assert.Single(await provider.GetReviewThreadsAsync(pr, cancel), t => t.Id == inline.Id);
        Assert.Equal((3, false), (thread.Line, thread.IsResolved));
    }

    [Fact]
    public async Task A_reply_and_a_resolve_land_on_rtfd_s_own_thread()
    {
        var (provider, pr, head) = Sandbox();
        var cancel = TestContext.Current.CancellationToken;
        var body = $"Could: live test thread {DateTimeOffset.UtcNow:O}.";
        var root = await provider.StartThreadAsync(new ThreadPost(pr, head, body, new ReviewPostComment(File, 5, DiffSide.New, body), null), cancel);

        var reply = await provider.ReplyAsync(pr, root.Id, "Looks good now.", cancel);
        await provider.ResolveThreadAsync(pr, root.Id, cancel);
        await provider.ResolveThreadAsync(pr, root.Id, cancel);

        var thread = Assert.Single(await provider.GetReviewThreadsAsync(pr, cancel), t => t.Id == root.Id);
        Assert.True(thread.IsResolved);
        Assert.Equal([root.Id, reply.Id], thread.Comments.Select(c => c.Id));
        Assert.Equal(Poster.Hash("Looks good now."), Poster.Hash(thread.Comments[1].Body));
    }

    /// <summary>On a draft Azure DevOps refuses any vote (checked 2026-10-21); on a published PR the vote reads back.</summary>
    [Fact]
    public async Task A_vote_reads_back_as_the_review_s_state_and_a_draft_takes_none()
    {
        var (provider, pr, head) = Sandbox();
        var cancel = TestContext.Current.CancellationToken;
        if ((await provider.GetPullRequestAsync(pr, cancel)).IsDraft)
        {
            var refused = await Assert.ThrowsAsync<ProviderException>(() => provider.SetVoteAsync(pr, Verdict.Approve, cancel));
            Assert.Equal(400, refused.Status);
            Assert.Contains("You cannot vote on draft pull requests", refused.Message, StringComparison.Ordinal);
            return;
        }

        var summary = await provider.StartThreadAsync(new ThreadPost(pr, head, $"Live test vote {DateTimeOffset.UtcNow:O}.", null, null), cancel);

        await provider.SetVoteAsync(pr, Verdict.Approve, cancel);
        Assert.Equal("APPROVED", (await provider.GetReviewAsync(pr, summary.Id, cancel))!.State);

        await provider.SetVoteAsync(pr, Verdict.RequestChanges, cancel);
        Assert.Equal("CHANGES_REQUESTED", (await provider.GetReviewAsync(pr, summary.Id, cancel))!.State);
    }

    [Fact]
    public async Task CI_and_the_fetch_source_answer_for_the_head()
    {
        var (provider, pr, head) = Sandbox();
        var cancel = TestContext.Current.CancellationToken;

        var ci = await provider.GetCiStateAsync(pr, head, cancel);
        var source = await provider.FetchSourceAsync(await provider.GetPullRequestAsync(pr, cancel), cancel);

        Assert.Equal(head, source.HeadRef);
        Assert.NotNull(ci);
    }
}
