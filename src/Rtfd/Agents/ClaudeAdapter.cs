using System.Text.Json.Nodes;
using Rtfd.Config;

namespace Rtfd.Agents;

/// <summary>
/// Renders a claude agent's command line, settings and MCP config (plan 1b.2). The isolation
/// and the tools are built in and a config can't loosen them (spec decision 41).
/// </summary>
/// <remarks>
/// Checked on Claude Code 2.1.296 (2026-10-13): <c>--restricted</c> drops the tools that run
/// code, ignores user, project and local settings, and confines file tools to the working
/// directory plus <c>--add-dir</c>; with <c>--strict-mcp-config</c> no user MCP server or
/// plugin's server loads, and neither do user plugins, skills or CLAUDE.md files.
/// </remarks>
public static class ClaudeAdapter
{
    public const string GitToolsServer = "rtfd-git";

    /// <summary>The built-in tools the agent gets: reads, confined to the run folder and the worktree.</summary>
    public static readonly IReadOnlyList<string> Tools = ["Read", "Grep", "Glob"];

    /// <summary>Denied as well as left out of <c>--tools</c>, so neither change alone hands one back.</summary>
    public static readonly IReadOnlyList<string> DeniedTools =
        ["Bash", "PowerShell", "Edit", "Write", "MultiEdit", "NotebookEdit", "WebFetch", "WebSearch", "Task", "Agent"];

    public static IReadOnlyList<string> Arguments(AgentConfig agent, AgentRunPaths paths)
    {
        var given = agent.Command.Skip(1).ToList();
        var arguments = new List<string>();
        if (!given.Contains("-p") && !given.Contains("--print"))
        {
            arguments.Add("-p");
        }

        arguments.AddRange(given);
        if (!Has(given, "--output-format"))
        {
            arguments.AddRange(["--output-format", "json"]);
        }

        if (!Has(given, "--permission-mode"))
        {
            arguments.AddRange(["--permission-mode", "dontAsk"]);
        }

        if (agent.Model is { } model && !Has(given, "--model"))
        {
            arguments.AddRange(["--model", model]);
        }

        if (agent.Effort is { } effort && !Has(given, "--effort"))
        {
            arguments.AddRange(["--effort", effort]);
        }

        arguments.AddRange(
        [
            "--restricted",
            "--strict-mcp-config",
            "--mcp-config", paths.McpConfig,
            "--settings", paths.Settings,
            "--tools", string.Join(',', Tools),
            "--allowedTools", $"mcp__{GitToolsServer}",
            "--disallowedTools", string.Join(',', DeniedTools),
            "--add-dir", paths.Worktree,
            "--no-session-persistence",
            "--disable-slash-commands",
        ]);
        return arguments;
    }

    /// <summary>rtfd's own settings for the run: <c>--settings</c> still applies under <c>--restricted</c>.</summary>
    public static string Settings() => new JsonObject
    {
        ["permissions"] = new JsonObject
        {
            ["defaultMode"] = "dontAsk",
            ["allow"] = new JsonArray($"mcp__{GitToolsServer}"),
            ["deny"] = new JsonArray([.. DeniedTools.Select(t => (JsonNode)t)]),
            ["disableBypassPermissionsMode"] = "disable",
        },
        ["disableAllHooks"] = true,
        ["enableAllProjectMcpServers"] = false,
    }.ToJsonString();

    /// <summary>The MCP config: <c>rtfd-git</c> only, started by its absolute path, bound to this run folder.</summary>
    public static string McpConfig(string rtfdExecutable, string runFolder) => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            [GitToolsServer] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = rtfdExecutable,
                ["args"] = new JsonArray("git-tools", "--run", runFolder),
            },
        },
    }.ToJsonString();

    private static bool Has(List<string> arguments, string flag) =>
        arguments.Any(a => a == flag || a.StartsWith(flag + "=", StringComparison.Ordinal));
}
