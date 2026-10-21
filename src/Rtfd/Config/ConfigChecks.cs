using System.Text.RegularExpressions;
using Rtfd.Providers;

namespace Rtfd.Config;

/// <summary>
/// Checks a well-formed config against what this build supports and the guards rtfd never
/// relaxes (spec core decision 2, "Policy"). Each check has a name, so it can be cited and
/// tested. The checks that need the provider are <see cref="ProviderChecks"/>, and the ones
/// that need the agent CLI are <see cref="Agents.AgentProbe"/>.
/// </summary>
public static partial class ConfigChecks
{
    public static IReadOnlyList<Diagnostic> Run(RtfdConfig config, Func<string, string?> environment)
    {
        var report = new Report(config);
        foreach (var agent in config.Agents.Values)
        {
            CheckAgent(agent, report);
        }

        foreach (var repo in config.Repos)
        {
            CheckRepo(repo, environment, report);
        }

        return report.Diagnostics;
    }

    // --- agents -----------------------------------------------------------

    private static void CheckAgent(AgentConfig agent, Report report)
    {
        var commandPath = $"{agent.Path}.command";
        switch (agent.Kind)
        {
            case AgentKind.Copilot:
                report.Error("config.agent_unsupported", "copilot agents arrive in v1; this build runs claude only", commandPath);
                return;
            case AgentKind.Unknown:
                report.Error("config.agent_unsupported",
                    $"rtfd doesn't know how to confine '{agent.Command[0]}'. It runs claude (copilot from v1), " +
                    "started directly, never through a shell or a wrapper", commandPath);
                return;
        }

        CheckClaudeCommand(agent, commandPath, report);
        CheckClaudeIsolation(agent, report);
        CheckClaudeTools(agent, report);
    }

    /// <summary>Flags rtfd renders itself for claude: tools, permissions, MCP servers and settings are its to decide.</summary>
    private static readonly HashSet<string> ClaudeOwnedFlags = new(StringComparer.Ordinal)
    {
        "--dangerously-skip-permissions",
        "--allow-dangerously-skip-permissions",
        "--allowedTools",
        "--allowed-tools",
        "--disallowedTools",
        "--disallowed-tools",
        "--tools",
        "--mcp-config",
        "--settings",
        "--setting-sources",
        "--add-dir",
        "--plugin-dir",
    };

    private static void CheckClaudeCommand(AgentConfig agent, string path, Report report)
    {
        var args = agent.Command;
        for (var i = 1; i < args.Count; i++)
        {
            var parts = args[i].Split('=', 2);
            var flag = parts[0];
            if (ClaudeOwnedFlags.Contains(flag))
            {
                report.Error("config.agent_command_owned_flag",
                    $"the command sets '{flag}', which rtfd renders itself: the agent's tools, permissions, " +
                    "MCP servers and settings are deny by default", path);
            }
            else if (flag == "--permission-mode")
            {
                var mode = parts.Length == 2 ? parts[1] : i + 1 < args.Count ? args[i + 1] : "";
                if (mode is not ("dontAsk" or "default"))
                {
                    report.Error("config.agent_command_owned_flag",
                        $"'--permission-mode {mode}' would let the agent do more than read; use dontAsk", path);
                }
            }
        }
    }

    /// <summary>The only isolation claude runs with. A setting left out still gets it.</summary>
    private static readonly Dictionary<string, string> ClaudeIsolation = new(StringComparer.Ordinal)
    {
        ["mcp"] = "strict",
        ["settings"] = "none",
        ["plugins"] = "none",
    };

    private static void CheckClaudeIsolation(AgentConfig agent, Report report)
    {
        foreach (var (key, values) in agent.Isolation)
        {
            var path = $"{agent.Path}.isolation.{key}";
            if (!ClaudeIsolation.TryGetValue(key, out var required))
            {
                report.Error("config.isolation_unknown",
                    $"claude has no isolation setting '{key}'; rtfd knows {string.Join(", ", ClaudeIsolation.Keys)}", path);
            }
            else if (values is not [var value] || value != required)
            {
                report.Error("config.isolation_weakened",
                    $"'{key}' must be {required}: nothing from your own setup may load into the agent", path);
            }
        }
    }

    /// <summary>Deny by default: file reads confined to these places, and rtfd's own git tools.</summary>
    private static readonly Dictionary<string, string[]> ClaudeTools = new(StringComparer.Ordinal)
    {
        ["read"] = ["worktree", "run_folder"],
        ["mcp"] = ["rtfd-git"],
    };

