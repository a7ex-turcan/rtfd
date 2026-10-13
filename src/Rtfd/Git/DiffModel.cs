using System.Globalization;
using System.Text;

namespace Rtfd.Git;

public enum FileChange
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
}

public enum LineKind
{
    Context,
    Added,
    Removed,
}

public enum DiffSide
{
    /// <summary>The PR head: added and context lines.</summary>
    New,

    /// <summary>The merge base: removed and context lines.</summary>
    Old,
}

public sealed record DiffLine(LineKind Kind, int? OldLine, int? NewLine, string Text);

public sealed record Hunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines);

public enum FileInclusion
{
    Reviewed,

    /// <summary>Matched an <c>exclude</c> pattern.</summary>
    Excluded,

    /// <summary>Left out by the size cap: the review is partial.</summary>
    Skipped,
}

public sealed record DiffFile
{
    /// <summary>The path at the PR head, or at the merge base for a deleted file.</summary>
    public required string Path { get; init; }

    /// <summary>The path at the merge base when it differs (renames, copies).</summary>
    public string? OldPath { get; init; }

    public required FileChange Change { get; init; }
    public bool IsBinary { get; init; }
    public required IReadOnlyList<Hunk> Hunks { get; init; }

    /// <summary>This file's part of the patch, header included.</summary>
    public required string Patch { get; init; }

    public FileInclusion Inclusion { get; init; } = FileInclusion.Reviewed;

    public int AddedLines => Hunks.Sum(h => h.Lines.Count(l => l.Kind == LineKind.Added));
    public int RemovedLines => Hunks.Sum(h => h.Lines.Count(l => l.Kind == LineKind.Removed));

    /// <summary>
    /// Whether a line on one side appears in a hunk, so the provider accepts an inline comment
    /// on it. GitHub refuses a whole review when one comment sits outside a hunk.
    /// </summary>
    public bool IsInHunk(DiffSide side, int line) => Hunks.Any(h => h.Lines.Any(l => side == DiffSide.New
        ? l.NewLine == line && l.Kind != LineKind.Removed
        : l.OldLine == line && l.Kind != LineKind.Added));
}

/// <summary>
/// The full PR diff (head against its merge base), parsed once into files, hunks and line
/// numbers. The validator, the prompt and <c>rtfd-git</c> all use this one model (plan 1a.5).
/// </summary>
public sealed class DiffModel(IReadOnlyList<DiffFile> files)
{
    /// <summary>Every changed file, whatever its inclusion.</summary>
    public IReadOnlyList<DiffFile> Files { get; } = files;

    public IEnumerable<DiffFile> Reviewed => Files.Where(f => f.Inclusion == FileInclusion.Reviewed);
    public IEnumerable<DiffFile> Excluded => Files.Where(f => f.Inclusion == FileInclusion.Excluded);
    public IEnumerable<DiffFile> Skipped => Files.Where(f => f.Inclusion == FileInclusion.Skipped);

    /// <summary>The size cap left files out, so the review can't be "ready" (spec decision 3).</summary>
    public bool IsPartial => Skipped.Any();

    /// <summary>The patch the agent reads: reviewed files only.</summary>
    public string ReviewedPatch => string.Concat(Reviewed.Select(f => f.Patch));

