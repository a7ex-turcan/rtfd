namespace Rtfd.Config;

/// <summary>What rtfd knows about each agent CLI without being told, from the spec's config draft.</summary>
internal static class BuiltIn
{
    public static AgentOutput Output(AgentKind kind) => kind switch
    {
        // `claude -p --output-format json` wraps the model's answer in its own JSON, as `result`.
        AgentKind.Claude => new(OutputSource.Stdout, ".result"),
        AgentKind.Copilot => new(OutputSource.File, null),
        _ => new(OutputSource.Stdout, null),
    };

    /// <summary>
    /// Files the CLI loads from a project. Executable configuration can run code (hooks, MCP
    /// servers); instructions steer the model. Both are reset to the base branch (spec decision 21).
    /// </summary>
    public static ProjectConfigPaths ProjectConfig(AgentKind kind) => kind switch
    {
        // AGENTS.md too: Claude Code 2.1.296 ships a built-in plugin, cc-plugin-agents-md, that reads it.
        AgentKind.Claude => new(
            [".claude/settings.json", ".claude/settings.local.json", ".claude/**/*.json", ".mcp.json"],
            ["CLAUDE.md", "**/CLAUDE.md", "CLAUDE.local.md", "**/CLAUDE.local.md", ".claude/**/*.md", "AGENTS.md", "**/AGENTS.md"]),
        AgentKind.Copilot => new(
            [".github/copilot/settings.json", ".github/copilot/settings.local.json", ".github/copilot/**"],
            [".github/copilot-instructions.md", ".github/instructions/**", "AGENTS.md"]),
        _ => new([], []),
    };

    /// <summary>Usage-limit messages, used when the config gives none. The wording changes, so a config can replace them.</summary>
    public static IReadOnlyList<ConfigPattern> LimitPatterns(AgentKind kind, string path) => kind switch
    {
        AgentKind.Claude =>
        [
            // e.g. "You've hit your session limit · resets 12:10am (Europe/Chisinau)"
            new(@"(?i)hit your (session|usage|weekly) limit.*?resets\s+(?<date>[A-Z][a-z]{2}\s+\d{1,2},?\s+)?(?<reset>\d{1,2}(:\d{2})?\s*[ap]m)(\s*\((?<zone>[^)]+)\))?", $"{path} (built in)"),
            new(@"(?i)usage limit reached|rate limit", $"{path} (built in)"),
        ],
        _ => [],
    };
}
