using System.Globalization;
using System.Text.Json.Nodes;
using Rtfd.Cli;
using Rtfd.Config;
using Rtfd.Providers;
using Rtfd.Serve;
using Rtfd.State;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// <c>rtfd serve</c>, all local (plan Phase 5): the quiet period and the CI wait, one review per
/// head, usage-limit pauses across every repo on an account, pause and resume, a fatal stop,
/// backoff, the startup sweep and crash recovery, the locks, and <c>rtfd status</c>.
/// </summary>
public sealed class ServeTests : IDisposable
{
    private const string FilePath = "src/counter.py";

    private const string Base = "class Counter:\n    def __init__(self) -> None:\n        self._counts: dict[str, int] = {}\n\n    def add(self, key: str) -> None:\n        self._counts[key] = self._counts.get(key, 0) + 1\n";

    private const string First = "class Counter:\n    def __init__(self) -> None:\n        self._counts: dict[str, int] = {}\n\n    def add(self, key: str, amount: int = 1) -> None:\n        self._counts[key] = self._counts.get(key, 0) + amount\n";

    private const string Clean = """{"schema_version": 1, "summary": "The amount parameter reads well.", "findings": []}""";

    /// <summary>Short waits, so a test moves the clock by minutes.</summary>
    private const string Timing = "    interval: 1m\n    quiet_period: 1m\n    ci_max_wait: 5m";

    private const string Other = """
          - name: other
            provider: github
            repo: owner/other
            trust: trusted
            agent: claude
            prompt: prompts/review.md
            authors: [owner]
            token_env: GH_TOKEN
            interval: 1m
            quiet_period: 1m
        """;

    private readonly RunHarness _h = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly Dictionary<string, FakeProvider> _providers = [];
    private readonly StringWriter _console = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private StateStore? _store;
    private int _pushes;

    public ServeTests()
    {
        _h.Set("RTFD_HOME", _h.Home.Root);
        _h.Git.Commit("Add the counter", new Dictionary<string, string?> { [FilePath] = Base });
        _h.Git.Branch("feature");
        var head = _h.Git.Commit("Count by amount", new Dictionary<string, string?> { [FilePath] = First });
        _h.Git.Checkout("main");
        _providers["repo"] = new FakeProvider(_h.Git, FakeProvider.Pr(head)) { Identity = "owner" };
        _providers["other"] = new FakeProvider(_h.Git, FakeProvider.Pr(head)) { Identity = "owner" };
    }

    public void Dispose()
    {
        _store?.Dispose();
        _h.Dispose();
    }

    private FakeProvider Provider => _providers["repo"];

    private StateStore Store => _store ??= StateStore.Open(Path.Combine(_h.DataRoot, "state.db"));

    private string Log => _console.ToString();

    /// <summary>The repo on Azure DevOps, its token from the Azure CLI.</summary>
    private static string AzureCli(string yaml) => yaml
        .Replace("provider: github", "provider: azure_devops", StringComparison.Ordinal)
        .Replace("repo: owner/repo", "repo: contoso/Fabrikam Fiber/repo", StringComparison.Ordinal)
        .Replace("token_env: GH_TOKEN", "auth: azure_cli\n    tenant: contoso.onmicrosoft.com", StringComparison.Ordinal);

    private RtfdConfig Config(string repoExtra = Timing, Func<string, string>? change = null)
    {
        _h.Home.WriteConfig((change ?? (y => y))(_h.Yaml(repoExtra)));
        var loaded = ConfigLoader.LoadFile(_h.Home.ConfigPath, _h.Home.Home);
        Assert.False(loaded.HasErrors, string.Join("\n", loaded.Diagnostics));
        return loaded.Config!;
    }

