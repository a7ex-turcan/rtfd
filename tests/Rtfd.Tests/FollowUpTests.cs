using System.Text.Json.Nodes;
using Rtfd.Cli;
using Rtfd.Providers;
using Rtfd.State;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// Follow-ups, all local (plan Phase 4): the interdiff, resolving and replying on rtfd's own
/// threads, the verdict counting what is still open, hand resolves reported once, a lost store
/// rebuilt from the review markers, and the own-threads rule.
/// </summary>
public sealed class FollowUpTests : IDisposable
{
    private const string Path = "src/counter.py";

    private const string Base = "class Counter:\n    def __init__(self) -> None:\n        self._counts: dict[str, int] = {}\n\n    def add(self, key: str) -> None:\n        self._counts[key] = self._counts.get(key, 0) + 1\n";

    // Line 6 takes any amount, line 12 raises KeyError: the two Shoulds of the first review.
    private const string First = "class Counter:\n    def __init__(self) -> None:\n        self._counts: dict[str, int] = {}\n\n    def add(self, key: str, amount: int = 1) -> None:\n        self._counts[key] = self._counts.get(key, 0) + amount\n\n    def total(self) -> int:\n        return sum(self._counts.values())\n\n    def remove(self, key: str) -> None:\n        del self._counts[key]\n";

    private const string RemoveFixed = "class Counter:\n    def __init__(self) -> None:\n        self._counts: dict[str, int] = {}\n\n    def add(self, key: str, amount: int = 1) -> None:\n        self._counts[key] = self._counts.get(key, 0) + amount\n\n    def total(self) -> int:\n        return sum(self._counts.values())\n\n    def remove(self, key: str) -> None:\n        self._counts.pop(key, None)\n";

    private const string TwoShoulds = """
        {
          "schema_version": 1,
          "summary": "Two things to fix.",
          "findings": [
            {"severity": "should", "path": "src/counter.py", "line": 6, "side": "new", "anchor": "self._counts.get(key, 0) + amount", "body": "Should: a negative amount makes a count go below zero."},
            {"severity": "should", "path": "src/counter.py", "line": 12, "side": "new", "anchor": "del self._counts[key]", "body": "Should: remove() raises KeyError for a key that was never added."}
          ]
        }
        """;

    private readonly RunHarness _h = new();
    private FakeProvider? _provider;

    public FollowUpTests()
    {
        _h.Set("RTFD_HOME", _h.Home.Root);
        _h.Git.Commit("Add the counter", new Dictionary<string, string?> { [Path] = Base });
        _h.Git.Branch("feature");
        var head = _h.Git.Commit("Count by amount, total, remove", new Dictionary<string, string?> { [Path] = First });
        _h.Git.Checkout("main");
        _provider = new FakeProvider(_h.Git, FakeProvider.Pr(head)) { Identity = "owner" };
    }

    public void Dispose() => _h.Dispose();

    private FakeProvider Provider => _provider!;

