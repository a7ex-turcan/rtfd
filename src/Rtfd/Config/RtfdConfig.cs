namespace Rtfd.Config;

public enum ProviderKind
{
    GitHub,
    Bitbucket,
}

public enum TrustTier
{
    Trusted,
    Untrusted,
}

public enum Verdict
{
    Comment,
    Approve,
    RequestChanges,
}

public enum FindingSeverity
{
    Should,
    Could,
    Minor,
}

/// <summary>Which CLI an agent entry runs, decided by the executable its command starts.</summary>
public enum AgentKind
{
    Unknown,
    Claude,
    Copilot,
}

public enum OutputSource
{
    Stdout,
    File,
}

/// <summary>The loaded config (spec "Config"), with defaults applied to every repo.</summary>
public sealed record RtfdConfig
{
    public required string FilePath { get; init; }
    public required DataDirectory Data { get; init; }
    public required SecretsSettings Secrets { get; init; }
    public required IReadOnlyDictionary<string, AgentConfig> Agents { get; init; }
    public required IReadOnlyList<RepoConfig> Repos { get; init; }

    /// <summary>The line of every value that was read, by dotted path, for checks that run after loading.</summary>
    public required IReadOnlyDictionary<string, int> Lines { get; init; }

    public int LineOf(string path) => Lines.TryGetValue(path, out var line) ? line : 0;
}

/// <summary>Settings a repo inherits from <c>defaults</c> and may override key by key.</summary>
public sealed record RepoSettings
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan CiMaxWait { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxDiffLines { get; init; } = 3000;
    public IReadOnlyList<string> Exclude { get; init; } = ["**/*.lock", "**/package-lock.json", "**/__snapshots__/**"];
    public IReadOnlyList<FindingSeverity> NotReadyIfOpen { get; init; } = [FindingSeverity.Should];
    public bool HumanResolveCloses { get; init; } = true;
    public IReadOnlyList<string> ContextTools { get; init; } = [];
}

/// <summary>The secret check on outgoing text (spec "Validator"). Global, not per repo.</summary>
public sealed record SecretsSettings
{
    public string Rules { get; init; } = "gitleaks";
    public IReadOnlyList<ConfigPattern> Extra { get; init; } = [];
}

/// <summary>A regular expression from the config, and where it came from.</summary>
public sealed record ConfigPattern(string Pattern, string Path);

public sealed record AgentConfig
{
    public required string Name { get; init; }

    /// <summary><c>agents.&lt;name&gt;</c>, for diagnostics.</summary>
    public required string Path { get; init; }

    public required AgentKind Kind { get; init; }

    /// <summary>The account usage limits apply to; every agent entry on it pauses together.</summary>
    public required string Account { get; init; }

    /// <summary>The command split into arguments, the executable first.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    public string? Model { get; init; }
    public string? Effort { get; init; }
    public required AgentOutput Output { get; init; }

    /// <summary>As written in the config; the checks refuse anything looser than full isolation.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Isolation { get; init; }

    /// <summary>As written in the config; the checks refuse anything beyond the deny-by-default set.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Tools { get; init; }

    public required ProjectConfigPaths ProjectConfig { get; init; }
    public required IReadOnlyList<ConfigPattern> LimitPatterns { get; init; }
}

public sealed record AgentOutput(OutputSource From, string? Extract);

/// <summary>Files an agent CLI loads from a project, reset to the base branch before every run (spec decision 21).</summary>
public sealed record ProjectConfigPaths(IReadOnlyList<string> Executable, IReadOnlyList<string> Instructions);

public sealed record RepoConfig
{
    public required string Name { get; init; }

    /// <summary><c>repos[i]</c>, for diagnostics.</summary>
    public required string Path { get; init; }

    public required ProviderKind Provider { get; init; }

    /// <summary><c>owner/name</c> on the provider.</summary>
    public required string Slug { get; init; }

    public required TrustTier Trust { get; init; }
    public required string Agent { get; init; }

    /// <summary>Absolute; re-read on every run.</summary>
    public required string PromptPath { get; init; }

    public required IReadOnlyList<string> Authors { get; init; }
    public required IReadOnlyList<Verdict> Verdicts { get; init; }

    /// <summary>The name of the variable holding the posting identity's token, never the token.</summary>
    public required string TokenEnv { get; init; }

    public required IReadOnlyList<HookConfig> Hooks { get; init; }
    public required RepoSettings Settings { get; init; }
}

public sealed record HookConfig(string? Name, string? Command);
