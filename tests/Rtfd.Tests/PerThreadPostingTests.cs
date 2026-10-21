using Rtfd.Cli;
using Rtfd.Config;
using Rtfd.Posting;
using Rtfd.Providers;
using Rtfd.State;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// Posting where a review goes a thread at a time, as on Azure DevOps (spec decision 71): the
/// summary thread, then a thread per inline comment naming it, then the vote. A crash part-way
/// through ends with the rest posted, never with anything posted twice.
/// </summary>
public sealed class PerThreadPostingTests : IDisposable
{
    private static readonly string[] Lines =
    [
        "def mean(values):",
        "    return sum(values) / len(values)",
        "",
        "def spread(values):",
        "    return max(values) - min(values)",
        "",
        "def middle(values):",
        "    return sorted(values)[len(values) // 2]",
    ];

    private readonly RunHarness _h = new();
    private readonly PullRequest _pr;
    private FakeProvider? _provider;

    public PerThreadPostingTests()
    {
        _h.Set("RTFD_HOME", _h.Home.Root);
        _h.Set("ADO_TOKEN", Tokens.Random(52));
        _h.Git.Commit("Start", new Dictionary<string, string?> { ["README.md"] = "Stats.\n" });
        _h.Git.Branch("feature");
        var head = _h.Git.Commit("Add stats", new Dictionary<string, string?> { ["src/stats.py"] = string.Join('\n', Lines) + "\n" });
        _h.Git.Checkout("main");
        _pr = FakeProvider.Pr(head) with { Author = "author@contoso.com" };
    }

    public void Dispose() => _h.Dispose();

    private FakeProvider Provider => _provider ??= new FakeProvider(_h.Git, _pr) { Posting = ReviewPosting.PerThread, Identity = "reviewer@contoso.com" };

    /// <summary>One finding per given line of src/stats.py, anchored on the line's text.</summary>
    private static string Findings(params (string Severity, int Line)[] findings)
    {
        var items = findings.Select((f, i) =>
            $$"""{"severity": "{{f.Severity}}", "path": "src/stats.py", "line": {{f.Line}}, "side": "new", "anchor": "{{Lines[f.Line - 1].Trim()}}", "body": "{{char.ToUpperInvariant(f.Severity[0])}}{{f.Severity[1..]}}: finding {{i + 1}}."}""");
        return $$"""{"schema_version": 1, "summary": "Stats need care.", "findings": [{{string.Join(", ", items)}}]}""";
    }