    public DiffFile? Find(string path) => Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));

    /// <summary>The text of every added and removed line, in every file: what "in the diff" means to the secret check.</summary>
    public IEnumerable<string> ChangedLineTexts =>
        Files.SelectMany(f => f.Hunks).SelectMany(h => h.Lines).Where(l => l.Kind != LineKind.Context).Select(l => l.Text);

    /// <summary>Marks excluded files, then skips files once the changed lines pass the cap, in diff order.</summary>
    public DiffModel Apply(IReadOnlyList<string> exclude, int maxLines)
    {
        var total = 0;
        var result = new List<DiffFile>();
        foreach (var file in Files)
        {
            if (Glob.IsMatchAny(exclude, file.Path) || (file.OldPath is { } old && Glob.IsMatchAny(exclude, old)))
            {
                result.Add(file with { Inclusion = FileInclusion.Excluded });
                continue;
            }

            var size = file.AddedLines + file.RemovedLines;
            if (total + size > maxLines)
            {
                result.Add(file with { Inclusion = FileInclusion.Skipped });
                continue;
            }

            total += size;
            result.Add(file with { Inclusion = FileInclusion.Reviewed });
        }

        return new DiffModel(result);
    }

    /// <summary>
    /// Reads the PR diff from the mirror. The file list comes from <c>--name-status -z</c>, which
    /// gives exact paths, and the hunks from the patch, in the same order.
    /// </summary>
    public static async Task<DiffModel> ReadAsync(GitRunner git, string mirror, string mergeBase, string head, CancellationToken cancellationToken = default)
    {
        string[] options = ["--no-color", "--no-ext-diff", "--no-textconv", "--find-renames", "--src-prefix=a/", "--dst-prefix=b/"];
        var names = await git.RunCheckedAsync(mirror, ["diff", .. options, "--name-status", "-z", "--end-of-options", mergeBase, head], cancellationToken: cancellationToken).ConfigureAwait(false);
        var patch = await git.RunCheckedAsync(mirror, ["diff", .. options, "--end-of-options", mergeBase, head], cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(names, patch);
    }

    public static DiffModel Parse(string nameStatus, string patch)
    {
        var entries = ParseNameStatus(nameStatus);
        var sections = SplitSections(patch);
        if (entries.Count != sections.Count)
        {
            throw new GitException($"the diff lists {entries.Count} files but its patch has {sections.Count}");
        }

        var files = new List<DiffFile>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var (change, path, oldPath) = entries[i];
            var (hunks, binary) = ParseHunks(sections[i]);
            files.Add(new DiffFile
            {
                Path = path,
                OldPath = oldPath,
                Change = change,
                IsBinary = binary,
                Hunks = hunks,
                Patch = sections[i],
            });
        }

        return new DiffModel(files);
    }

    private static List<(FileChange Change, string Path, string? OldPath)> ParseNameStatus(string text)
    {
        var fields = text.Split('\0');
        var entries = new List<(FileChange, string, string?)>();
        var i = 0;
        while (i < fields.Length && fields[i].Length > 0)
        {
            var status = fields[i++];
            var change = status[0] switch
            {
                'A' => FileChange.Added,
                'M' => FileChange.Modified,
                'D' => FileChange.Deleted,
                'R' => FileChange.Renamed,
                'C' => FileChange.Copied,
                'T' => FileChange.TypeChanged,
                _ => throw new GitException($"unexpected diff status '{status}'"),
            };

            if (change is FileChange.Renamed or FileChange.Copied)
            {
                var old = fields[i++];
                entries.Add((change, fields[i++], old));
            }
            else
            {
                entries.Add((change, fields[i++], null));
            }
        }

        return entries;
    }

    private static List<string> SplitSections(string patch)
    {
        var sections = new List<string>();
        var current = new StringBuilder();
        foreach (var line in SplitLinesKeepingEnds(patch))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && current.Length > 0)
            {
                sections.Add(current.ToString());
                current.Clear();
            }

            current.Append(line);
        }

        if (current.Length > 0)
        {
            sections.Add(current.ToString());
        }

        return sections;
    }

    private static (List<Hunk> Hunks, bool Binary) ParseHunks(string section)
    {
        var hunks = new List<Hunk>();
        var binary = false;
        List<DiffLine>? lines = null;
        int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0, oldLine = 0, newLine = 0;

        void Close()
        {
            if (lines is not null)
            {
                hunks.Add(new Hunk(oldStart, oldCount, newStart, newCount, lines));
            }
        }

        foreach (var raw in SplitLinesKeepingEnds(section))
        {
            var line = raw.TrimEnd('\n').TrimEnd('\r');
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                Close();
                (oldStart, oldCount, newStart, newCount) = ParseHunkHeader(line);
                oldLine = oldStart;
                newLine = newStart;
                lines = [];
                continue;
            }

            if (lines is null)
            {
                binary |= line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch";
                continue;
            }

            if (line.Length == 0)
            {
                // A context line whose only content was its leading space, trimmed by an editor.
                lines.Add(new DiffLine(LineKind.Context, oldLine++, newLine++, ""));
                continue;
            }

            switch (line[0])
            {
                case ' ':
                    lines.Add(new DiffLine(LineKind.Context, oldLine++, newLine++, line[1..]));
                    break;
                case '+':
                    lines.Add(new DiffLine(LineKind.Added, null, newLine++, line[1..]));
                    break;
                case '-':
                    lines.Add(new DiffLine(LineKind.Removed, oldLine++, null, line[1..]));
                    break;
                default:
                    // "\ No newline at end of file", and anything else that isn't a line of code.
                    break;
            }
        }

        Close();
        return (hunks, binary);
    }

    private static (int, int, int, int) ParseHunkHeader(string header)
    {
        // @@ -oldStart[,oldCount] +newStart[,newCount] @@ section heading
        var end = header.IndexOf(" @@", 3, StringComparison.Ordinal);
        var ranges = header[3..(end < 0 ? header.Length : end)].Split(' ');
        var (oldStart, oldCount) = ParseRange(ranges[0].TrimStart('-'));
        var (newStart, newCount) = ParseRange(ranges[1].TrimStart('+'));
        return (oldStart, oldCount, newStart, newCount);
    }

    private static (int Start, int Count) ParseRange(string range)
    {
        var comma = range.IndexOf(',');
        return comma < 0
            ? (int.Parse(range, CultureInfo.InvariantCulture), 1)
            : (int.Parse(range[..comma], CultureInfo.InvariantCulture), int.Parse(range[(comma + 1)..], CultureInfo.InvariantCulture));
    }

    private static IEnumerable<string> SplitLinesKeepingEnds(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                yield return text[start..];
                yield break;
            }

            yield return text[start..(end + 1)];
            start = end + 1;
        }
    }
}
