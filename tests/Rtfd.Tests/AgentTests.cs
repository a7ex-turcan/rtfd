using Rtfd.Agents;
using Rtfd.Config;
using Rtfd.Processes;

namespace Rtfd.Tests;

public sealed class LimitParserTests
{
    private static readonly IReadOnlyList<ConfigPattern> Patterns = BuiltIn.LimitPatterns(AgentKind.Claude, "agents.claude.limit_patterns");
    private static readonly TimeZoneInfo Chisinau = TimeZoneInfo.FindSystemTimeZoneById("Europe/Chisinau");

    /// <summary>2026-10-13 14:00 in Chisinau (UTC+3 in summer time).</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Local(int month, int day, int hour, int minute) =>
        new(2026, month, day, hour, minute, 0, Chisinau.GetUtcOffset(new DateTime(2026, month, day, hour, minute, 0)));

    [Theory]
    [InlineData("You've hit your session limit · resets 3pm (Europe/Chisinau)", 10, 13, 15, 0)]
    [InlineData("You've hit your session limit · resets 12:10am (Europe/Chisinau)", 10, 14, 0, 10)]
    [InlineData("You've hit your session limit · resets 12pm (Europe/Chisinau)", 10, 14, 12, 0)]
    [InlineData("You've hit your session limit · resets 2:30pm (Europe/Chisinau)", 10, 13, 14, 30)]
    [InlineData("You've hit your weekly limit · resets Oct 16, 9am (Europe/Chisinau)", 10, 16, 9, 0)]
    public void The_reset_time_honours_the_zone_rolls_past_times_to_tomorrow_and_uses_a_given_date(string message, int month, int day, int hour, int minute)
    {
        var hit = LimitParser.Parse(message, Patterns, Now, TimeZoneInfo.Utc);

        Assert.Equal(Local(month, day, hour, minute), hit!.ResetAt);
    }

    [Fact]
    public void Without_a_zone_the_local_zone_applies()
    {
        var hit = LimitParser.Parse("You've hit your usage limit · resets 5pm", Patterns, Now, Chisinau);

        Assert.Equal(Local(10, 13, 17, 0), hit!.ResetAt);
    }

    [Fact]
    public void Without_a_time_the_pause_is_thirty_minutes()
    {
        var hit = LimitParser.Parse("API Error: rate limit exceeded", Patterns, Now, Chisinau);

        Assert.Equal(Now + TimeSpan.FromMinutes(30), hit!.ResetAt);
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_the_local_one()
    {
        var hit = LimitParser.Parse("You've hit your session limit · resets 4pm (Mars/Olympus)", Patterns, Now, Chisinau);

        Assert.Equal(Local(10, 13, 16, 0), hit!.ResetAt);
    }

    [Fact]
    public void Ordinary_text_is_no_limit() =>
        Assert.Null(LimitParser.Parse("All good, nothing to report.", Patterns, Now, Chisinau));

    [Theory]
    [InlineData("12am", 0, 0)]
    [InlineData("12:45am", 0, 45)]
    [InlineData("12pm", 12, 0)]
    [InlineData("1pm", 13, 0)]
    [InlineData("11:30 PM", 23, 30)]
    public void Twelve_am_is_midnight_and_twelve_pm_is_noon(string text, int hour, int minute) =>
        Assert.Equal(new TimeSpan(hour, minute, 0), LimitParser.ParseTime(text));
}

public sealed class OutcomeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);

    private static AgentConfig Agent() => new()
    {
        Name = "claude",
        Path = "agents.claude",
        Kind = AgentKind.Claude,
        Account = "a",
        Command = ["claude", "-p"],
        Output = BuiltIn.Output(AgentKind.Claude),
        Isolation = new Dictionary<string, IReadOnlyList<string>>(),
        Tools = new Dictionary<string, IReadOnlyList<string>>(),
        ProjectConfig = BuiltIn.ProjectConfig(AgentKind.Claude),
        LimitPatterns = BuiltIn.LimitPatterns(AgentKind.Claude, "x"),
    };

    private static AgentOutcome Classify(ProcessResult process) =>
        AgentRunner.Classify(Agent(), process, Now, TimeZoneInfo.Utc, TimeSpan.FromMinutes(30));

    [Fact]
    public void A_clean_answer_is_ok_even_when_it_talks_about_rate_limits()
    {
        var outcome = Classify(new ProcessResult(0, """{"type": "result", "subtype": "success", "is_error": false, "result": "The rate limit check looks right."}""", ""));

        Assert.Equal((OutcomeKind.Ok, "The rate limit check looks right."), (outcome.Kind, outcome.Text));
    }

    [Fact]
    public void A_limit_message_on_a_failed_run_is_a_limit()
    {
        var outcome = Classify(new ProcessResult(1, """{"type": "result", "subtype": "success", "is_error": true, "result": "You've hit your session limit · resets 3pm (UTC)"}""", ""));

        Assert.Equal(OutcomeKind.Limit, outcome.Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.Zero), outcome.Limit!.ResetAt);
    }

    [Fact]
    public void Claude_s_usage_limit_fields_are_a_limit_whatever_the_wording()
    {
        // The shape Claude Code 2.1.296 printed on a real session limit, with the message reworded.
        var outcome = Classify(new ProcessResult(1, """{"type": "result", "subtype": "success", "is_error": true, "api_error_status": 429, "api_error": "usage_limit_reached", "result": "Limit hit, come back later"}""", ""));

        Assert.Equal(OutcomeKind.Limit, outcome.Kind);
        Assert.Equal(Now + LimitParser.DefaultPause, outcome.Limit!.ResetAt);
    }

    [Fact]
    public void Not_being_logged_in_is_fatal() =>
        Assert.Equal(OutcomeKind.Fatal, Classify(new ProcessResult(1, "", "Invalid API key · Please run /login")).Kind);

    [Fact]
    public void A_missing_binary_is_fatal() =>
        Assert.Equal(OutcomeKind.Fatal, Classify(new ProcessResult(-1, "", "") { StartError = "'claude' was not found on PATH" }).Kind);

    [Fact]
    public void A_timeout_or_an_unexplained_failure_is_transient()
    {
        Assert.Equal(OutcomeKind.Transient, Classify(new ProcessResult(-1, "", "") { TimedOut = true }).Kind);
        Assert.Equal(OutcomeKind.Transient, Classify(new ProcessResult(1, "", "socket hang up")).Kind);
        Assert.Equal(OutcomeKind.Transient, Classify(new ProcessResult(0, "not json at all", "")).Kind);
        Assert.Equal(OutcomeKind.Transient, Classify(new ProcessResult(0, """{"type": "result", "subtype": "error_during_execution", "is_error": true}""", "")).Kind);
    }
}

