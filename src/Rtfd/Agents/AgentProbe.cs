using System.Text.Json;
using System.Text.Json.Nodes;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.GitTools;
using Rtfd.Processes;

namespace Rtfd.Agents;

/// <param name="Name">The server's name as the CLI reports it.</param>
/// <param name="Status">connected, failed, needs-auth, pending.</param>
/// <param name="Source">Where it came from: dynamic (rtfd's --mcp-config), user, plugin, claudeai.</param>
public sealed record McpServerReport(string Name, string Status, string? Source);

/// <summary>What one agent turned out to be on this machine.</summary>
public sealed record AgentReport
{
    public required AgentConfig Agent { get; init; }
    public string? Executable { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<McpServerReport> McpServers { get; init; } = [];

    /// <summary>Plugins as <c>name@source</c>; the <c>@builtin</c> ones ship inside the CLI.</summary>
    public IReadOnlyList<string> Plugins { get; init; } = [];

    public IReadOnlyList<string> Skills { get; init; } = [];
    public IReadOnlyList<string> SlashCommands { get; init; } = [];
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>The probe launched the agent and read what it loaded.</summary>
    public bool Probed { get; init; }
}

/// <summary>
/// Proves what an agent gets, on this machine and the installed CLI (plan Phase 2): the binary
/// on the scrubbed <c>PATH</c>, its version, every flag rtfd renders in its <c>--help</c>, and
/// then the agent launched exactly as a review launches it (same flags, same environment, a
/// throwaway run folder with <c>rtfd-git</c>), reading what the CLI reports it loaded rather
/// than what a model says. Anything beyond the expected set is an error, and so is not being
/// able to tell: it fails closed.
/// </summary>
public static class AgentProbe
{
    /// <summary>rtfd-git's four tools, as Claude names MCP tools.</summary>
    public static readonly IReadOnlyList<string> GitTools =
        [.. new[] { "log", "show", "blame", "diff" }.Select(t => $"mcp__{ClaudeAdapter.GitToolsServer}__{t}")];

    public static IReadOnlyList<string> ExpectedTools => [.. ClaudeAdapter.Tools, .. GitTools];

    /// <param name="environment">The child's whole environment, as a review builds it.</param>
    /// <param name="work">A folder for the throwaway repo and run folder; deleted afterwards.</param>
    /// <param name="rtfdExecutable">The binary <c>mcp.json</c> starts as <c>rtfd-git</c>.</param>
    /// <param name="line">The config line diagnostics point at.</param>
    /// <param name="adjust">Tests weaken the command line with it, to prove the probe catches a leak.</param>
    public static async Task<AgentReport> RunAsync(
        AgentConfig agent, IReadOnlyDictionary<string, string> environment, string work, string rtfdExecutable, int line,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? adjust = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<Diagnostic>();
        var at = $"{agent.Path}.command";
        void Error(string check, string message) => diagnostics.Add(new(check, DiagnosticLevel.Error, message, at, line));

        if (agent.Kind != AgentKind.Claude)
        {
            return new AgentReport { Agent = agent };
        }

        var executable = ProcessRunner.Resolve(agent.Command[0], environment);
        if (executable is null)
        {
            Error("agent.binary_missing", $"'{agent.Command[0]}' isn't on the PATH the agent gets ({Shorten(environment.GetValueOrDefault("PATH") ?? "", 120)})");
            return new AgentReport { Agent = agent, Diagnostics = diagnostics };
        }

        var userArguments = agent.Command.Skip(1).ToList();
        var version = await RunQuickAsync(executable, [.. userArguments, "--version"], environment, work, cancellationToken).ConfigureAwait(false);
        var versionText = version.Stdout.Trim().Split('\n')[0].Trim();
        var help = await RunQuickAsync(executable, [.. userArguments, "--help"], environment, work, cancellationToken).ConfigureAwait(false);
        var missing = ClaudeAdapter.RenderedFlags(agent).Where(f => !HelpMentions(help.Stdout + help.Stderr, f)).ToList();
        if (missing.Count > 0)
        {
            Error("agent.flag_missing", $"{versionText} doesn't know {string.Join(", ", missing)}, which rtfd needs to confine it. Update the CLI.");
            return new AgentReport { Agent = agent, Executable = executable, Version = versionText, Diagnostics = diagnostics };
        }

        var report = await ProbeAsync(agent, executable, environment, work, rtfdExecutable, adjust, cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            Error("agent.init_unreadable", "the agent didn't report what it loaded (no init event in --output-format stream-json), so rtfd can't prove its tools. It fails closed.");
            return new AgentReport { Agent = agent, Executable = executable, Version = versionText, Diagnostics = diagnostics };
        }

        report = report with { Executable = executable, Version = versionText };
        diagnostics.AddRange(Judge(report, at, line));

        // The init event doesn't say whether memory files load, and checking means a model call
        // under the user's own login. So say what rtfd can't prove (spec open question 3).
        var profile = environment.GetValueOrDefault("USERPROFILE") ?? environment.GetValueOrDefault("HOME");
        if (profile is not null && File.Exists(Path.Combine(profile, ".claude", "CLAUDE.md")))
        {
            diagnostics.Add(new("agent.user_memory_present", DiagnosticLevel.Warning,
                "~/.claude/CLAUDE.md exists, and rtfd can't yet prove Claude Code leaves it out of a review, so its instructions may reach the reviewer",
                at, line));
        }

        return report with { Diagnostics = diagnostics };
    }

