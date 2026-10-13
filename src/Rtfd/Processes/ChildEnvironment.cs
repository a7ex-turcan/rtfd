namespace Rtfd.Processes;

/// <summary>
/// The environment of every process rtfd starts: the agent, <c>rtfd-git</c>, git, later hooks.
/// Built from an allowlist, never as "rtfd's environment minus the secrets" (CLAUDE.md rule 1),
/// because rtfd itself holds provider tokens and a new secret variable must not leak by default.
/// </summary>
public static class ChildEnvironment
{
    /// <summary>What a Windows program needs to start, find its profile and reach the network.</summary>
    public static readonly IReadOnlyList<string> WindowsNames =
    [
        "SystemRoot", "windir", "PATH", "PATHEXT", "TEMP", "TMP",
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "USERNAME",
    ];

    public static readonly IReadOnlyList<string> UnixNames = ["PATH", "HOME", "USER", "LANG", "TMPDIR"];

    public static IReadOnlyList<string> AllowedNames => OperatingSystem.IsWindows() ? WindowsNames : UnixNames;

    /// <summary>Windows variable names are case-insensitive, Unix ones aren't.</summary>
    public static StringComparer NameComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The allowlisted variables that are set, read through <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">Reads a variable; tests pass their own.</param>
    /// <param name="secrets">Every token value rtfd holds. A variable whose value contains one is
    /// dropped, whatever its name: a token copied into <c>TMP</c> stays out too.</param>
    /// <param name="extra">More names the caller needs (git's own variables are set by
    /// <see cref="Git.GitRunner"/> directly). <c>CLAUDE*</c> names are refused here: a nested
    /// Claude won't run under a parent session's variables, and they carry its session.</param>
    public static Dictionary<string, string> Build(
        Func<string, string?> environment, IReadOnlyCollection<string> secrets, IEnumerable<string>? extra = null)
    {
        var result = new Dictionary<string, string>(NameComparer);
        foreach (var name in AllowedNames.Concat(extra ?? []))
        {
            if (IsSessionVariable(name) || result.ContainsKey(name))
            {
                continue;
            }

            var value = environment(name);
            if (string.IsNullOrEmpty(value) || secrets.Any(s => s.Length > 0 && value.Contains(s, StringComparison.Ordinal)))
            {
                continue;
            }

            result[name] = value;
        }

        return result;
    }

    /// <summary>Variables Claude Code sets in a session (rtfc found a nested Claude refuses to run under them).</summary>
    public static bool IsSessionVariable(string name) => name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase);
}
