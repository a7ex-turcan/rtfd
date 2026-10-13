using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Posting;
using Rtfd.Providers;
using Rtfd.Providers.GitHub;

namespace Rtfd.Tests;

/// <summary>
/// Against GitHub itself, on the sandbox repo only (CLAUDE.md rule 4), and only with
/// <c>RTFD_LIVE_GITHUB=1</c> and a token in <c>GH_TOKEN</c>. Each run posts a small COMMENT
/// review to sandbox PR #1, the hostile fixture, whose head adds lines 25 and 26 of
/// tally/counter.py.
/// </summary>
public sealed class GitHubLiveTests
{
    private const string Sandbox = "a7ex-turcan/rtfd-sandbox";

    private static GitHubProvider Provider()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RTFD_LIVE_GITHUB") == "1", "set RTFD_LIVE_GITHUB=1 to post to the sandbox repo");
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        Assert.SkipWhen(string.IsNullOrEmpty(token), "GH_TOKEN isn't set");
        return new GitHubProvider(Sandbox, new ProviderToken(token!));
    }

    [Fact]
    public async Task A_posted_review_reads_back_with_the_same_hashes()
    {
        using var provider = Provider();
        var cancel = TestContext.Current.CancellationToken;
        var pr = await provider.GetPullRequestAsync(1, cancel);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var body = $"MINOR: rtfd's live test checks that a review reads back as sent ({stamp}).\n\nA second paragraph, with `code` and a trailing space. \n\n{Poster.Marker(pr.HeadSha, 0)}";
        ReviewPostComment[] comments =
        [
            new("tally/counter.py", 26, DiffSide.New, $"MINOR: live test comment one ({stamp})."),
            new("tally/counter.py", 25, DiffSide.New, "MINOR: live test comment two.\n\nWith a second paragraph."),
        ];

        var posted = await provider.PostReviewAsync(new ReviewPost(1, pr.HeadSha, Verdict.Comment, body, comments), cancel);
        var read = await provider.GetReviewAsync(1, posted.Id, cancel);
        var listed = await provider.ListReviewsAsync(1, cancel);

        Assert.NotNull(read);
        Assert.Equal("COMMENTED", read.State);
        Assert.Equal(Poster.Hash(body), Poster.Hash(read.Body));
        Assert.Equal(pr.HeadSha, read.CommitId);
        Assert.Equal(comments.Select(c => Poster.Hash(c.Body)).Order(), read.Comments.Select(c => Poster.Hash(c.Body)).Order());
        Assert.Contains(listed, r => r.Id == posted.Id && Poster.Hash(r.Body) == Poster.Hash(body));
        Assert.StartsWith("https://github.com/", posted.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comment_outside_the_diff_is_refused_with_a_422_that_says_why()
    {
        using var provider = Provider();
        var cancel = TestContext.Current.CancellationToken;
        var pr = await provider.GetPullRequestAsync(1, cancel);

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.PostReviewAsync(
            new ReviewPost(1, pr.HeadSha, Verdict.Comment, "MINOR: rtfd's live test; this review must not land.", [new("tally/counter.py", 2, DiffSide.New, "MINOR: outside the diff.")]),
            cancel));

        Assert.Equal(422, error.Status);
        Assert.False(error.MayHaveLanded);
    }
}