    private (int Code, string Stdout, string Stderr) Run(string repoExtra = "", params string[] extra)
    {
        _h.Home.WriteConfig(_h.Yaml(repoExtra));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var services = new RunServices((_, _) => Provider, "rtfd-test", TimeProvider.System, _ => Task.CompletedTask);
        var code = RunCommand.Run(new CommandContext(stdout, stderr, _h.Environment), ["repo", "--pr", "7", "--config", _h.Home.ConfigPath, .. extra], services);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A push to the PR branch; the provider's PR moves to the new head.</summary>
    private string Push(string message, string content) => Push(message, new Dictionary<string, string?> { [Path] = content });

    private string Push(string message, Dictionary<string, string?> files)
    {
        _h.Git.Checkout("feature");
        var head = _h.Git.Commit(message, files);
        _h.Git.Checkout("main");
        Provider.PullRequest = Provider.PullRequest with { HeadSha = head };
        return head;
    }

    /// <summary>The first review: two Shoulds, on lines 6 and 12. Returns their thread ids.</summary>
    private (string Amount, string Remove) FirstReview()
    {
        _h.Agent.Responds(FakeAgent.Findings(TwoShoulds));
        var (code, stdout, _) = Run();
        Assert.True(code == EntryPoint.Ok, stdout);
        var comments = Provider.Reviews.Single().Comments;
        return (comments.Single(c => c.Line == 6).Id, comments.Single(c => c.Line == 12).Id);
    }

    private static string Answer(string summary, string threads = "[]", string findings = "[]") =>
        $$"""{"schema_version": 1, "summary": "{{summary}}", "findings": {{findings}}, "threads": {{threads}}}""";

    private StateStore Store() => StateStore.Open(System.IO.Path.Combine(_h.DataRoot, "state.db"));

    private JsonObject LastInputs => _h.Agent.Records[^1]["inputs"]!.AsObject();

    [Fact]
    public void A_follow_up_resolves_the_fixed_thread_leaves_the_other_and_stays_not_ready()
    {
        var (amount, remove) = FirstReview();
        var firstHead = Provider.PullRequest.HeadSha;
        Push("Don't raise on an unknown key", RemoveFixed);
        _h.Agent.Responds(FakeAgent.Findings(Answer("The KeyError is fixed.", $$"""[{"id": "{{remove}}", "action": "resolve"}]""")));

        var (code, stdout, _) = Run();

        Assert.True(code == EntryPoint.Ok, stdout);
        var removeThread = Provider.Threads.Single(t => t.RootId == remove);
        var amountThread = Provider.Threads.Single(t => t.RootId == amount);
        Assert.True(removeThread.IsResolved);
        Assert.Equal("Looks good now.", removeThread.Comments[^1].Body);
        Assert.False(amountThread.IsResolved);
        Assert.Single(amountThread.Comments);

        var followUp = Provider.Reviews.Last(r => r.Body.Length > 0);
        Assert.StartsWith("Not ready: 1 Should open.", followUp.Body, StringComparison.Ordinal);
        Assert.Contains("Still open from my earlier review: 1 Should (`src/counter.py:6`).", followUp.Body, StringComparison.Ordinal);
        Assert.Contains("and 1 reply on earlier threads (1 resolved)", stdout, StringComparison.Ordinal);
        Assert.Contains("Read back from GitHub: the summary, the event and every inline comment and reply are there as sent.", stdout, StringComparison.Ordinal);

        // The agent saw only what the author changed, and both threads with their ids.
        var inputs = LastInputs;
        var interdiff = inputs["interdiff.diff"]!.GetValue<string>();
        Assert.Contains("+        self._counts.pop(key, None)", interdiff, StringComparison.Ordinal);
        Assert.DoesNotContain("def total", interdiff, StringComparison.Ordinal);
        Assert.Contains($"\"last_reviewed_head\": \"{firstHead}\"", inputs["pr.json"]!.GetValue<string>(), StringComparison.Ordinal);
        var own = JsonNode.Parse(inputs["threads.json"]!.GetValue<string>())!["own"]!.AsArray();
        Assert.Equal([amount, remove], own.Select(t => t!["id"]!.GetValue<string>()).Order());
        Assert.Contains("## A follow-up", _h.Agent.Records[^1]["stdin"]!.GetValue<string>(), StringComparison.Ordinal);

        using var store = Store();
        Assert.NotNull(store.Threads("repo", 7).Single(t => t.CommentId == remove).ResolvedByRtfd);
        Assert.Null(store.Threads("repo", 7).Single(t => t.CommentId == amount).ResolvedByRtfd);
    }

    [Fact]
    public void Merging_the_base_branch_into_the_PR_brings_none_of_its_changes_into_the_interdiff()
    {
        FirstReview();
        var firstHead = Provider.PullRequest.HeadSha;
        _h.Git.Commit("Elsewhere on main", new Dictionary<string, string?> { ["src/other.py"] = "VALUE = 1\n" });
        _h.Git.Checkout("feature");
        _h.Git.Run("merge", "--quiet", "--no-edit", "main");
        _h.Git.Checkout("main");
        var head = Push("Don't raise on an unknown key", RemoveFixed);
        _h.Agent.Responds(FakeAgent.Findings(Answer("Fixed.")));

        var (code, stdout, _) = Run();

        Assert.True(code == EntryPoint.Ok, stdout);
        var interdiff = LastInputs["interdiff.diff"]!.GetValue<string>();
        Assert.Contains("self._counts.pop(key, None)", interdiff, StringComparison.Ordinal);
        Assert.DoesNotContain("other.py", interdiff, StringComparison.Ordinal);

        // What a plain diff of the two heads would have brought in.
        Assert.Contains("other.py", _h.Git.Run("diff", "--stat", firstHead, head), StringComparison.Ordinal);
    }

    [Fact]
    public void A_push_that_only_merges_the_base_branch_is_not_reviewed_and_the_next_one_starts_there()
    {
        FirstReview();
        _h.Git.Commit("Elsewhere on main", new Dictionary<string, string?> { ["src/other.py"] = "VALUE = 1\n" });
        _h.Git.Checkout("feature");
        _h.Git.Run("merge", "--quiet", "--no-edit", "main");
        var merged = _h.Git.Head();
        _h.Git.Checkout("main");
        Provider.PullRequest = Provider.PullRequest with { HeadSha = merged };

        var (code, stdout, _) = Run();
        var (again, refused, _) = Run();

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Contains("Nothing to review: since the last review", stdout, StringComparison.Ordinal);
        Assert.Single(_h.Agent.Records);
        Assert.Single(Provider.Reviews);
        Assert.Equal(EntryPoint.Failure, again);
        Assert.Contains("is already reviewed", refused, StringComparison.Ordinal);
        using var store = Store();
        Assert.Equal(merged, store.LastReviewed("repo", 7)!.Head);
    }

    [Fact]
    public void A_hand_resolved_Should_is_reported_in_the_next_summary_and_not_the_one_after()
    {
        var (_, remove) = FirstReview();
        Provider.ResolveByHand(remove);
        Push("Document total", First + "\n# total() counts every key.\n");
        _h.Agent.Responds(FakeAgent.Findings(Answer("A comment was added.")));

        Run();
        var second = Provider.Reviews.Last(r => r.Body.Length > 0).Body;
        Push("Document remove", First + "\n# total() counts every key.\n# remove() deletes a key.\n");
        Run();
        var third = Provider.Reviews.Last(r => r.Body.Length > 0).Body;

        Assert.StartsWith("Not ready: 1 Should open.", second, StringComparison.Ordinal);
        Assert.Contains("Closed by hand since my last review: 1 Should (`src/counter.py:12`).", second, StringComparison.Ordinal);
        Assert.StartsWith("Not ready: 1 Should open.", third, StringComparison.Ordinal);
        Assert.DoesNotContain("by hand", third, StringComparison.Ordinal);
        Assert.Equal(0, Provider.ReplyCalls);
    }

    [Fact]
    public void With_human_resolve_closes_off_a_hand_resolved_Should_still_counts_and_can_still_be_resolved()
    {
        var (_, remove) = FirstReview();
        Provider.ResolveByHand(remove);
        Push("Document total", First + "\n# total() counts every key.\n");
        _h.Agent.Responds(FakeAgent.Findings(Answer("A comment was added.")));

        Run("    human_resolve_closes: false");
        var second = Provider.Reviews.Last(r => r.Body.Length > 0).Body;
        var own = JsonNode.Parse(LastInputs["threads.json"]!.GetValue<string>())!["own"]!.AsArray();

        Assert.StartsWith("Not ready: 2 Shoulds open.", second, StringComparison.Ordinal);
        Assert.Contains("Resolved by hand, but it still counts here until it's fixed: 1 Should (`src/counter.py:12`).", second, StringComparison.Ordinal);
        Assert.True(own.Single(t => t!["id"]!.GetValue<string>() == remove)!["resolved_by_hand"]!.GetValue<bool>());

        Push("Don't raise on an unknown key", RemoveFixed + "\n# total() counts every key.\n");
        _h.Agent.Responds(FakeAgent.Findings(Answer("The KeyError is fixed.", $$"""[{"id": "{{remove}}", "action": "resolve"}]""")));
        Run("    human_resolve_closes: false");

        Assert.StartsWith("Not ready: 1 Should open.", Provider.Reviews.Last(r => r.Body.Length > 0).Body, StringComparison.Ordinal);
        Assert.Equal("Looks good now.", Provider.Threads.Single(t => t.RootId == remove).Comments[^1].Body);
        Assert.Equal(0, Provider.ResolveCalls);
    }

    [Fact]
    public void A_lost_store_is_rebuilt_from_the_markers_and_the_follow_up_resolves_correctly()
    {
        var (amount, remove) = FirstReview();
        foreach (var file in Directory.EnumerateFiles(_h.DataRoot, "state.db*"))
        {
            File.Delete(file);
        }

        Push("Don't raise on an unknown key", RemoveFixed);
        _h.Agent.Responds(FakeAgent.Findings(Answer("The KeyError is fixed.", $$"""[{"id": "{{remove}}", "action": "resolve"}]""")));

        var (code, stdout, stderr) = Run();

        Assert.True(code == EntryPoint.Ok, stdout);
        Assert.Contains("rebuilt it from the review markers: last review of", stderr, StringComparison.Ordinal);
        Assert.True(Provider.Threads.Single(t => t.RootId == remove).IsResolved);
        Assert.False(Provider.Threads.Single(t => t.RootId == amount).IsResolved);
        Assert.StartsWith("Not ready: 1 Should open.", Provider.Reviews.Last(r => r.Body.Length > 0).Body, StringComparison.Ordinal);
        Assert.Contains("self._counts.pop(key, None)", LastInputs["interdiff.diff"]!.GetValue<string>(), StringComparison.Ordinal);

        using var store = Store();
        var threads = store.Threads("repo", 7);
        Assert.Equal(["should", "should"], threads.Select(t => t.Severity));
        Assert.Null(threads.Single(t => t.CommentId == remove).PostingId);
    }

    [Fact]
    public void A_lost_store_on_a_head_already_reviewed_posts_nothing_again()
    {
        FirstReview();
        foreach (var file in Directory.EnumerateFiles(_h.DataRoot, "state.db*"))
        {
            File.Delete(file);
        }

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains("is already reviewed", stdout, StringComparison.Ordinal);
        Assert.Single(Provider.Reviews);
        Assert.Single(_h.Agent.Records);
    }

    [Fact]
    public void A_committed_credential_gets_rtfd_s_Should_once_across_follow_ups()
    {
        var fake = Tokens.GitHubLike();
        Push("Add the config", new Dictionary<string, string?> { ["src/config.py"] = $"GITHUB_TOKEN = \"{fake}\"\n" });
        _h.Agent.Responds(FakeAgent.Findings(Answer("Looks fine.")));
        Run();
        Push("Document total", First + "\n# total() counts every key.\n");

        var (code, stdout, _) = Run();

        Assert.True(code == EntryPoint.Ok, stdout);
        var credential = Assert.Single(Provider.Threads);
        Assert.StartsWith("Should: this commits a credential", credential.Comments[0].Body, StringComparison.Ordinal);
        Assert.StartsWith("Not ready: 1 Should open.", Provider.Reviews.Last(r => r.Body.Length > 0).Body, StringComparison.Ordinal);
        Assert.DoesNotContain(fake, string.Concat(Provider.Comments.Select(c => c.Body)), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolving_a_thread_a_person_started_is_refused_by_the_validator()
    {
        var person = Provider.StartThread("a-reviewer", Path, 6, "Please rename amount to count.");
        _h.Agent.Responds(FakeAgent.Findings(Answer("Fine.", $$"""[{"id": "{{person}}", "action": "resolve"}]""")));

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains($"{person} isn't one of your threads", stdout, StringComparison.Ordinal);
        Assert.Empty(Provider.Reviews);
        Assert.Equal(0, Provider.ReplyCalls + Provider.ResolveCalls);
        Assert.False(Provider.Threads.Single().IsResolved);
    }

    [Fact]
    public void A_stored_thread_that_another_login_started_is_not_rtfd_s_own()
    {
        var (_, remove) = FirstReview();
        Provider.Identity = "someone-else";
        Push("Don't raise on an unknown key", RemoveFixed);
        _h.Agent.Responds(FakeAgent.Findings(Answer("Fixed.", $$"""[{"id": "{{remove}}", "action": "resolve"}]""")));

        var (code, stdout, _) = Run();

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains($"{remove} isn't one of your threads", stdout, StringComparison.Ordinal);
        Assert.False(Provider.Threads.Single(t => t.RootId == remove).IsResolved);
    }

    [Fact]
    public void A_resolve_on_a_thread_already_resolved_is_dropped()
    {
        var (_, remove) = FirstReview();
        Provider.ResolveByHand(remove);
        Push("Don't raise on an unknown key", RemoveFixed);
        _h.Agent.Responds(FakeAgent.Findings(Answer("Fixed.", $$"""[{"id": "{{remove}}", "action": "resolve"}]""")));

        var (code, stdout, _) = Run();

        Assert.True(code == EntryPoint.Ok, stdout);
        Assert.Contains($"Dropped: thread {remove} (src/counter.py:12) is already resolved, so its resolve was dropped.", stdout, StringComparison.Ordinal);
        Assert.Equal(0, Provider.ReplyCalls + Provider.ResolveCalls);
    }

    [Fact]
    public void A_crash_between_two_replies_is_settled_by_the_next_run_without_replying_twice()
    {
        var (amount, remove) = FirstReview();
        Push("Fix both", RemoveFixed.Replace("self._counts.get(key, 0) + amount", "self._counts.get(key, 0) + max(amount, 0)", StringComparison.Ordinal));
        _h.Agent.Responds(FakeAgent.Findings(Answer("Both fixed.", $$"""[{"id": "{{amount}}", "action": "resolve"}, {"id": "{{remove}}", "action": "resolve"}]""")));
        Provider.ReplyFailures[2] = PostFailure.CrashAfterPost;

        Assert.Throws<SimulatedCrash>(() => Run());
        var (code, stdout, _) = Run();

        Assert.True(code == EntryPoint.Ok, stdout);
        Assert.Equal(2, Provider.ReplyCalls);
        Assert.All(Provider.Threads, t => Assert.True(t.IsResolved));
        Assert.All(Provider.Threads, t => Assert.Equal(2, t.Comments.Count));
        Assert.Equal(2, _h.Agent.Records.Count);
        Assert.StartsWith("Ready to merge from my side.", Provider.Reviews.Last(r => r.Body.Length > 0).Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dry_run_follow_up_reads_the_store_and_writes_nothing()
    {
        var (_, remove) = FirstReview();
        var firstHead = Provider.PullRequest.HeadSha;
        Push("Don't raise on an unknown key", RemoveFixed);
        _h.Agent.Responds(FakeAgent.Findings(Answer("Fixed.", $$"""[{"id": "{{remove}}", "action": "resolve"}]""")));

        var (code, stdout, _) = Run(extra: "--dry-run");

        Assert.True(code == EntryPoint.Ok, stdout);
        Assert.Contains("Follow-up on ", stdout, StringComparison.Ordinal);
        Assert.Contains($"--- thread {remove} (src/counter.py:12, Should): resolve ---", stdout, StringComparison.Ordinal);
        Assert.Equal(0, Provider.ReplyCalls + Provider.ResolveCalls);
        Assert.Single(Provider.Reviews);
        using var store = Store();
        Assert.Null(store.Threads("repo", 7).Single(t => t.CommentId == remove).ResolvedByRtfd);
        Assert.Equal(firstHead, store.LastReviewed("repo", 7)!.Head);
    }

    [Fact]
    public void A_dry_run_without_a_store_rebuilds_in_memory_only()
    {
        FirstReview();
        foreach (var file in Directory.EnumerateFiles(_h.DataRoot, "state.db*"))
        {
            File.Delete(file);
        }

        Push("Don't raise on an unknown key", RemoveFixed);
        _h.Agent.Responds(FakeAgent.Findings(Answer("Fixed.")));

        var (code, stdout, _) = Run(extra: "--dry-run");

        Assert.True(code == EntryPoint.Ok, stdout);
        Assert.Contains("Rebuilt from the review markers: last review of", stdout, StringComparison.Ordinal);
        Assert.Contains("Still open from my earlier review: 2 Shoulds", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(System.IO.Path.Combine(_h.DataRoot, "state.db")));
    }
}