    private static void CheckClaudeTools(AgentConfig agent, Report report)
    {
        foreach (var (key, values) in agent.Tools)
        {
            var path = $"{agent.Path}.tools.{key}";
            if (!ClaudeTools.TryGetValue(key, out var allowed))
            {
                report.Error("config.tool_not_allowed",
                    $"the agent gets file reads and rtfd-git, nothing else; '{key}' is not one of {string.Join(", ", ClaudeTools.Keys)}", path);
                continue;
            }

            foreach (var value in values.Where(v => !allowed.Contains(v)))
            {
                report.Error("config.tool_not_allowed",
                    $"'{value}' is not allowed under '{key}'; the allowed values are {string.Join(", ", allowed)}", path);
            }
        }
    }

    // --- repos ------------------------------------------------------------

    private static void CheckRepo(RepoConfig repo, Func<string, string?> environment, Report report)
    {
        if (repo.Provider == ProviderKind.Bitbucket)
        {
            report.Error("config.provider_unsupported", "Bitbucket arrives in v1; this build talks to GitHub and Azure DevOps", $"{repo.Path}.provider");
        }

        if (repo.Auth == TokenAuth.AzureCli && repo.Provider != ProviderKind.AzureDevOps)
        {
            report.Error("config.auth_unsupported",
                $"'auth: azure_cli' gets an Entra ID token, which only Azure DevOps takes; {ProviderNames.Of(repo.Provider)} needs token_env",
                $"{repo.Path}.auth");
        }

        if (repo.Trust == TrustTier.Untrusted)
        {
            report.Error("config.trust_untrusted_unsupported",
                "the untrusted tier doesn't exist yet. Keep the repo trusted with an author allowlist that excludes outsiders",
                $"{repo.Path}.trust");
        }

        if (repo.Hooks.Count > 0)
        {
            report.Error("config.hooks_unsupported", "pre-review hooks aren't built yet", $"{repo.Path}.hooks");
        }

        if (repo.Settings.ContextTools.Count > 0)
        {
            report.Error("config.context_tools_unsupported", "context tools for the agent aren't built yet",
                report.SettingPath(repo, "context_tools"));
        }

        if (repo.TokenEnv is { } tokenEnv)
        {
            CheckTokenEnv(repo, tokenEnv, environment, report);
        }

        if (!File.Exists(repo.PromptPath))
        {
            report.Error("config.prompt_missing", $"no prompt file at {repo.PromptPath}", $"{repo.Path}.prompt");
        }
    }

    /// <summary>
    /// <c>token_env</c> names a variable; it never holds the token. A value that looks like a token
    /// is refused, and no message here repeats it, because validate's output gets pasted around.
    /// </summary>
    private static void CheckTokenEnv(RepoConfig repo, string tokenEnv, Func<string, string?> environment, Report report)
    {
        var path = $"{repo.Path}.token_env";
        if (LooksLikeAToken(tokenEnv))
        {
            report.Error("config.token_env_holds_a_token",
                "token_env holds what looks like a token. It must name the environment variable that holds it, " +
                "such as GH_TOKEN (the value isn't shown here)", path);
        }
        else if (!EnvironmentVariableName().IsMatch(tokenEnv))
        {
            report.Error("config.token_env_not_a_name",
                "token_env must be the name of an environment variable, such as GH_TOKEN (the value isn't shown here)", path);
        }
        else if (string.IsNullOrEmpty(environment(tokenEnv)))
        {
            report.Warning("config.token_env_unset",
                $"{tokenEnv} is not set in this environment; reviews of this repo fail until it is", path);
        }
    }

    internal static bool LooksLikeAToken(string value) =>
        (value.Length >= 20 && KnownTokenPrefix().IsMatch(value))
        || (value.Length >= 32 && value.Any(char.IsLower) && value.Any(char.IsUpper) && value.Any(char.IsDigit));

    [GeneratedRegex("^(gh[pousr]_|github_pat_|glpat-|xox[abprs]-|ATBB|ATATT|AKIA|sk-)")]
    private static partial Regex KnownTokenPrefix();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentVariableName();

    private sealed class Report(RtfdConfig config)
    {
        private readonly HashSet<(string, string)> _seen = [];

        public List<Diagnostic> Diagnostics { get; } = [];

        public void Error(string check, string message, string path) => Add(check, DiagnosticLevel.Error, message, path);

        public void Warning(string check, string message, string path) => Add(check, DiagnosticLevel.Warning, message, path);

        /// <summary>Where a repo setting came from: the repo itself, else <c>defaults</c>.</summary>
        public string SettingPath(RepoConfig repo, string key) =>
            config.Lines.ContainsKey($"{repo.Path}.{key}") ? $"{repo.Path}.{key}" : $"defaults.{key}";

        // A setting inherited from defaults would otherwise be reported once per repo.
        private void Add(string check, DiagnosticLevel level, string message, string path)
        {
            if (_seen.Add((check, path)))
            {
                Diagnostics.Add(new(check, level, message, path, config.LineOf(path)));
            }
        }
    }
}
