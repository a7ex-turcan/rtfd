using System.Text.Json.Nodes;
using Rtfd.Agents;
using Rtfd.Cli;
using Rtfd.Config;
using Rtfd.Processes;
using Rtfd.Providers;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>The init event Claude Code 2.1.296 printed under rtfd's flags, as a template.</summary>
internal static class Init
{
    public static JsonObject Clean() => new()
    {
        ["type"] = "system",
        ["subtype"] = "init",
        ["tools"] = new JsonArray("Glob", "Grep", "Read", "mcp__rtfd-git__blame", "mcp__rtfd-git__diff", "mcp__rtfd-git__log", "mcp__rtfd-git__show"),
        ["mcp_servers"] = new JsonArray(new JsonObject { ["name"] = "rtfd-git", ["status"] = "connected", ["source"] = "dynamic" }),
        ["plugins"] = new JsonArray(new JsonObject { ["name"] = "cc-plugin-agents-md", ["path"] = "builtin", ["source"] = "cc-plugin-agents-md@builtin" }),
        ["skills"] = new JsonArray(),
        ["slash_commands"] = new JsonArray(),
        ["claude_code_version"] = "2.1.296",
    };
}

public sealed class AgentProbeTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "rtfd-t", Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeAgent _fake;

    public AgentProbeTests() => _fake = new FakeAgent(Path.Combine(_work, "agent"));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private AgentConfig Agent() => new()
    {
        Name = "claude",
        Path = "agents.claude",
        Kind = AgentKind.Claude,
        Account = "a",
        Command = CommandSplitter.Split(_fake.Command)!,
        Output = BuiltIn.Output(AgentKind.Claude),
        Isolation = new Dictionary<string, IReadOnlyList<string>>(),
        Tools = new Dictionary<string, IReadOnlyList<string>>(),
        ProjectConfig = BuiltIn.ProjectConfig(AgentKind.Claude),
        LimitPatterns = [],
    };

    private string Help() => string.Join("\n", ClaudeAdapter.RenderedFlags(Agent()).Select(f => $"  {f} <value>   something")) + "\n";

    private void Scenario(JsonObject? init, string? help = null)
    {
        var scenario = new JsonObject
        {
            ["version"] = "2.1.296 (Claude Code)",
            ["help"] = help ?? Help(),
            ["record"] = _fake.RecordPath,
            ["responses"] = new JsonArray(new JsonObject { ["stdout"] = "not an init event\n" }),
        };
        if (init is not null)
        {
            scenario["init"] = init;
        }

        File.WriteAllText(_fake.ScenarioPath, scenario.ToJsonString());
    }

    private Task<AgentReport> ProbeAsync() =>
        AgentProbe.RunAsync(Agent(), ChildEnvironment.Build(Environment.GetEnvironmentVariable, []), Path.Combine(_work, "probe"), "rtfd-test", 7,
            cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_expected_tools_pass_and_are_all_reported()
    {
        Scenario(Init.Clean());

        var report = await ProbeAsync();

        Assert.Empty(report.Diagnostics);
        Assert.True(report.Probed);
        Assert.Equal("2.1.296 (Claude Code)", report.Version);
        Assert.Equal(7, report.Tools.Count);
        Assert.Equal("rtfd-git", Assert.Single(report.McpServers).Name);
    }

    [Fact]
    public async Task The_probe_launches_the_agent_as_a_review_would_and_stops_it_at_the_init_event()
    {
        Scenario(Init.Clean());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await ProbeAsync();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(40), $"took {watch.Elapsed}: the agent wasn't stopped");
        Assert.False(Directory.Exists(Path.Combine(_work, "probe", "never")));
    }

    [Fact]
    public async Task A_leaked_tool_server_or_plugin_is_an_error_that_names_it()
    {
        var init = Init.Clean();
        init["tools"]!.AsArray().Add("Bash");
        init["tools"]!.AsArray().Add("mcp__plugin_rtfc_rtfc__send");
        init["mcp_servers"]!.AsArray().Add(new JsonObject { ["name"] = "plugin:rtfc:rtfc", ["status"] = "connected", ["source"] = "plugin" });
        init["plugins"]!.AsArray().Add(new JsonObject { ["name"] = "rtfc", ["source"] = "rtfc@rtfc" });
        init["skills"]!.AsArray().Add("deploy");
        Scenario(init);

        var report = await ProbeAsync();

        var messages = report.Diagnostics.Select(d => $"{d.Check}: {d.Message}").ToList();
        Assert.Contains(messages, m => m.StartsWith("agent.tool_unexpected", StringComparison.Ordinal) && m.Contains("Bash", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("agent.tool_unexpected", StringComparison.Ordinal) && m.Contains("mcp__plugin_rtfc_rtfc__send", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("agent.mcp_unexpected", StringComparison.Ordinal) && m.Contains("plugin:rtfc:rtfc", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("agent.plugin_unexpected", StringComparison.Ordinal) && m.Contains("rtfc@rtfc", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("agent.skills_loaded", StringComparison.Ordinal));
        Assert.All(report.Diagnostics, d => Assert.Equal((DiagnosticLevel.Error, 7, "agents.claude.command"), (d.Level, d.Line, d.Path)));
    }

    [Fact]
    public async Task rtfd_git_not_starting_is_an_error()
    {
        var init = Init.Clean();
        init["tools"] = new JsonArray("Glob", "Grep", "Read");
        init["mcp_servers"] = new JsonArray(new JsonObject { ["name"] = "rtfd-git", ["status"] = "failed", ["source"] = "dynamic" });
        Scenario(init);

        var report = await ProbeAsync();

        Assert.Contains(report.Diagnostics, d => d.Check == "agent.git_tools_failed");
        Assert.Equal(4, report.Diagnostics.Count(d => d.Check == "agent.tool_missing"));
    }

    [Fact]
    public async Task A_CLI_without_a_flag_rtfd_needs_is_refused_before_it_runs()
    {
        Scenario(Init.Clean(), help: Help().Replace("--restricted", "--relaxed", StringComparison.Ordinal));

        var report = await ProbeAsync();

        var error = Assert.Single(report.Diagnostics);
        Assert.Equal("agent.flag_missing", error.Check);
        Assert.Contains("--restricted", error.Message, StringComparison.Ordinal);
        Assert.False(report.Probed);
    }

    [Fact]
    public async Task No_init_event_fails_closed()
    {
        Scenario(init: null);

        var report = await ProbeAsync();

        Assert.Equal("agent.init_unreadable", Assert.Single(report.Diagnostics).Check);
    }

    [Fact]
    public async Task A_binary_that_isn_t_on_the_agent_s_PATH_is_an_error()
    {
        var agent = Agent() with { Command = ["rtfd-no-such-agent", "-p"] };

        var report = await AgentProbe.RunAsync(agent, ChildEnvironment.Build(Environment.GetEnvironmentVariable, []), Path.Combine(_work, "probe"), "rtfd-test", 3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("agent.binary_missing", Assert.Single(report.Diagnostics).Check);
    }

    [Fact]
    public void An_init_event_without_its_lists_is_unreadable_rather_than_empty()
    {
        var init = Init.Clean();
        init.Remove("tools");

        Assert.Null(AgentProbe.ParseInit(Agent(), init.ToJsonString()));
        Assert.Null(AgentProbe.ParseInit(Agent(), """{"type": "assistant"}"""));
        Assert.NotNull(AgentProbe.ParseInit(Agent(), Init.Clean().ToJsonString()));
    }

    [Theory]
    [InlineData("  --tools <tools...>  Specify", "--tools", true)]
    [InlineData("  --allowedTools, --allowed-tools <tools...>", "--tools", false)]
    [InlineData("  --allowedTools, --allowed-tools <tools...>", "--allowedTools", true)]
    [InlineData("  --add-dir <directories...>", "--add-dir", true)]
    [InlineData("  --strict-mcp-config-x", "--strict-mcp-config", false)]
    public void A_flag_counts_only_as_a_word_of_its_own(string help, string flag, bool expected) =>
        Assert.Equal(expected, AgentProbe.HelpMentions(help, flag));
}

public sealed class ProviderChecksTests
{
    private sealed class StubProvider(Func<string> whoami, Func<RepositoryInfo> repository) : IProvider
    {
        public int WhoAmICalls { get; private set; }

        public Task<string> WhoAmIAsync(CancellationToken cancellationToken = default)
        {
            WhoAmICalls++;
            return Task.FromResult(whoami());
        }

        public Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(repository());

        public Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CiState> GetCiStateAsync(string sha, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Git.FetchSource FetchSource(PullRequest pullRequest) => throw new NotSupportedException();

        public Task<PostedReview> PostReviewAsync(ReviewPost post, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PostedReview?> GetReviewAsync(int number, string reviewId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostedReview>> ListReviewsAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static RtfdConfig Config(TempHome home, string authors = "[owner]")
    {
        var second = Configs.Repo.Replace("name: nutrx", "name: other", StringComparison.Ordinal).Replace("owner/nutrx", "owner/other", StringComparison.Ordinal);
        var yaml = Configs.Minimal(repoExtra: second).Replace("authors: [owner]", $"authors: {authors}", StringComparison.Ordinal);
        var loaded = home.Load(yaml);
        Assert.False(loaded.HasErrors, string.Join("\n", loaded.Diagnostics));
        return loaded.Config!;
    }

    [Fact]
    public async Task A_refused_token_is_an_error_on_each_repo_that_uses_it_and_is_asked_once()
    {
        using var home = new TempHome();
        var calls = 0;
        IProvider Providers(RepoConfig repo, ProviderToken token)
        {
            var stub = new StubProvider(() => { calls++; throw new ProviderException("GitHub refused the token (401): Bad credentials.", 401); }, () => new RepositoryInfo(true, "main"));
            return stub;
        }

        var (repos, diagnostics) = await ProviderChecks.RunAsync(Config(home), n => n == "GH_TOKEN" ? "bad" : null, Providers, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Equal(["nutrx", "other"], diagnostics.Select(d => d.Message.Split(':')[0]));
        Assert.All(diagnostics, d => Assert.Equal("provider.token_refused", d.Check));
        Assert.All(diagnostics, d => Assert.EndsWith(".token_env", d.Path, StringComparison.Ordinal));
        Assert.All(repos, r => Assert.NotNull(r.Skipped));
    }

    [Fact]
    public async Task Everyone_s_PRs_on_a_public_repo_are_refused_and_on_a_private_one_allowed()
    {
        using var home = new TempHome();
        var config = Config(home, authors: "[\"*\"]");

        var (_, onPublic) = await ProviderChecks.RunAsync(config, _ => "token", (_, _) => new StubProvider(() => "owner", () => new RepositoryInfo(true, "main")), TestContext.Current.CancellationToken);
        var (repos, onPrivate) = await ProviderChecks.RunAsync(config, _ => "token", (_, _) => new StubProvider(() => "owner", () => new RepositoryInfo(false, "main")), TestContext.Current.CancellationToken);

        Assert.Equal(2, onPublic.Count(d => d.Check == "config.authors_everyone_public"));
        Assert.Empty(onPrivate);
        Assert.All(repos, r => Assert.Equal(("owner", false), (r.Identity, r.IsPublic!.Value)));
    }

    [Fact]
    public async Task A_repo_the_token_can_t_see_is_an_error_on_its_repo_line()
    {
        using var home = new TempHome();

        var (_, diagnostics) = await ProviderChecks.RunAsync(Config(home), _ => "token",
            (repo, _) => new StubProvider(() => "owner", () => repo.Name == "other" ? throw new ProviderException("GitHub has no repos/owner/other (404).", 404) : new RepositoryInfo(false, "main")),
            TestContext.Current.CancellationToken);

        var error = Assert.Single(diagnostics);
        Assert.Equal(("provider.repo_unreachable", "repos[1].repo"), (error.Check, error.Path));
    }

    [Fact]
    public async Task A_repo_whose_token_variable_isn_t_set_is_skipped()
    {
        using var home = new TempHome();

        var (repos, diagnostics) = await ProviderChecks.RunAsync(Config(home), _ => null, (_, _) => throw new InvalidOperationException("no provider without a token"), TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics);
        Assert.All(repos, r => Assert.Equal("GH_TOKEN isn't set", r.Skipped));
    }
}

public sealed class ValidateCommandPhase2Tests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly FakeAgent _fake;

    public ValidateCommandPhase2Tests()
    {
        _fake = new FakeAgent(Path.Combine(_home.Root, "agent"));
        _home.Set("GH_TOKEN", Tokens.GitHubLike());
    }

    public void Dispose() => _home.Dispose();

    private (int Code, string Stdout) Validate(params string[] args)
    {
        var help = string.Join("\n", ClaudeAdapter.RenderedFlags(new AgentConfig
        {
            Name = "claude",
            Path = "agents.claude",
            Kind = AgentKind.Claude,
            Account = "a",
            Command = ["claude"],
            Output = BuiltIn.Output(AgentKind.Claude),
            Isolation = new Dictionary<string, IReadOnlyList<string>>(),
            Tools = new Dictionary<string, IReadOnlyList<string>>(),
            ProjectConfig = BuiltIn.ProjectConfig(AgentKind.Claude),
            LimitPatterns = [],
        }));
        File.WriteAllText(_fake.ScenarioPath, new JsonObject
        {
            ["help"] = help,
            ["init"] = Init.Clean(),
            ["responses"] = new JsonArray(new JsonObject { ["stdout"] = "" }),
        }.ToJsonString());

        var yaml = Configs.Minimal().Replace("command: claude -p --permission-mode dontAsk --output-format json", $"command: '{_fake.Command.Replace("'", "''", StringComparison.Ordinal)}'", StringComparison.Ordinal);
        _home.WriteConfig(yaml);
        var stdout = new StringWriter();

        // The real environment for PATH and the profile, the home's for RTFD_HOME and the token.
        string? Environment(string name) => _home.Environment(name) ?? System.Environment.GetEnvironmentVariable(name);
        var services = new ValidateServices((_, _) => new StubProviderForValidate(), "rtfd-test");
        var code = ValidateCommand.Run(new CommandContext(stdout, new StringWriter(), Environment), args, services);
        return (code, stdout.ToString());
    }

    private sealed class StubProviderForValidate : IProvider
    {
        public Task<string> WhoAmIAsync(CancellationToken cancellationToken = default) => Task.FromResult("owner");

        public Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RepositoryInfo(true, "main"));

        public Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CiState> GetCiStateAsync(string sha, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Git.FetchSource FetchSource(PullRequest pullRequest) => throw new NotSupportedException();

        public Task<PostedReview> PostReviewAsync(ReviewPost post, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PostedReview?> GetReviewAsync(int number, string reviewId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostedReview>> ListReviewsAsync(int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public void Validate_prints_each_repo_and_everything_the_agent_loads()
    {
        var (code, stdout) = Validate();

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Contains("nutrx: owner/nutrx, public, rtfd reads and posts as owner", stdout, StringComparison.Ordinal);
        Assert.Contains("tools: Glob, Grep, Read; rtfd-git: blame, diff, log, show", stdout, StringComparison.Ordinal);
        Assert.Contains("MCP servers: rtfd-git (connected)", stdout, StringComparison.Ordinal);
        Assert.Contains("built into the CLI: cc-plugin-agents-md", stdout, StringComparison.Ordinal);
        Assert.Contains("No errors.", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_home.Root, "validate")) && Directory.EnumerateFileSystemEntries(Path.Combine(_home.Root, "validate")).Any());
    }

    [Fact]
    public void Config_only_stops_after_the_config()
    {
        var (code, stdout) = Validate("--config-only");

        Assert.Equal(EntryPoint.Ok, code);
        Assert.DoesNotContain("Repos:", stdout, StringComparison.Ordinal);
        Assert.Contains("not checked (--config-only)", stdout, StringComparison.Ordinal);
    }
}

/// <summary>
/// Against the real Claude Code, with a scratch user setup (<c>CLAUDE_CONFIG_DIR</c>) holding a
/// user MCP server, a hook and a CLAUDE.md, so nothing of the developer's own setup is touched.
/// Opt-in with <c>RTFD_LIVE_CLAUDE=1</c>. It stops Claude at the init event, so no model is
/// called and no login is needed.
/// </summary>
public sealed class LiveClaudeIsolationTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "rtfd-t", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private (AgentConfig Agent, Dictionary<string, string> Environment, string Marker) Setup()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RTFD_LIVE_CLAUDE") == "1", "set RTFD_LIVE_CLAUDE=1 to run against the installed Claude Code");

        var config = Path.Combine(_work, "scratch-config");
        Directory.CreateDirectory(config);
        var marker = Path.Combine(_work, "hook-marker.txt");
        File.WriteAllText(Path.Combine(config, ".claude.json"), """{"mcpServers": {"scratch-leak": {"type": "stdio", "command": "rtfd-no-such-server", "args": []}}}""");
        File.WriteAllText(Path.Combine(config, "settings.json"), new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["SessionStart"] = new JsonArray(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"echo hooked > \"{marker.Replace('\\', '/')}\"" }),
                }),
            },
        }.ToJsonString());
        File.WriteAllText(Path.Combine(config, "CLAUDE.md"), "The secret word is SCRATCH-PINEAPPLE.\n");

        // A review's environment, plus the scratch setup: CLAUDE* is never in it otherwise.
        var environment = ChildEnvironment.Build(Environment.GetEnvironmentVariable, []);
        environment["CLAUDE_CONFIG_DIR"] = config;
        var agent = new AgentConfig
        {
            Name = "claude",
            Path = "agents.claude",
            Kind = AgentKind.Claude,
            Account = "a",
            Command = ["claude", "-p", "--permission-mode", "dontAsk", "--output-format", "json"],
            Model = "haiku",
            Output = BuiltIn.Output(AgentKind.Claude),
            Isolation = new Dictionary<string, IReadOnlyList<string>>(),
            Tools = new Dictionary<string, IReadOnlyList<string>>(),
            ProjectConfig = BuiltIn.ProjectConfig(AgentKind.Claude),
            LimitPatterns = [],
        };
        return (agent, environment, marker);
    }

    private static string RtfdBinary => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "rtfd.exe" : "rtfd");

    [Fact]
    public async Task Nothing_from_the_user_setup_reaches_the_agent()
    {
        var (agent, environment, marker) = Setup();

        var report = await AgentProbe.RunAsync(agent, environment, Path.Combine(_work, "probe"), RtfdBinary, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Probed, string.Join("\n", report.Diagnostics));
        Assert.DoesNotContain(report.Diagnostics, d => d.IsError);
        Assert.Equal(AgentProbe.ExpectedTools.Order(StringComparer.Ordinal), report.Tools.Order(StringComparer.Ordinal));
        Assert.False(File.Exists(marker), "the scratch setup's hook ran");
    }

    [Fact]
    public async Task Without_the_isolation_flags_the_probe_catches_the_user_MCP_server_by_name()
    {
        var (agent, environment, _) = Setup();
        static IReadOnlyList<string> Weaken(IReadOnlyList<string> arguments) =>
            [.. arguments.Where(a => a is not "--strict-mcp-config" and not "--restricted")];

        var report = await AgentProbe.RunAsync(agent, environment, Path.Combine(_work, "probe"), RtfdBinary, 1, Weaken, TestContext.Current.CancellationToken);

        Assert.Contains(report.Diagnostics, d => d.Check == "agent.mcp_unexpected" && d.Message.Contains("scratch-leak", StringComparison.Ordinal));
    }
}