    private ServeHost Host(string repoExtra = Timing, Func<string, string>? change = null, TokenSources? tokens = null)
    {
        var config = Config(repoExtra, change);
        return new ServeHost(new ServeContext
        {
            Config = config,
            Store = Store,
            Environment = _h.Environment,
            Tokens = tokens ?? TestTokens.From(_h.Environment),
            Providers = (repo, source) =>
            {
                _providers[repo.Name].Source = source;
                return _providers[repo.Name];
            },
            RtfdExecutable = "rtfd-test",
            Log = new ServeLog(config.Data.LogsDirectory, _clock, _console),
            Clock = _clock,
            Zone = TimeZoneInfo.Utc,
            RunDelay = (_, _) => Task.CompletedTask,
        });
    }

    private async Task<ServeHost> StartedAsync(string repoExtra = Timing, Func<string, string>? change = null, TokenSources? tokens = null)
    {
        var host = Host(repoExtra, change, tokens);
        await host.StartAsync(_ct);
        return host;
    }

    /// <summary>A push to the PR branch; every provider's PR moves to the new head.</summary>
    private string Push()
    {
        _h.Git.Checkout("feature");
        var head = _h.Git.Commit($"Push {++_pushes}", new Dictionary<string, string?> { [FilePath] = First + $"\n# push {_pushes}\n" });
        _h.Git.Checkout("main");
        foreach (var provider in _providers.Values)
        {
            provider.PullRequest = provider.PullRequest with { HeadSha = head };
        }

        return head;
    }

    private (int Code, string Stdout, string Stderr) Command(Func<CommandContext, string[], int> run, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = run(new CommandContext(stdout, stderr, _h.Environment), [.. args, "--config", _h.Home.ConfigPath]);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Code, string Stdout, string Stderr) Status() => Command((ctx, args) => StatusCommand.Run(ctx, args, _clock));

    [Fact]
    public async Task Five_pushes_inside_the_quiet_period_produce_one_review()
    {
        using var host = await StartedAsync();
        var watcher = host.Watchers[0];
        _h.Agent.Responds(FakeAgent.Findings(Clean));

        await watcher.TickAsync(_ct);
        string head = Provider.PullRequest.HeadSha;
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            head = Push();
            await watcher.TickAsync(_ct);
        }

