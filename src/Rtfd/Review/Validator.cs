using System.Text;
using Rtfd.Config;
using Rtfd.Git;

namespace Rtfd.Review;

/// <summary>An inline comment that will post on a line inside a hunk.</summary>
/// <param name="ClaimedLine">The line the agent gave, when the anchor moved it.</param>
/// <param name="ByRtfd">rtfd's own finding (agent config, a committed credential), not the agent's.</param>
/// <param name="Anchor">The code it refers to, which the thread keeps for the follow-ups.</param>
public sealed record InlineComment(FindingSeverity Severity, string Path, int Line, DiffSide Side, string Body, int? ClaimedLine = null, bool ByRtfd = false, string? Anchor = null);

/// <summary>A finding on the whole PR, posted in the review body.</summary>
public sealed record GeneralComment(FindingSeverity Severity, string Body, bool ByRtfd = false);

/// <summary>A finding whose anchor is outside the diff: folded into the summary, not dropped (spec "Findings contract").</summary>
public sealed record FoldedFinding(FindingSeverity Severity, string Path, int Line, string Body);

/// <param name="Threads">The thread actions that will post: on rtfd's own threads that are still open, a resolve always with its reply.</param>
public sealed record ValidationResult(
    IReadOnlyList<InlineComment> Inline, IReadOnlyList<GeneralComment> General, IReadOnlyList<FoldedFinding> Folded,
    IReadOnlyList<ThreadAction> Threads, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>Actions on rtfd's own threads that are already closed: dropped, not an error (plan Phase 4).</summary>
    public IReadOnlyList<string> DroppedThreads { get; init; } = [];
}

/// <summary>
/// Checks a findings document against the PR (spec "Validator", plan 1d.2). Models get line
/// numbers wrong often enough to matter, so each inline finding's anchor is looked up in the
/// file, at the head for <c>side: new</c> or at the merge base for <c>side: old</c>, nearest to
/// the line the model gave. Found inside a hunk, the line snaps to it; found outside the diff,
/// the finding is folded into the summary; not found, the document is rejected. GitHub refuses
/// a whole review when one comment sits outside a hunk, so this is what makes posting safe.
/// </summary>
public static class Validator
{
    /// <summary>The reply a resolve posts when the agent gave none: the convention's words.</summary>
    public const string ResolveReply = "Looks good now.";

    /// <param name="readFile">A file's text on one side, or null when that side has no such file.</param>
    /// <param name="ownThreads">rtfd's own threads on the PR; no other thread may be resolved or replied to (CLAUDE.md rule 3).</param>
    public static async Task<ValidationResult> ValidateAsync(
        FindingsDocument document, DiffModel diff, Func<DiffSide, string, Task<string?>> readFile, IReadOnlyList<OwnThread> ownThreads)
    {
        var inline = new List<InlineComment>();
        var general = new List<GeneralComment>();
        var folded = new List<FoldedFinding>();
        var errors = new List<string>();

        for (var i = 0; i < document.Findings.Count; i++)
        {
            var finding = document.Findings[i];
            var body = Labelled(finding.Severity, finding.Body);
            if (finding.Path is null)
            {
                general.Add(new GeneralComment(finding.Severity, body));
                continue;
            }

            var path = finding.Path.Replace('\\', '/').TrimStart('/');
            while (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path[2..];
            }

            var file = diff.Find(path);
            var sidePath = finding.Side == DiffSide.Old && file?.OldPath is { } oldPath ? oldPath : path;
            var text = await readFile(finding.Side, sidePath).ConfigureAwait(false);
            if (text is null)
            {
                errors.Add($"findings[{i}]: there is no {path} at the {(finding.Side == DiffSide.New ? "PR head (side new)" : "merge base (side old)")}");
                continue;
            }

            if (FindAnchor(text, finding.Anchor!, finding.Line) is not { } line)
            {
                errors.Add($"findings[{i}]: the anchor \"{Shorten(finding.Anchor!)}\" isn't in {path} at the {(finding.Side == DiffSide.New ? "PR head" : "merge base")}. Copy a short piece of the line exactly from the file, without diff markers.");
                continue;
            }

            if (file is not null && file.IsInHunk(finding.Side, line))
            {
                inline.Add(new InlineComment(finding.Severity, path, line, finding.Side, body, finding.Line is { } claimed && claimed != line ? claimed : null, Anchor: finding.Anchor));
            }
            else
            {
                folded.Add(new FoldedFinding(finding.Severity, path, line, body));
            }
        }

        var actions = new List<ThreadAction>();
        var dropped = new List<string>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < document.Threads.Count; i++)
        {
            var action = document.Threads[i];
            var thread = ownThreads.FirstOrDefault(t => t.Id == action.Id);
            if (thread is null)
            {
                errors.Add($"threads[{i}]: {Shorten(action.Id)} isn't one of your threads. You may only resolve or reply to threads listed under own in inputs/threads.json.");
                continue;
            }

            if (!named.Add(action.Id))
            {
                errors.Add($"threads[{i}]: thread {action.Id} is named twice; give each thread one action");
                continue;
            }

            if (!thread.CountsOpen)
            {
                // Resolved since the agent's inputs were written, or by hand: stirring it helps nobody.
                dropped.Add($"thread {action.Id} ({thread.Where}) is already resolved, so its {action.Action.ToString().ToLowerInvariant()} was dropped");
                continue;
            }

            actions.Add(action.Action == ThreadActionKind.Resolve && string.IsNullOrWhiteSpace(action.Reply) ? action with { Reply = ResolveReply } : action);
        }

