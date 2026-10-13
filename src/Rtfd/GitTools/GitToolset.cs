using System.Globalization;
using System.Text.RegularExpressions;
using Rtfd.Git;

namespace Rtfd.GitTools;

/// <summary>A tool call rtfd won't run, with a message that tells the agent what it may do instead.</summary>
public sealed class ToolRefusal(string message) : Exception(message);

/// <summary>
/// The four tools behind <c>rtfd-git</c> (spec "rtfd-git"): <c>log</c>, <c>show</c>,
/// <c>blame</c> and <c>diff</c>. This is the agent's only way to look at history, so every
/// argument is checked before git sees it and every command line is fixed here. No
/// <c>--output</c>, no pager, no external diff or textconv, no pathspec magic.
/// </summary>
public sealed partial class GitToolset
{
    public const int MaxOutputChars = 60_000;
    public const int MaxLogCount = 50;
    public const int MaxBlameLines = 500;

    private static readonly string[] DiffOptions = ["--no-color", "--no-ext-diff", "--no-textconv"];

    private readonly GitRunner _git;
    private readonly GitToolsBinding _binding;
    private readonly HashSet<string> _allowed;

    private GitToolset(GitRunner git, GitToolsBinding binding, HashSet<string> allowed)
    {
        _git = git;
        _binding = binding;
        _allowed = allowed;
    }

    /// <summary>The commits an argument may name: the head, the merge base, the last reviewed head and every commit between.</summary>
    public IReadOnlySet<string> AllowedCommits => _allowed;

