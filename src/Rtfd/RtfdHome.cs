namespace Rtfd;

/// <summary>
/// The folder that holds <c>rtfd.yaml</c> and the prompts (spec decision 40): <c>RTFD_HOME</c>,
/// else <c>~/.rtfd</c>. Tests and manual runs set <c>RTFD_HOME</c> to stay away from the real
/// one (CLAUDE.md rule 5).
/// </summary>
public sealed class RtfdHome(string root)
{
    public const string EnvironmentVariable = "RTFD_HOME";
    public const string ConfigFileName = "rtfd.yaml";

    public string Root { get; } = Path.GetFullPath(root);

    public string ConfigPath => Path.Combine(Root, ConfigFileName);

    public static RtfdHome Resolve(Func<string, string?> environment)
    {
        var fromEnvironment = environment(EnvironmentVariable);
        return new RtfdHome(string.IsNullOrWhiteSpace(fromEnvironment) ? DefaultRoot : fromEnvironment);
    }

    /// <summary><c>~/.rtfd</c>: the home when <c>RTFD_HOME</c> is not set.</summary>
    public static string DefaultRoot => Path.Combine(UserProfile, ".rtfd");

    internal static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Resolves a path from the config: a leading <c>~</c> is the user profile, and a relative
    /// path is relative to <paramref name="relativeTo"/>, the config file's folder.
    /// </summary>
    internal static string ResolvePath(string value, string relativeTo)
    {
        if (value == "~")
        {
            return UserProfile;
        }

        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith(@"~\", StringComparison.Ordinal))
        {
            value = Path.Combine(UserProfile, value[2..]);
        }

        return Path.GetFullPath(value, relativeTo);
    }
}

/// <summary>
/// Where rtfd writes: mirrors, run folders, the state store, logs and the status file. The
/// config's <c>data_dir</c>, else the home (spec decision 40).
/// </summary>
public sealed class DataDirectory(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string MirrorsDirectory => Path.Combine(Root, "mirrors");
    public string RunsDirectory => Path.Combine(Root, "runs");
    public string WorktreesDirectory => Path.Combine(Root, "worktrees");

    /// <summary>git's empty global config and empty hooks folder (<see cref="Git.GitRunner"/>).</summary>
    public string GitDirectory => Path.Combine(Root, "git");
    public string StatePath => Path.Combine(Root, "state.db");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string StatusPath => Path.Combine(Root, "status.json");

    /// <summary>Held by the one <c>serve</c> of this data directory for as long as it runs.</summary>
    public string ServeLockPath => Path.Combine(Root, "serve.lock");

    /// <summary>Held by whoever works on a repo's mirror and PRs: <c>serve</c>'s tick or an <c>rtfd run</c>.</summary>
    public string RepoLockPath(string repo) => Path.Combine(Root, "locks", repo + ".lock");
}
