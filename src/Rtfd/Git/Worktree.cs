using Rtfd.Config;

namespace Rtfd.Git;

public enum ConfigFileKind
{
    /// <summary>Settings that can define hooks, tools or permissions, and MCP server definitions.</summary>
    Executable,

    /// <summary>Instruction files such as <c>CLAUDE.md</c>.</summary>
    Instructions,
}

/// <summary>An agent configuration file the PR changes, for the agent-config finding (spec decision 29).</summary>
public sealed record ConfigChange(string Path, ConfigFileKind Kind, FileChange Change);

/// <summary>What the reset did, for the printout and the tests.</summary>
public sealed record ConfigReset(IReadOnlyList<string> Restored, IReadOnlyList<string> Deleted, IReadOnlyList<ConfigChange> ChangedByPr);

/// <summary>
/// A checkout of the PR head from the mirror, outside the agent's working directory, with
/// <c>core.symlinks=false</c> (on every git call) so a symlink in the PR is a plain text file
/// rather than a door out of the worktree.
/// </summary>
public sealed class Worktree
{
    private readonly GitRunner _git;
    private readonly Mirror _mirror;

    private Worktree(GitRunner git, Mirror mirror, string path)
    {
        _git = git;
        _mirror = mirror;
        Path = path;
    }

    public string Path { get; }

    public static async Task<Worktree> CreateAsync(GitRunner git, Mirror mirror, string path, string head, CancellationToken cancellationToken = default)
    {
        path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await git.RunCheckedAsync(mirror.Path, ["worktree", "add", "--quiet", "--detach", path, head],
            timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new Worktree(git, mirror, path);
    }

    /// <summary>
    /// Makes every agent configuration file match the base branch (spec decision 21): a path
    /// matching the agent's <c>project_config</c> patterns, in the head or in the base, ends up
    /// exactly as on the base branch, overwritten or restored, or deleted where the base has
    /// none. Fails closed: if git can't restore a file, the run stops.
    /// </summary>
    public async Task<ConfigReset> ResetAgentConfigAsync(
        ProjectConfigPaths patterns, string baseTip, string head, DiffModel diff, CancellationToken cancellationToken = default)
    {
        bool Matches(string path) => Glob.IsMatchAny(patterns.Executable, path) || Glob.IsMatchAny(patterns.Instructions, path);

        var inBase = (await _mirror.ListFilesAsync(baseTip, cancellationToken).ConfigureAwait(false)).Where(Matches).ToList();
        var inHead = (await _mirror.ListFilesAsync(head, cancellationToken).ConfigureAwait(false)).Where(Matches).ToList();
        var baseSet = new HashSet<string>(inBase, StringComparer.Ordinal);

        // Delete first: on a case-insensitive file system a head-only `.Claude/x.json` and the
        // base's `.claude/x.json` are one file, and the base version must win.
        var deleted = new List<string>();
        foreach (var path in inHead.Where(p => !baseSet.Contains(p)))
        {
            var full = Within(path);
            if (File.Exists(full))
            {
                File.SetAttributes(full, FileAttributes.Normal);
                File.Delete(full);
            }

            deleted.Add(path);
        }

        if (inBase.Count > 0)
        {
            await _git.RunCheckedAsync(Path,
                ["checkout", "--quiet", baseTip, "--pathspec-from-file=-", "--pathspec-file-nul"],
                stdin: string.Join('\0', inBase), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var changed = new List<ConfigChange>();
        foreach (var file in diff.Files)
        {
            var path = Matches(file.Path) ? file.Path : file.OldPath is { } old && Matches(old) ? old : null;
            if (path is null)
            {
                continue;
            }

            var kind = Glob.IsMatchAny(patterns.Executable, path) || (file.OldPath is { } o && Glob.IsMatchAny(patterns.Executable, o))
                ? ConfigFileKind.Executable
                : ConfigFileKind.Instructions;
            changed.Add(new ConfigChange(file.Path, kind, file.Change));
        }

        return new ConfigReset(inBase, deleted, changed);
    }

    public async Task RemoveAsync()
    {
        try
        {
            await _git.RunAsync(_mirror.Path, ["worktree", "remove", "--force", "--force", Path]).ConfigureAwait(false);
        }
        finally
        {
            await Folders.DeleteAsync(Path).ConfigureAwait(false);
            await _git.RunAsync(_mirror.Path, ["worktree", "prune"]).ConfigureAwait(false);
        }
    }

    private string Within(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
}

/// <summary>Deleting folders on Windows, where a just-killed process can hold a handle for a moment.</summary>
public static class Folders
{
    public static async Task DeleteAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }
    }
}