    private (int Code, string Stdout, string Stderr) Run(string repoExtra = "")
    {
        var yaml = _h.Yaml(repoExtra)
            .Replace("provider: github", "provider: azure_devops", StringComparison.Ordinal)
            .Replace("repo: owner/repo", "repo: contoso/Fabrikam Fiber/repo", StringComparison.Ordinal)
            .Replace("token_env: GH_TOKEN", "token_env: ADO_TOKEN", StringComparison.Ordinal)
            .Replace("authors: [owner]", "authors: [author@contoso.com]", StringComparison.Ordinal);
        _h.Home.WriteConfig(yaml);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var services = new RunServices((_, _) => Provider, "rtfd-test", TimeProvider.System, _ => Task.CompletedTask);
        var code = RunCommand.Run(new CommandContext(stdout, stderr, _h.Environment), ["repo", "--pr", "7", "--config", _h.Home.ConfigPath], services);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private StateStore Store() => StateStore.Open(Path.Combine(_h.DataRoot, "state.db"));

    [Fact]
    public void A_review_goes_as_a_summary_thread_then_a_thread_per_comment_that_names_it()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings(("should", 2), ("minor", 8))));

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Ok, code);
        var summary = Assert.Single(Provider.Reviews);
        Assert.StartsWith("Not ready: 1 Should open.", summary.Body, StringComparison.Ordinal);
        Assert.Matches($"<!-- rtfd sha={_pr.HeadSha} base=[0-9a-f]{{40}} run=\\d+ -->$", summary.Body);
        Assert.Equal(3, Provider.ThreadCalls);
        Assert.Equal([(2, summary.Id), (8, summary.Id)], Provider.Comments.Select(c => (c.Line!.Value, c.ReviewId)));
        Assert.Contains("Posted the review as COMMENT, with 2 inline comments.", stdout, StringComparison.Ordinal);
        Assert.Contains("Read back from Azure DevOps: the summary and every inline comment are there as sent.", stdout, StringComparison.Ordinal);

        using var store = Store();
        var landed = store.LandedPosting("repo", 7, _pr.HeadSha)!;
        Assert.Equal(("verified", summary.Id), (landed.State, landed.ReviewId));
        Assert.All(store.Bodies(landed.Id), b => Assert.NotNull(b.CommentId));
        Assert.Equal(2, store.Threads("repo", 7).Count);
    }

    [Fact]
    public void A_crash_after_two_of_five_comments_posts_only_the_other_three()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings(("should", 1), ("could", 2), ("minor", 4), ("minor", 5), ("could", 8))));

        // The summary, then two comments: the second lands and the process dies before rtfd records it.
        Provider.ThreadFailures[3] = PostFailure.CrashAfterPost;
        Assert.Throws<SimulatedCrash>(() => Run());
        Assert.Equal(2, Provider.Comments.Count);

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Single(Provider.Reviews);
        Assert.Equal([1, 2, 4, 5, 8], Provider.Comments.Select(c => c.Line!.Value).Order());
        Assert.Equal(6, Provider.ThreadCalls);
        Assert.Single(_h.Agent.Records);
        Assert.Contains("nothing was posted again", stdout, StringComparison.Ordinal);
        using var store = Store();
        Assert.Equal("verified", store.LandedPosting("repo", 7, _pr.HeadSha)!.State);
        Assert.Equal(5, store.Threads("repo", 7).Count);
    }

    [Fact]
    public void A_comment_that_may_not_have_landed_is_looked_for_before_it_goes_again()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings(("should", 2), ("minor", 8))));
        Provider.ThreadFailures[2] = PostFailure.BadGatewayButLanded;

        var (first, firstOut, _) = Run();

        Assert.Equal(EntryPoint.Failure, first);
        Assert.Contains("may still land", firstOut, StringComparison.Ordinal);
        using (var store = Store())
        {
            Assert.Single(store.OpenPostings("repo", 7));
        }

        var (second, _, _) = Run();

        Assert.Equal(EntryPoint.Ok, second);
        Assert.Equal(2, Provider.Comments.Count);
        Assert.Equal(3, Provider.ThreadCalls);
    }

    [Fact]
    public void A_refused_comment_doesn_t_stop_the_rest_and_the_outcome_says_which()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings(("should", 2), ("minor", 8))));
        Provider.ThreadFailures[2] = PostFailure.Reject;

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Equal([8], Provider.Comments.Select(c => c.Line!.Value));
        Assert.Contains("Read back from Azure DevOps, with problems: Azure DevOps refused the comment on src/stats.py:2", stdout, StringComparison.Ordinal);
        using var store = Store();
        Assert.Equal("unverified", store.LandedPosting("repo", 7, _pr.HeadSha)!.State);
    }

    [Fact]
    public void A_refused_summary_posts_nothing_and_counts_the_run_invalid()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings(("should", 2))));
        Provider.ThreadFailures[1] = PostFailure.Reject;

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Empty(Provider.Comments);
        Assert.Contains("Not posted: Azure DevOps refused the review", stdout, StringComparison.Ordinal);
        using var store = Store();
        Assert.Empty(store.OpenPostings("repo", 7));
        Assert.Equal(1, store.Attempts("repo", 7, _pr.HeadSha).Used);
    }

    [Fact]
    public void The_vote_goes_last_on_someone_else_s_PR_and_never_on_your_own()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings(("should", 2))));

        var (code, stdout, _) = Run(repoExtra: "    verdicts: [comment, approve, request_changes]");

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Equal((Verdict.RequestChanges, 1), (Provider.Vote, Provider.VoteCalls));
        Assert.Contains("Posted the review as REQUEST_CHANGES", stdout, StringComparison.Ordinal);
        Assert.Contains("Read back from Azure DevOps: the summary, the vote and every inline comment are there as sent.", stdout, StringComparison.Ordinal);

        using var own = new PerThreadPostingTests();
        own._h.Agent.Responds(FakeAgent.Findings("""{"schema_version": 1, "summary": "Fine.", "findings": []}"""));
        own.Provider.Identity = "author@contoso.com";
        var (ownCode, ownOut, _) = own.Run(repoExtra: "    verdicts: [comment, approve, request_changes]");

        Assert.Equal(EntryPoint.Ok, ownCode);
        Assert.Equal(0, own.Provider.VoteCalls);
        Assert.Contains("APPROVE downgraded to COMMENT: the posting identity is the PR author", ownOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lost_store_is_rebuilt_from_the_summary_s_marker_and_the_threads_that_name_it()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings(("should", 2), ("minor", 8))));
        Run();

        var threads = await Provider.GetReviewThreadsAsync(7, TestContext.Current.CancellationToken);
        var (last, rows) = await ReviewHistory.RebuildAsync(Provider, 7, "reviewer@contoso.com", threads, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(_pr.HeadSha, last?.Head);
        Assert.Equal([("should", 2), ("minor", 8)], rows.Select(r => (r.Severity, r.Line!.Value)));
    }
}