public sealed class ClaudeAdapterTests
{
    private static readonly AgentRunPaths Paths = new("/run", "/worktree", "/run/prompt.md", "/run/settings.json", "/run/mcp.json");

    private static AgentConfig Agent(params string[] command) => new()
    {
        Name = "claude",
        Path = "agents.claude",
        Kind = AgentKind.Claude,
        Account = "a",
        Command = command,
        Model = "opus",
        Effort = "medium",
        Output = BuiltIn.Output(AgentKind.Claude),
        Isolation = new Dictionary<string, IReadOnlyList<string>>(),
        Tools = new Dictionary<string, IReadOnlyList<string>>(),
        ProjectConfig = BuiltIn.ProjectConfig(AgentKind.Claude),
        LimitPatterns = [],
    };

    [Fact]
    public void The_isolation_and_tools_are_always_rendered()
    {
        var arguments = ClaudeAdapter.Arguments(Agent("claude", "-p", "--permission-mode", "dontAsk", "--output-format", "json"), Paths);

        string After(string flag) => arguments[arguments.ToList().IndexOf(flag) + 1];
        Assert.Contains("--restricted", arguments);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Contains("--no-session-persistence", arguments);
        Assert.Contains("--disable-slash-commands", arguments);
        Assert.Equal("/run/mcp.json", After("--mcp-config"));
        Assert.Equal("/run/settings.json", After("--settings"));
        Assert.Equal("/worktree", After("--add-dir"));
        Assert.Equal("Read,Grep,Glob", After("--tools"));
        Assert.Equal("mcp__rtfd-git", After("--allowedTools"));
        Assert.Contains("Bash", After("--disallowedTools").Split(','));
        Assert.Equal(("opus", "medium"), (After("--model"), After("--effort")));
        Assert.Single(arguments, "-p");
    }

    [Fact]
    public void Print_mode_json_output_and_dont_ask_are_added_when_the_command_leaves_them_out()
    {
        var arguments = ClaudeAdapter.Arguments(Agent("claude"), Paths).ToList();

        Assert.Equal("-p", arguments[0]);
        Assert.Equal("json", arguments[arguments.IndexOf("--output-format") + 1]);
        Assert.Equal("dontAsk", arguments[arguments.IndexOf("--permission-mode") + 1]);
    }

    [Fact]
    public void The_settings_deny_every_tool_that_writes_or_runs_code_and_turn_hooks_off()
    {
        var settings = System.Text.Json.Nodes.JsonNode.Parse(ClaudeAdapter.Settings())!;

        Assert.True(settings["disableAllHooks"]!.GetValue<bool>());
        Assert.Equal("dontAsk", settings["permissions"]!["defaultMode"]!.GetValue<string>());
        var deny = settings["permissions"]!["deny"]!.AsArray().Select(d => d!.GetValue<string>()).ToList();
        Assert.Contains("Bash", deny);
        Assert.Contains("PowerShell", deny);
        Assert.Contains("Write", deny);
        Assert.Contains("WebFetch", deny);
    }

    [Fact]
    public void The_mcp_config_names_rtfd_git_only_by_absolute_path()
    {
        var config = System.Text.Json.Nodes.JsonNode.Parse(ClaudeAdapter.McpConfig("/opt/rtfd/rtfd", "/data/runs/x"))!["mcpServers"]!.AsObject();

        var server = Assert.Single(config);
        Assert.Equal("rtfd-git", server.Key);
        Assert.Equal("/opt/rtfd/rtfd", server.Value!["command"]!.GetValue<string>());
        Assert.Equal(["git-tools", "--run", "/data/runs/x"], server.Value["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }
}
