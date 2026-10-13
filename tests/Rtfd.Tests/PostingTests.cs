using System.Text.Json.Nodes;
using Rtfd.Cli;
using Rtfd.Providers;
using Rtfd.State;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// <c>rtfd run</c> without <c>--dry-run</c>, all local (plan Phase 3): the write-ahead record,
/// one review per head, crash recovery by body hash, attempts per commit and <c>retry</c>.
/// </summary>
public sealed class PostingTests : IDisposable
{
    private const string Findings = """
        {
          "schema_version": 1,
          "summary": "The amount parameter needs a guard.",
          "findings": [
            {
              "severity": "should",
              "path": "src/counter.py",
              "line": 6,
              "side": "new",
              "anchor": "self._counts[key] = self._counts.get(key, 0) + amount",
              "body": "Should: a negative amount makes a count go below zero."
            },
            {
              "severity": "minor",
              "path": "src/counter.py",
              "line": 9,
              "side": "new",
              "anchor": "return sum(self._counts.values())",
              "body": "MINOR: total() could say it counts every key."
            }
          ]
        }
        """;

    private const string Clean = """{"schema_version": 1, "summary": "The amount parameter is a good addition.", "findings": []}""";

    private readonly RunHarness _h = new();
    private readonly PullRequest _pr;
    private FakeProvider? _provider;

    public PostingTests()
    {
        _h.Set("RTFD_HOME", _h.Home.Root);
        _h.Git.Commit("Add the counter", new Dictionary<string, string?>
        {
            ["src/counter.py"] = "class Counter:\n    def __init__(self) -> None:\n        self._counts: dict[str, int] = {}\n\n    def add(self, key: str) -> None:\n        self._counts[key] = self._counts.get(key, 0) + 1\n",
        });
        _h.Git.Branch("feature");
        var head = _h.Git.Commit("Count by amount", new Dictionary<string, string?>
        {
            ["src/counter.py"] = "class Counter:\n    def __init__(self) -> None:\n        self._counts: dict[str, int] = {}\n\n    def add(self, key: str, amount: int = 1) -> None:\n        self._counts[key] = self._counts.get(key, 0) + amount\n\n    def total(self) -> int:\n        return sum(self._counts.values())\n",
        });
        _h.Git.Checkout("main");
        _pr = FakeProvider.Pr(head);
    }

    public void Dispose() => _h.Dispose();

    private FakeProvider Provider => _provider ??= new FakeProvider(_h.Git, _pr);