    /// <summary>The checks on what the CLI reported. Pure, so every case is a test.</summary>
    public static IReadOnlyList<Diagnostic> Judge(AgentReport report, string at, int line)
    {
        var diagnostics = new List<Diagnostic>();
        void Error(string check, string message) => diagnostics.Add(new(check, DiagnosticLevel.Error, message, at, line));

        foreach (var tool in report.Tools.Where(t => !ExpectedTools.Contains(t)))
        {
            Error("agent.tool_unexpected", $"the agent gets the tool {tool}, which isn't one rtfd gives it");
        }

        foreach (var tool in ExpectedTools.Where(t => !report.Tools.Contains(t)))
        {
            Error("agent.tool_missing", $"the agent doesn't get {tool}, so reviews would run without it");
        }

        foreach (var server in report.McpServers.Where(s => s.Name != ClaudeAdapter.GitToolsServer))
        {
            Error("agent.mcp_unexpected", $"the MCP server {server.Name} ({server.Source ?? "unknown source"}, {server.Status}) loads into the agent; only rtfd-git may");
        }

        if (report.McpServers.FirstOrDefault(s => s.Name == ClaudeAdapter.GitToolsServer) is { } git && git.Status != "connected")
        {
            Error("agent.git_tools_failed", $"rtfd-git didn't start in the agent ({git.Status})");
        }

        foreach (var plugin in report.Plugins.Where(p => !p.EndsWith("@builtin", StringComparison.Ordinal)))
        {
            Error("agent.plugin_unexpected", $"the plugin {plugin} loads into the agent");
        }

        if (report.Skills.Count > 0)
        {
            Error("agent.skills_loaded", $"skills load into the agent: {string.Join(", ", report.Skills)}");
        }

        if (report.SlashCommands.Count > 0)
        {
            Error("agent.commands_loaded", $"commands load into the agent: {string.Join(", ", report.SlashCommands)}");
        }

        return diagnostics;
    }

