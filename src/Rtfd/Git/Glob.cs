namespace Rtfd.Git;

/// <summary>
/// The path patterns in the config (<c>exclude</c>, <c>project_config</c>): <c>**</c> is any
/// number of folders, <c>*</c> and <c>?</c> stay inside one name. Patterns are anchored at the
/// repo root, so <c>CLAUDE.md</c> is the root file only and <c>**/CLAUDE.md</c> is every one.
/// </summary>
/// <remarks>
/// Matching ignores case. Windows and macOS file systems do, so a PR adding
/// <c>.CLAUDE/Settings.json</c> lands on disk where Claude Code reads <c>.claude/settings.json</c>.
/// </remarks>
public static class Glob
{
    public static bool IsMatch(string pattern, string path)
    {
        var parts = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var names = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Match(parts, 0, names, 0);
    }

    public static bool IsMatchAny(IEnumerable<string> patterns, string path) => patterns.Any(p => IsMatch(p, path));

    private static bool Match(string[] parts, int p, string[] names, int n)
    {
        while (p < parts.Length)
        {
            if (parts[p] == "**")
            {
                // Zero or more folders, then the rest of the pattern.
                for (var skip = n; skip <= names.Length; skip++)
                {
                    if (Match(parts, p + 1, names, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (n >= names.Length || !MatchName(parts[p], names[n]))
            {
                return false;
            }

            p++;
            n++;
        }

        return n == names.Length;
    }

    private static bool MatchName(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
    {
        // Iterative wildcard match with one backtrack point for the last '*'.
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