    private (int Code, string Stdout, string Stderr) Run(string command = "run", string repoExtra = "", params string[] extra)
    {
        _h.Home.WriteConfig(_h.Yaml(repoExtra));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var services = new RunServices((_, _) => Provider, "rtfd-test", TimeProvider.System, _ => Task.CompletedTask);
        var ctx = new CommandContext(stdout, stderr, _h.Environment);
        string[] args = ["repo", "--pr", "7", "--config", _h.Home.ConfigPath, .. extra];
        var code = command == "retry" ? RetryCommand.Run(ctx, args) : RunCommand.Run(ctx, args, services);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private StateStore Store() => StateStore.Open(Path.Combine(_h.DataRoot, "state.db"));

    [Fact]
    public void A_live_run_posts_one_review_with_the_marker_and_reads_it_back()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings));

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Ok, code);
        var review = Assert.Single(Provider.Reviews);
        Assert.Equal(_pr.HeadSha, review.CommitId);
        Assert.StartsWith("Not ready: 1 Should open.", review.Body, StringComparison.Ordinal);
        Assert.Matches($"<!-- rtfd sha={_pr.HeadSha} run=\\d+ -->$", review.Body);
        Assert.Equal(2, review.Comments.Count);
        Assert.Contains("Posted the review as COMMENT, with 2 inline comments.", stdout, StringComparison.Ordinal);
        Assert.Contains("Read back from GitHub: the summary, the event and every inline comment are there as sent.", stdout, StringComparison.Ordinal);

        using var store = Store();
        var landed = store.LandedPosting("repo", 7, _pr.HeadSha)!;
        Assert.Equal(("verified", review.Id), (landed.State, landed.ReviewId));
        Assert.All(store.Bodies(landed.Id).Skip(1), b => Assert.NotNull(b.CommentId));
        Assert.Equal(_pr.HeadSha, store.LastReviewedHead("repo", 7));
    }

    [Fact]
    public void A_head_that_was_reviewed_is_not_reviewed_again()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings));
        Run();

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains("is already reviewed", stdout, StringComparison.Ordinal);
        Assert.Equal(1, Provider.PostCalls);
        Assert.Single(_h.Agent.Records);
    }

    [Fact]
    public void A_crash_after_the_post_is_found_by_body_hash_and_nothing_posts_again()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings));
        Provider.Failure = PostFailure.CrashAfterPost;

        Assert.Throws<SimulatedCrash>(() => Run());
        using (var store = Store())
        {
            Assert.Equal("pending", Assert.Single(store.OpenPostings("repo", 7)).State);
        }

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Single(Provider.Reviews);
        Assert.Equal(1, Provider.PostCalls);
        Assert.Single(_h.Agent.Records);
        Assert.Contains("nothing was posted again", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void A_crash_before_the_post_posts_once_at_the_next_run_without_the_agent()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings));
        Provider.Failure = PostFailure.CrashBeforePost;

        Assert.Throws<SimulatedCrash>(() => Run());
        Assert.Empty(Provider.Reviews);

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Single(Provider.Reviews);
        Assert.Equal(2, Provider.PostCalls);
        Assert.Single(_h.Agent.Records);
        Assert.Contains("never landed, so what it recorded was posted now", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void A_502_that_landed_anyway_is_found_straight_away_and_not_posted_twice()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings));
        Provider.Failure = PostFailure.BadGatewayButLanded;

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Single(Provider.Reviews);
        Assert.Equal(1, Provider.PostCalls);
        Assert.Contains("Posted the review as COMMENT", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void A_422_posts_nothing_and_counts_the_run_invalid()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings));
        Provider.Failure = PostFailure.Reject;

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Empty(Provider.Reviews);
        Assert.Contains("Not posted: GitHub refused the review", stdout, StringComparison.Ordinal);
        using var store = Store();
        Assert.Empty(store.OpenPostings("repo", 7));
        Assert.Equal(1, store.Attempts("repo", 7, _pr.HeadSha).Used);
    }

    [Fact]
    public void Used_up_attempts_refuse_the_head_until_retry_gives_them_back()
    {
        _h.Agent.Responds(FakeAgent.Answer("No JSON here."));

        var (first, _, _) = Run(repoExtra: "    max_attempts: 2");
        var (second, refused, _) = Run(repoExtra: "    max_attempts: 2");

        Assert.Equal(EntryPoint.Failure, first);
        Assert.Equal(EntryPoint.Failure, second);
        Assert.Contains("all 2 attempts on", refused, StringComparison.Ordinal);
        Assert.Contains($"rtfd retry repo --pr 7", refused, StringComparison.Ordinal);
        Assert.Equal(2, _h.Agent.Records.Count);

        var (_, retried, _) = Run("retry");
        _h.Agent.Responds(FakeAgent.Findings(Clean));
        var (third, _, _) = Run(repoExtra: "    max_attempts: 2");

        Assert.Contains("gave back 2 attempts", retried, StringComparison.Ordinal);
        Assert.Equal(EntryPoint.Ok, third);
        Assert.Single(Provider.Reviews);
    }

    [Fact]
    public void A_secret_from_outside_the_diff_blocks_the_head_until_retry()
    {
        _h.Agent.Responds(FakeAgent.Findings($$"""{"schema_version": 1, "summary": "Token: {{_h.Token}}", "findings": []}"""));

        var (code, _, _) = Run();
        var (_, blocked, _) = Run();

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Empty(Provider.Reviews);
        Assert.Contains("is blocked: a review of it held a secret that isn't in the diff", blocked, StringComparison.Ordinal);

        var (_, retried, _) = Run("retry");
        Assert.Contains("lifted the block", retried, StringComparison.Ordinal);
    }

    [Fact]
    public void Approving_your_own_PR_posts_a_comment_and_someone_else_s_posts_an_approval()
    {
        _h.Agent.Responds(FakeAgent.Findings(Clean));
        Provider.Identity = "owner";

        var (own, ownOut, _) = Run(repoExtra: "    verdicts: [comment, approve, request_changes]");

        Assert.Equal(EntryPoint.Ok, own);
        Assert.Equal("COMMENTED", Assert.Single(Provider.Reviews).State);
        Assert.Contains("APPROVE downgraded to COMMENT: the posting identity is the PR author", ownOut, StringComparison.Ordinal);

        using var other = new PostingTests();
        other._h.Agent.Responds(FakeAgent.Findings(Clean));
        other.Provider.Identity = "a-reviewer";
        var (_, otherOut, _) = other.Run(repoExtra: "    verdicts: [comment, approve, request_changes]");

        Assert.Equal("APPROVED", Assert.Single(other.Provider.Reviews).State);
        Assert.Contains("Posted the review as APPROVE", otherOut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dry_run_posts_nothing_and_writes_no_state()
    {
        _h.Agent.Responds(FakeAgent.Findings(Findings));

        var (code, stdout, _) = Run(extra: "--dry-run");

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Empty(Provider.Reviews);
        Assert.False(File.Exists(Path.Combine(_h.DataRoot, "state.db")));
        Assert.Contains("Dry run: nothing was posted.", stdout, StringComparison.Ordinal);
    }
}