    /// <summary>Reads the init event out of a stream-json line, or null when the line isn't one.</summary>
    public static AgentReport? ParseInit(AgentConfig agent, string line)
    {
        JsonObject? init;
        try
        {
            init = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (init is null || Text(init["type"]) != "system" || Text(init["subtype"]) != "init")
        {
            return null;
        }

        // A missing list is a format change, not an empty list: fail closed on it.
        if (init["tools"] is not JsonArray tools || init["mcp_servers"] is not JsonArray servers)
        {
            return null;
        }

        return new AgentReport
        {
            Agent = agent,
            Probed = true,
            Tools = Strings(tools),
            McpServers = [.. servers.OfType<JsonObject>().Select(s => new McpServerReport(Text(s["name"]) ?? "?", Text(s["status"]) ?? "?", Text(s["source"])))],
            Plugins = init["plugins"] is JsonArray plugins
                ? [.. plugins.OfType<JsonObject>().Select(p => Text(p["source"]) ?? Text(p["name"]) ?? "?")]
                : [],
            Skills = init["skills"] is JsonArray skills ? Strings(skills) : [],
            SlashCommands = init["slash_commands"] is JsonArray commands ? Strings(commands) : [],
        };
    }

    private static async Task<AgentReport?> ProbeAsync(
        AgentConfig agent, string executable, IReadOnlyDictionary<string, string> environment, string work, string rtfdExecutable,
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? adjust, CancellationToken cancellationToken)
    {
        var repo = Path.Combine(work, "repo");
        var run = Path.Combine(work, "run");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(run);

        // A one-commit repository is enough for rtfd-git to start and list its tools.
        var git = new GitRunner(Path.Combine(work, "git"), name => environment.GetValueOrDefault(name), []);
        await git.RunCheckedAsync(repo, ["init", "--quiet"], cancellationToken: cancellationToken).ConfigureAwait(false);
        await git.RunCheckedAsync(repo, ["-c", "user.name=rtfd", "-c", "user.email=rtfd@localhost", "-c", "commit.gpgsign=false", "commit", "--quiet", "--allow-empty", "-m", "rtfd validate"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var head = (await git.RunCheckedAsync(repo, ["rev-parse", "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();

        new GitToolsBinding(repo, repo, head, head, null, Path.Combine(work, "git")).Write(run);
        var paths = new AgentRunPaths(run, repo, Path.Combine(run, "prompt.md"), Path.Combine(run, "settings.json"), Path.Combine(run, "mcp.json"));
        File.WriteAllText(paths.Settings, ClaudeAdapter.Settings());
        File.WriteAllText(paths.McpConfig, ClaudeAdapter.McpConfig(rtfdExecutable, run));

        var arguments = ClaudeAdapter.ProbeArguments(agent, paths);
        AgentReport? report = null;
        var result = await ProcessRunner.RunAsync(new ProcessRequest
        {
            FileName = executable,
            Arguments = adjust is null ? arguments : adjust(arguments),
            Environment = environment,
            WorkingDirectory = run,
            Stdin = "Reply with the word ok.",
            Timeout = TimeSpan.FromSeconds(90),
            StopWhenLine = l => (report = ParseInit(agent, l)) is not null,
        }, cancellationToken).ConfigureAwait(false);

        return result.Stopped ? report : null;
    }

    private static Task<ProcessResult> RunQuickAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string work, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(work);
        return ProcessRunner.RunAsync(new ProcessRequest
        {
            FileName = executable,
            Arguments = arguments,
            Environment = environment,
            WorkingDirectory = work,
            Timeout = TimeSpan.FromSeconds(60),
        }, cancellationToken);
    }

    /// <summary>Whether <c>--help</c> lists the flag as a word of its own.</summary>
    internal static bool HelpMentions(string help, string flag)
    {
        // Not a regex: so --tools isn't found inside --allowedTools, and nothing backtracks.
        static bool IsFlagChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
        for (var at = help.IndexOf(flag, StringComparison.Ordinal); at >= 0; at = help.IndexOf(flag, at + 1, StringComparison.Ordinal))
        {
            var end = at + flag.Length;
            if ((at == 0 || !IsFlagChar(help[at - 1])) && (end == help.Length || !IsFlagChar(help[end])))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<string> Strings(JsonArray array) => [.. array.Select(Text).OfType<string>()];

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
