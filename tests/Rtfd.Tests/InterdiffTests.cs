using Rtfd.Git;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// The interdiff against real repositories (spec decision 36; plan Phase 4): the old PR diff
/// applied onto the new merge base, then diffed against the new head.
/// </summary>
public sealed class InterdiffTests : IDisposable
{
    private readonly GitFixture _git = new();
    private readonly Mirror _mirror;
    private readonly string _base;
    private readonly string _reviewed;
    private readonly string _reviewedBase;

    public InterdiffTests()
    {
        _base = _git.Commit("Base", new Dictionary<string, string?> { ["app.txt"] = "a\nb\nc\nd\ne\nf\ng\nh\n", ["other.txt"] = "one\n" });
        _git.Branch("feature");
        _reviewed = _git.Commit("PR", new Dictionary<string, string?> { ["app.txt"] = "a\nB from the PR\nc\nd\ne\nf\ng\nh\n", ["pr.txt"] = "new\n" });
        _git.Checkout("main");
        _mirror = Mirror.For(_git.Data, "repo", _git.Git);
        _reviewedBase = Fetch("feature").MergeBase;
    }

    public void Dispose() => _git.Dispose();

    private ReviewCommits Fetch(string branch) =>
        _mirror.FetchAsync(Source(branch), 7, "main", TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private FetchSource Source(string branch) => new(_git.Origin, $"refs/heads/{branch}", null);

    private Task<FollowUpPass> BuildAsync(ReviewCommits commits, string? lastHead = null, string? lastBase = null) =>
        Interdiff.BuildAsync(_git.Git, _mirror, Source("feature"), 7, lastHead ?? _reviewed, lastBase ?? _reviewedBase, commits, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_plain_push_gives_what_the_author_added()
    {
        _git.Checkout("feature");
        _git.Commit("Fix", new Dictionary<string, string?> { ["pr.txt"] = "new\nfix\n" });
        _git.Checkout("main");

        var pass = await BuildAsync(Fetch("feature"));

        var file = Assert.Single(pass.Interdiff!.Files);
        Assert.Equal("pr.txt", file.Path);
        Assert.Equal(1, file.AddedLines);
    }

    [Fact]
    public async Task A_rebase_onto_a_moved_base_still_gets_an_interdiff_from_the_kept_head()
    {
        await _mirror.KeepAsync(7, _reviewed, TestContext.Current.CancellationToken);
        _git.Commit("Base moves", new Dictionary<string, string?> { ["other.txt"] = "one\ntwo\n", ["app.txt"] = "a\nb\nc\nd\ne\nf\ng\nH from main\n" });
        _git.Branch("rebased");
        _git.Commit("PR, rebased, with a change", new Dictionary<string, string?> { ["app.txt"] = "a\nB from the PR\nc\nd\ne\nf\ng\nH from main\n", ["pr.txt"] = "new\nrebased change\n" });
        _git.Checkout("main");

        // The old head is gone from the remote's branches; only the mirror's kept ref holds it.
        _git.Run("branch", "-D", "feature");
        var pass = await BuildAsync(Fetch("rebased"));

        Assert.Null(pass.FullReviewReason);
        var file = Assert.Single(pass.Interdiff!.Files);
        Assert.Equal("pr.txt", file.Path);
        Assert.Contains("+rebased change", file.Patch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changes_that_conflict_with_the_moved_base_fall_back_to_a_full_review()
    {
        _git.Commit("Base edits the PR's line", new Dictionary<string, string?> { ["app.txt"] = "a\nB from main\nc\nd\ne\nf\ng\nh\n" });
        _git.Checkout("feature");
        _git.Run("merge", "--quiet", "--no-edit", "-X", "ours", "main");
        _git.Checkout("main");

        var pass = await BuildAsync(Fetch("feature"));

        Assert.Null(pass.Interdiff);
        Assert.Contains("don't apply cleanly onto the base branch", pass.FullReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_last_head_that_can_t_be_fetched_falls_back_to_a_full_review()
    {
        var gone = new string('0', 39) + "1";

        var pass = await BuildAsync(Fetch("feature"), lastHead: gone, lastBase: null);

        Assert.Null(pass.Interdiff);
        Assert.Contains("can't be fetched any more", pass.FullReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_recorded_merge_base_it_is_worked_out_from_the_old_head()
    {
        _git.Checkout("feature");
        _git.Commit("Fix", new Dictionary<string, string?> { ["pr.txt"] = "new\nfix\n" });
        _git.Checkout("main");

        var pass = await Interdiff.BuildAsync(_git.Git, _mirror, Source("feature"), 7, _reviewed, null, Fetch("feature"), TestContext.Current.CancellationToken);

        Assert.Equal(_base, pass.LastMergeBase);
        Assert.Equal("pr.txt", Assert.Single(pass.Interdiff!.Files).Path);
    }
}