    public static async Task<GitToolset> OpenAsync(GitToolsBinding binding, Func<string, string?> environment, CancellationToken cancellationToken = default)
    {
        var git = new GitRunner(binding.GitDirectory, environment, []);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { binding.Head, binding.MergeBase };
        if (binding.LastReviewedHead is { } last)
        {
            allowed.Add(last);
        }

        var between = await git.RunCheckedAsync(binding.Mirror, ["rev-list", "--end-of-options", $"{binding.MergeBase}..{binding.Head}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        allowed.UnionWith(between.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return new GitToolset(git, binding, allowed);
    }

    /// <summary>Commits on the PR, newest first, or the history of one path from the head.</summary>
    public async Task<string> LogAsync(string? path, int? max, CancellationToken cancellationToken = default)
    {
        var count = max ?? 20;
        if (count is < 1 or > MaxLogCount)
        {
            throw new ToolRefusal($"max must be between 1 and {MaxLogCount}");
        }

        string[] format = ["--no-decorate", "--date=iso-strict", "--stat=120", "--pretty=medium", $"--max-count={count.ToString(CultureInfo.InvariantCulture)}"];
        return path is null
            ? await RunAsync(["log", .. DiffOptions, .. format, "--end-of-options", $"{_binding.MergeBase}..{_binding.Head}"], cancellationToken).ConfigureAwait(false)
            : await RunAsync(["log", .. DiffOptions, .. format, "--end-of-options", _binding.Head, "--", CheckPath(path)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One commit with its patch, or one file as it was at that commit.</summary>
    public async Task<string> ShowAsync(string sha, string? path, CancellationToken cancellationToken = default)
    {
        var commit = await CheckCommitAsync(sha, cancellationToken).ConfigureAwait(false);
        return path is null
            ? await RunAsync(["show", .. DiffOptions, "--no-decorate", "--date=iso-strict", "--pretty=medium", "--stat=120", "--patch", "--end-of-options", commit], cancellationToken).ConfigureAwait(false)
            : await RunAsync(["cat-file", "-p", $"{commit}:{CheckPath(path)}"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Who last changed each line of a range, at the PR head.</summary>
    public async Task<string> BlameAsync(string path, int start, int end, CancellationToken cancellationToken = default)
    {
        if (start < 1 || end < start)
        {
            throw new ToolRefusal("start must be at least 1, and end at least start");
        }

        if (end - start + 1 > MaxBlameLines)
        {
            throw new ToolRefusal($"blame at most {MaxBlameLines} lines at a time");
        }

        // No --end-of-options here: git 2.47's blame then reads the path after `--` as a
        // revision. The head is rtfd's own full SHA from the binding, never the agent's.
        var range = string.Create(CultureInfo.InvariantCulture, $"-L{start},{end}");
        return await RunAsync(["blame", "--no-textconv", "--date=short", range, _binding.Head, "--", CheckPath(path)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The diff between two commits of this PR or its merge base, optionally for one path.</summary>
    public async Task<string> DiffAsync(string from, string to, string? path, CancellationToken cancellationToken = default)
    {
        var a = await CheckCommitAsync(from, cancellationToken).ConfigureAwait(false);
        var b = await CheckCommitAsync(to, cancellationToken).ConfigureAwait(false);
        string[] arguments = ["diff", .. DiffOptions, "--find-renames", "--end-of-options", a, b];
        return await RunAsync(path is null ? arguments : [.. arguments, "--", CheckPath(path)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A path relative to the repo root, normalized: no absolute, drive or UNC path, no <c>..</c>,
    /// nothing git could read as an option or pathspec magic. <c>GIT_LITERAL_PATHSPECS</c> and
    /// <c>--</c> guard the same things again.
    /// </summary>
    public static string CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096)
        {
            throw new ToolRefusal("path must be a file path relative to the repo root");
        }

        if (path.Any(char.IsControl))
        {
            throw new ToolRefusal("path must not contain control characters");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || DrivePath().IsMatch(normalized))
        {
            throw new ToolRefusal($"'{path}' is absolute; give a path relative to the repo root");
        }

        if (normalized.StartsWith(':') || normalized.StartsWith('-'))
        {
            throw new ToolRefusal($"'{path}' isn't a plain path; pathspec magic and options aren't allowed");
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        if (parts.Contains(".."))
        {
            throw new ToolRefusal($"'{path}' leaves the repo; '..' isn't allowed");
        }

        if (parts.Count == 0)
        {
            throw new ToolRefusal("path must name a file or folder in the repo");
        }

        return string.Join('/', parts);
    }

    /// <summary>A commit of this PR, given as hex, resolved to its full SHA.</summary>
    public async Task<string> CheckCommitAsync(string sha, CancellationToken cancellationToken = default)
    {
        if (!Sha().IsMatch(sha))
        {
            throw new ToolRefusal($"'{Shorten(sha)}' isn't a commit SHA; give 7 to 64 hex digits");
        }

        var result = await _git.RunAsync(_binding.Mirror, ["rev-parse", "--verify", "--quiet", "--end-of-options", sha + "^{commit}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var full = result.Stdout.Trim();
        if (!result.Ok || !_allowed.Contains(full))
        {
            throw new ToolRefusal($"{Shorten(sha)} isn't a commit of this PR. Use the head ({_binding.Head[..12]}), the merge base ({_binding.MergeBase[..12]}) or a commit between them (log lists them).");
        }

        return full;
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(_binding.Mirror, arguments, timeout: TimeSpan.FromSeconds(60), maxOutputChars: MaxOutputChars, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new ToolRefusal("git took too long; narrow the request");
        }

        if (result.ExitCode != 0)
        {
            var message = result.Stderr.Trim();
            throw new ToolRefusal(message.Length > 0 ? Shorten(message, 500) : $"git {arguments[0]} failed");
        }

        return result.Truncated
            ? result.Stdout + $"\n[truncated: the output passed {MaxOutputChars} characters. Narrow it with a path or a smaller range.]"
            : result.Stdout;
    }

    private static string Shorten(string text, int max = 80) => text.Length <= max ? text : text[..max] + "...";

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex Sha();

    [GeneratedRegex("^[A-Za-z]:")]
    private static partial Regex DrivePath();
}