        Assert.Empty(Provider.Reviews);
        Assert.Empty(_h.Agent.Records);
        Assert.Contains("quiet for", Log);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await watcher.TickAsync(_ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await watcher.TickAsync(_ct);

        var review = Assert.Single(Provider.Reviews);
        Assert.Equal(head, review.CommitId);
        Assert.Single(_h.Agent.Records);
        Assert.Contains("repo: 1 open PR, nothing to do (#7 reviewed)", Log);

        // A log line for every poll: eight ticks, eight polls.
        Assert.Equal(8, Provider.ListCalls);
        Assert.Equal(8, Log.Split('\n').Count(l => l.Contains(" repo: 1 open PR", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_head_waits_for_CI_to_settle()
    {
        Provider.Ci = new(Providers.CiSummary.Pending, [new("build", "in_progress", null, null)]);
        using var host = await StartedAsync();
        var watcher = host.Watchers[0];
        _h.Agent.Responds(FakeAgent.Findings(Clean));

        await watcher.TickAsync(_ct);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await watcher.TickAsync(_ct);
        Assert.Contains("#7 waiting for CI, up to 4m more", Log);
        Assert.Empty(Provider.Reviews);

        Provider.Ci = new(Providers.CiSummary.Success, [new("build", "completed", "success", null)]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await watcher.TickAsync(_ct);

        var review = Assert.Single(Provider.Reviews);
        Assert.DoesNotContain("CI hadn't finished", review.Body);
    }

    [Fact]
    public async Task CI_still_running_after_ci_max_wait_doesnt_block_the_review_and_the_summary_says_so()
    {
        Provider.Ci = new(Providers.CiSummary.Pending, [new("build", "in_progress", null, null)]);
        using var host = await StartedAsync();
        var watcher = host.Watchers[0];
        _h.Agent.Responds(FakeAgent.Findings(Clean));

        await watcher.TickAsync(_ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await watcher.TickAsync(_ct);

        Assert.Contains("#7 due (CI still running after ci_max_wait)", Log);
        Assert.Contains("CI hadn't finished when I reviewed this.", Assert.Single(Provider.Reviews).Body);
    }

    [Fact]
    public async Task A_CI_result_on_a_reviewed_head_triggers_nothing()
    {
        using var host = await StartedAsync();
        var watcher = host.Watchers[0];
        _h.Agent.Responds(FakeAgent.Findings(Clean));
        await watcher.TickAsync(_ct);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await watcher.TickAsync(_ct);
        Assert.Single(Provider.Reviews);

        Provider.Ci = new(Providers.CiSummary.Failure, [new("build", "completed", "failure", null)]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await watcher.TickAsync(_ct);

        Assert.Single(Provider.Reviews);
        Assert.Single(_h.Agent.Records);
    }

    [Fact]
    public async Task A_draft_is_skipped_until_it_is_ready()
    {
        Provider.PullRequest = Provider.PullRequest with { IsDraft = true };
        using var host = await StartedAsync();
        var watcher = host.Watchers[0];
        _h.Agent.Responds(FakeAgent.Findings(Clean));

        await watcher.TickAsync(_ct);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await watcher.TickAsync(_ct);
        Assert.Single(Log.Split('\n'), l => l.Contains("#7: skipped: it's a draft", StringComparison.Ordinal));

        // Only a push resets the quiet period, and the last one was two minutes ago.
        Provider.PullRequest = Provider.PullRequest with { IsDraft = false };
        await watcher.TickAsync(_ct);
        Assert.Contains("#7 is ready for review", Log);
        Assert.Single(Provider.Reviews);
    }

    [Fact]
    public async Task A_usage_limit_pauses_every_repo_on_the_account_with_attempts_unchanged_and_resumes_on_its_own()
    {
        var reset = new DateTimeOffset(_clock.Now.Year, _clock.Now.Month, _clock.Now.Day, _clock.Now.Hour, 0, 0, TimeSpan.Zero).AddHours(2);
        var message = $"You've hit your session limit · resets {reset.ToString("h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant()} (UTC)";
        _h.Agent.Responds(new JsonObject { ["result"] = message, ["is_error"] = true, ["exit"] = 1 });
        using var host = await StartedAsync(Timing + "\n" + Other);
        var (repo, other) = (host.Watchers[0], host.Watchers[1]);
        await repo.TickAsync(_ct);
        await other.TickAsync(_ct);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await repo.TickAsync(_ct);
        await other.TickAsync(_ct);

        Assert.Single(_h.Agent.Records);
        Assert.Empty(Provider.Reviews);
        Assert.Empty(_providers["other"].Reviews);
        var pause = Store.AccountPaused("test-account", _clock.Now)!;
        Assert.Equal(reset, pause.Until);
        Assert.Equal(0, Store.Attempts("repo", 7, Provider.PullRequest.HeadSha).Used);
        Assert.Contains("test-account hit a usage limit", Log);
        Assert.Contains("#7 due, but test-account is paused until", Log);

        host.WriteStatus(stoppedAt: null);
        var status = StatusFile.Read(Path.Combine(_h.DataRoot, "status.json"))!;
        Assert.Equal("test-account", Assert.Single(status.Accounts).Name);
        Assert.Equal("account_paused", Assert.Single(status.Repos[1].Prs).State);
        Assert.Contains("test-account: paused until", Status().Stdout);

        // Past the reset: both repos go on, without anyone's say.
        _clock.Now = reset.AddMinutes(1);
        _h.Agent.Responds(FakeAgent.Findings(Clean));
        await repo.TickAsync(_ct);
        await other.TickAsync(_ct);

        Assert.Single(Provider.Reviews);
        Assert.Single(_providers["other"].Reviews);
        Assert.Null(Store.AccountPaused("test-account", _clock.Now));
    }

    [Fact]
    public async Task Pause_and_resume_take_effect_at_the_next_tick()
    {
        using var host = await StartedAsync();
        var watcher = host.Watchers[0];
        await watcher.TickAsync(_ct);

        var (code, stdout, _) = Command((ctx, args) => PauseCommand.Run(ctx, args, pause: true, _clock), "repo");
        Assert.Equal(EntryPoint.Ok, code);
        Assert.Contains("repo: paused", stdout);

        await watcher.TickAsync(_ct);
        Assert.Equal(1, Provider.ListCalls);
        Assert.Contains("repo: paused; `rtfd resume repo` watches it again.", Log);
        Assert.Equal("paused", watcher.Status.State);

        (code, stdout, _) = Command((ctx, args) => PauseCommand.Run(ctx, args, pause: false, _clock), "repo");
        Assert.Contains("lifted the pause", stdout);
        await watcher.TickAsync(_ct);
        Assert.Equal(2, Provider.ListCalls);
        Assert.Equal("watching", watcher.Status.State);
    }

    [Fact]
    public async Task A_fatal_agent_outcome_stops_the_repo_until_resume()
    {
        _h.Agent.Responds(new JsonObject { ["stdout"] = "", ["stderr"] = "Invalid API key · Please run /login", ["exit"] = 1 });
        using var host = await StartedAsync();
        var watcher = host.Watchers[0];

        await watcher.TickAsync(_ct);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await watcher.TickAsync(_ct);
        Assert.Contains("isn't logged in", Store.Flags("repo").StoppedReason);

        await watcher.TickAsync(_ct);
        Assert.Equal("stopped", watcher.Status.State);
        Assert.Contains("Fix it, then `rtfd resume repo`", Log);
        Assert.Single(_h.Agent.Records);

        var (_, resumed, _) = Command((ctx, args) => PauseCommand.Run(ctx, args, pause: false, _clock), "repo");
        Assert.Contains("lifted the stop", resumed);
        _h.Agent.Responds(FakeAgent.Findings(Clean));
        await watcher.TickAsync(_ct);
        Assert.Single(Provider.Reviews);
    }

    [Fact]
    public async Task A_transient_failure_backs_off_and_tries_again()
    {
        using var host = await StartedAsync();
        var watcher = host.Watchers[0];
        _h.Agent.Responds(FakeAgent.Findings(Clean));
        Provider.PullRequestFailures = 1;

        await watcher.TickAsync(_ct);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await watcher.TickAsync(_ct);
        Assert.Contains("GitHub answered 502", Log);
        Assert.Contains("Trying again in 1m", Log);

        await watcher.TickAsync(_ct);
        Assert.Contains("#7 trying again in", Log);
        Assert.Empty(Provider.Reviews);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await watcher.TickAsync(_ct);
        Assert.Single(Provider.Reviews);
    }

    [Fact]
    public async Task A_restart_after_a_kill_mid_post_sweeps_the_leftovers_and_posts_no_second_review()
    {
        // The first serve dies right after GitHub took the review, before rtfd recorded it.
        using (var first = await StartedAsync())
        {
            _h.Agent.Responds(FakeAgent.Findings(Clean));
            Provider.Failure = PostFailure.CrashAfterPost;
            await first.Watchers[0].TickAsync(_ct);
            _clock.Advance(TimeSpan.FromSeconds(61));
            await first.Watchers[0].TickAsync(_ct);
        }

        Assert.Single(Provider.Reviews);
        Assert.Single(Store.OpenPostings("repo", 7));

        // What a kill mid-review leaves (besides the crashed run above): a run that never finished, a run
        // folder and a worktree, one kept on purpose, and another repo's.
        Store.StartRun("repo", 7, Provider.PullRequest.HeadSha, null, _clock.Now);
        var runs = Path.Combine(_h.DataRoot, "runs");
        var worktrees = Path.Combine(_h.DataRoot, "worktrees");
        Directory.CreateDirectory(Path.Combine(runs, "repo-7-abcdef12-1", "inputs"));
        Directory.CreateDirectory(Path.Combine(worktrees, "repo-7-abcdef12-1", "src"));
        Directory.CreateDirectory(Path.Combine(runs, "repo-7-abcdef12-2"));
        File.WriteAllText(Path.Combine(runs, "repo-7-abcdef12-2", ".kept"), "");
        Directory.CreateDirectory(Path.Combine(runs, "other-7-abcdef12-1"));

        using var second = await StartedAsync();
        await second.Watchers[0].TickAsync(_ct);

        Assert.Single(Provider.Reviews);
        Assert.Single(_h.Agent.Records);
        Assert.Empty(Store.OpenPostings("repo", 7));
        Assert.False(Directory.Exists(Path.Combine(runs, "repo-7-abcdef12-1")));
        Assert.False(Directory.Exists(Path.Combine(worktrees, "repo-7-abcdef12-1")));
        Assert.True(Directory.Exists(Path.Combine(runs, "repo-7-abcdef12-2")));
        Assert.True(Directory.Exists(Path.Combine(runs, "other-7-abcdef12-1")));
        Assert.Contains("deleted 2 folders a stopped run left behind", Log);
        Assert.Contains("marked 2 runs a stopped serve or `rtfd run` never finished as abandoned", Log);
        Assert.Equal("abandoned", Store.LastRuns("repo").Single().Outcome);
        Assert.Contains("the review an earlier run posted is on the PR; nothing posted again", Log);
    }

    [Fact]
    public async Task An_Azure_CLI_without_a_token_keeps_the_repo_trying_until_someone_logs_in()
    {
        var loggedIn = false;
        var tokens = TestTokens.From(_h.Environment, tenant => new AzureCliTokenSource(tenant, _clock, (_, _) => Task.FromResult(loggedIn
            ? new AzureCliAnswer(0, $$"""{"accessToken": "eyJ{{Tokens.Random(20)}}.{{Tokens.Random(20)}}.{{Tokens.Random(20)}}", "expires_on": {{(_clock.Now + TimeSpan.FromHours(1)).ToUnixTimeSeconds()}}}""", "")
            : new AzureCliAnswer(1, "", "ERROR: Please run 'az login' to setup account."))));
        using var host = await StartedAsync(change: AzureCli, tokens: tokens);
        var watcher = host.Watchers[0];
        Assert.Contains("repo: waiting for a token: the Azure CLI (tenant contoso.onmicrosoft.com) gave no token", Log);

        await watcher.TickAsync(_ct);

        Assert.Equal("watching", watcher.Status.State);
        Assert.Contains("repo: the poll failed: the Azure CLI (tenant contoso.onmicrosoft.com) gave no token: ERROR: Please run 'az login' to setup account.", Log);
        Assert.Equal(0, Provider.ListCalls);

        loggedIn = true;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await watcher.TickAsync(_ct);

        Assert.Equal(1, Provider.ListCalls);
        Assert.Contains("repo: 1 open PR", Log);
    }

    [Fact]
    public async Task A_closed_PR_is_dropped()
    {
        using var host = await StartedAsync();
        var watcher = host.Watchers[0];
        await watcher.TickAsync(_ct);

        Provider.Closed = true;
        await watcher.TickAsync(_ct);

        Assert.Contains("#7 was closed or merged", Log);
        Assert.Contains("repo: no open PRs", Log);
        Assert.Empty(watcher.Detector.Open);
    }

    [Fact]
    public void A_second_serve_on_the_same_data_directory_refuses()
    {
        var config = Config();
        using var held = FileLock.TryAcquire(config.Data.ServeLockPath);
        Assert.NotNull(held);

        var (code, _, stderr) = Command((ctx, args) => ServeCommand.Run(ctx, args, new ServeServices((repo, _) => _providers[repo.Name], "rtfd-test", _clock)));

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains("rtfd serve: already running for", stderr);
    }

    [Fact]
    public void A_manual_run_refuses_a_repo_serve_is_working_on()
    {
        var config = Config();
        using var held = FileLock.TryAcquire(config.Data.RepoLockPath("repo"));

        var services = new RunServices((repo, _) => _providers[repo.Name], "rtfd-test", _clock, _ => Task.CompletedTask, LockWait: TimeSpan.Zero);
        var (code, _, stderr) = Command((ctx, args) => RunCommand.Run(ctx, args, services), "repo", "--pr", "7", "--dry-run");

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains("another rtfd is working on repo right now", stderr);
        Assert.Empty(_h.Agent.Records);
    }

    [Fact]
    public async Task A_tick_waits_for_a_manual_run_holding_the_repo()
    {
        using var host = await StartedAsync();
        using (FileLock.TryAcquire(Path.Combine(_h.DataRoot, "locks", "repo.lock")))
        {
            var next = await host.Watchers[0].TickAsync(_ct);
            Assert.Equal(_clock.Now + TimeSpan.FromMinutes(1), next);
        }

        Assert.Equal(0, Provider.ListCalls);
        Assert.Contains("an `rtfd run` is working on it", Log);
    }

    [Fact]
    public async Task Status_says_running_then_stopped_for_a_real_serve()
    {
        _h.Agent.Responds(FakeAgent.Findings(Clean));
        Config("    interval: 1s\n    quiet_period: 0s");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var services = new ServeServices((repo, _) => _providers[repo.Name], "rtfd-test", TimeProvider.System)
        {
            Stop = stop.Token,
            Heartbeat = TimeSpan.FromMilliseconds(100),
        };
        var serve = Task.Run(() => Command((ctx, args) => ServeCommand.Run(ctx, args, services)), _ct);

        var statusPath = Path.Combine(_h.DataRoot, "status.json");
        for (var i = 0; i < 300 && StatusFile.Read(statusPath)?.Repos.SingleOrDefault()?.Prs.SingleOrDefault()?.State != "reviewed"; i++)
        {
            await Task.Delay(100, _ct);
        }

        var (running, runningOut, _) = Command((ctx, args) => StatusCommand.Run(ctx, args));
        Assert.True(running == EntryPoint.Ok, runningOut);
        Assert.Contains("rtfd serve is running: pid", runningOut);
        Assert.Contains("#7  Count things faster · reviewed", runningOut);

        await stop.CancelAsync();
        var (code, _, stderr) = await serve;
        Assert.True(code == EntryPoint.Ok, stderr);
        Assert.Single(Provider.Reviews);

        var (stopped, stoppedOut, _) = Command((ctx, args) => StatusCommand.Run(ctx, args));
        Assert.Equal(EntryPoint.Failure, stopped);
        Assert.Contains("rtfd serve is not running. It stopped at", stoppedOut);
        Assert.Contains("#7  reviewed", stoppedOut);
    }

    [Fact]
    public void Status_says_not_responding_when_serve_holds_the_lock_with_an_old_heartbeat()
    {
        var config = Config();
        var old = StateStore.Time(_clock.Now.AddMinutes(-10));
        StatusFile.Write(config.Data.StatusPath, new ServeStatus(1, 4242, "0.1.0", config.FilePath, old, old, null, false, [], []));

        using (FileLock.TryAcquire(config.Data.ServeLockPath))
        {
            var (code, stdout, _) = Status();
            Assert.Equal(EntryPoint.Failure, code);
            Assert.Contains("rtfd serve is not responding: pid 4242 holds the lock, but its last heartbeat was 10m ago", stdout);
        }

        var (_, killed, _) = Status();
        Assert.Contains("rtfd serve is not running. It last wrote its status 10m ago and didn't stop cleanly", killed);
    }

    [Fact]
    public void Status_without_a_serve_or_a_store_says_so()
    {
        Config();

        var (code, stdout, _) = Status();

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains("rtfd serve is not running.", stdout);
        Assert.Contains("repo  owner/repo · claude (test-account) · every 1m", stdout);
        Assert.Contains("nothing reviewed yet", stdout);
    }
}