        return new ValidationResult(inline, general, folded, actions, errors) { DroppedThreads = dropped };
    }

    /// <summary>
    /// The 1-based line where the anchor is, whitespace-normalized, nearest to the claimed line;
    /// a multi-line anchor matches consecutive lines and reports its first. Retried without a
    /// leading diff marker, which models copy from the patch.
    /// </summary>
    public static int? FindAnchor(string text, string anchor, int? claimed)
    {
        var lines = text.Split('\n').Select(Normalize).ToArray();
        return Find(lines, AnchorLines(anchor, stripMarkers: false), claimed)
            ?? Find(lines, AnchorLines(anchor, stripMarkers: true), claimed);
    }

    private static int? Find(string[] lines, string[] anchor, int? claimed)
    {
        if (anchor.Length == 0)
        {
            return null;
        }

        int? best = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Matches(lines, i, anchor))
            {
                continue;
            }

            var line = i + 1;
            if (best is null || (claimed is { } c && Math.Abs(line - c) < Math.Abs(best.Value - c)))
            {
                best = line;
            }

            if (claimed is null)
            {
                break;
            }
        }

        return best;
    }

    private static bool Matches(string[] lines, int start, string[] anchor)
    {
        if (anchor.Length == 1)
        {
            return lines[start].Contains(anchor[0], StringComparison.Ordinal);
        }

        // First and last anchor lines may be partial; the ones between must be whole lines.
        var at = start;
        for (var a = 0; a < anchor.Length; a++, at++)
        {
            while (at < lines.Length && lines[at].Length == 0)
            {
                at++;
            }

            if (at >= lines.Length)
            {
                return false;
            }

            var ok = a == 0 ? lines[at].EndsWith(anchor[a], StringComparison.Ordinal)
                : a == anchor.Length - 1 ? lines[at].StartsWith(anchor[a], StringComparison.Ordinal)
                : lines[at] == anchor[a];
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] AnchorLines(string anchor, bool stripMarkers) =>
        [.. anchor.Split('\n')
            .Select(l => stripMarkers && l.Length > 0 && l[0] is '+' or '-' ? l[1..] : l)
            .Select(Normalize)
            .Where(l => l.Length > 0)];

    /// <summary>Runs of whitespace become one space, and the ends are trimmed.</summary>
    public static string Normalize(string line)
    {
        var result = new StringBuilder(line.Length);
        var space = false;
        foreach (var c in line.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                result.Append(' ');
                space = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>
    /// Every comment leads with its severity (Should / Could / MINOR), as the convention says
    /// and as rebuilding a lost store relies on (spec "Posting and idempotency"). Added when
    /// the agent left it out.
    /// </summary>
    public static string Labelled(FindingSeverity severity, string body)
    {
        var label = severity switch
        {
            FindingSeverity.Should => "Should",
            FindingSeverity.Could => "Could",
            _ => "MINOR",
        };
        return body.StartsWith(label, StringComparison.OrdinalIgnoreCase) ? body : $"{label}: {body}";
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "...";
}
