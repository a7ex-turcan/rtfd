using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Posting;
using Rtfd.Providers;
using Rtfd.Providers.Bitbucket;

namespace Rtfd.Tests;

/// <summary>
/// Against Bitbucket itself, on a scratch draft PR only (CLAUDE.md rule 4), and only with
/// <c>RTFD_LIVE_BITBUCKET=1</c>. Nothing about the workspace is in this repo: the variables name
/// it. <c>RTFD_BB_SANDBOX</c> is <c>workspace/repository</c>, <c>RTFD_BB_PR</c> the draft PR,
/// whose head adds <c>rtfd-sandbox/Counter.cs</c> with at least ten lines; <c>BB_TOKEN</c> is
/// <c>email:token</c>. Each run adds comments to that PR; the vote test takes its votes back.
/// </summary>
public sealed class BitbucketLiveTests
{
    private const string File = "rtfd-sandbox/Counter.cs";

    private static (BitbucketProvider Provider, int Pr, string Head) Sandbox()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RTFD_LIVE_BITBUCKET") == "1", "set RTFD_LIVE_BITBUCKET=1 to post to the scratch PR");
        var slug = Environment.GetEnvironmentVariable("RTFD_BB_SANDBOX");
        var pr = Environment.GetEnvironmentVariable("RTFD_BB_PR");
        Assert.SkipWhen(string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(pr), "RTFD_BB_SANDBOX and RTFD_BB_PR name the scratch PR");

        var provider = new BitbucketProvider(slug!, new EnvironmentTokenSource("BB_TOKEN", Environment.GetEnvironmentVariable));
        var number = int.Parse(pr!, System.Globalization.CultureInfo.InvariantCulture);
        var head = provider.GetPullRequestAsync(number, TestContext.Current.CancellationToken).GetAwaiter().GetResult().HeadSha;
        return (provider, number, head);
    }

    [Fact]
    public async Task A_review_posted_a_comment_at_a_time_reads_back_with_the_same_hashes()
    {
        var (provider, pr, head) = Sandbox();
        var cancel = TestContext.Current.CancellationToken;
        var stamp = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var me = await provider.WhoAmIAsync(cancel);

        var summaryBody = $"Live test summary {stamp}.\n\n{Poster.Marker(head, null, 1, ProviderKind.Bitbucket)}";
        var summary = await provider.StartThreadAsync(new ThreadPost(pr, head, summaryBody, null, null), cancel);
        var inlineBody = $"Should: live test comment {stamp}.";
        var inline = await provider.StartThreadAsync(new ThreadPost(pr, head, inlineBody, new ReviewPostComment(File, 3, DiffSide.New, inlineBody), summary.Id), cancel);

        var review = await provider.GetReviewAsync(pr, summary.Id, cancel);
        Assert.NotNull(review);
        Assert.Equal(me, review.Author);
        Assert.Equal(Poster.Hash(summaryBody), Poster.Hash(review.Body));
        Assert.Equal(head, Poster.ParseMarker(review.Body)?.Head);
        var comment = Assert.Single(review.Comments, c => c.Id == inline.Id);
        Assert.Equal((File, Poster.Hash(inlineBody)), (comment.Path, Poster.Hash(comment.Body)));

        var listed = await provider.ListReviewCommentsAsync(pr, cancel);
        Assert.Equal(summary.Id, Assert.Single(listed, c => c.Id == inline.Id).ReviewId);
        Assert.Contains(await provider.ListReviewsAsync(pr, cancel), r => r.Id == summary.Id);

        var thread = Assert.Single(await provider.GetReviewThreadsAsync(pr, cancel), t => t.Id == inline.Id);
        Assert.Equal((3, false, false), (thread.Line, thread.IsResolved, thread.IsOutdated));
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
        Assert.Equal(await provider.WhoAmIAsync(cancel), thread.ResolvedBy);
        Assert.Equal([root.Id, reply.Id], thread.Comments.Select(c => c.Id));
        Assert.Equal(Poster.Hash("Looks good now."), Poster.Hash(thread.Comments[1].Body));
    }

    /// <summary>Bitbucket takes a vote on a draft, and on your own PR; this one takes each back at once.</summary>
    [Fact]
    public async Task A_vote_reads_back_as_the_review_s_state_and_is_taken_back()
    {
        var (provider, pr, head) = Sandbox();
        var cancel = TestContext.Current.CancellationToken;
        var summary = await provider.StartThreadAsync(new ThreadPost(pr, head, $"Live test vote {DateTimeOffset.UtcNow:O}.", null, null), cancel);
        try
        {
            await provider.SetVoteAsync(pr, Verdict.Approve, cancel);
            await provider.SetVoteAsync(pr, Verdict.Approve, cancel);
            Assert.Equal("APPROVED", (await provider.GetReviewAsync(pr, summary.Id, cancel))!.State);

            await provider.SetVoteAsync(pr, Verdict.RequestChanges, cancel);
            Assert.Equal("CHANGES_REQUESTED", (await provider.GetReviewAsync(pr, summary.Id, cancel))!.State);
        }
        finally
        {
            await provider.RemoveVoteAsync(pr, cancel);
        }

        Assert.Equal("COMMENTED", (await provider.GetReviewAsync(pr, summary.Id, cancel))!.State);
    }

    [Fact]
    public async Task CI_and_the_fetch_source_answer_for_the_head()
    {
        var (provider, pr, head) = Sandbox();
        var cancel = TestContext.Current.CancellationToken;

        var ci = await provider.GetCiStateAsync(pr, head, cancel);
        var source = await provider.FetchSourceAsync(await provider.GetPullRequestAsync(pr, cancel), cancel);

        Assert.Equal(40, head.Length);
        Assert.Equal(head, source.HeadRef);
        Assert.NotEmpty(ci.Checks);
    }
}
