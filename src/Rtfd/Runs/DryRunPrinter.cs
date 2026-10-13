using Rtfd.Agents;
using Rtfd.Git;
using Rtfd.Providers;
using Rtfd.Review;

namespace Rtfd.Runs;

/// <summary>
/// The dry-run printout (plan 1d.8): the inputs, each attempt, snapped lines, the review exactly
/// as it would post, redactions, and the verdict with the event and why.
/// </summary>
public static class DryRunPrinter
{
    public static void Print(RunReport report, RunRequest request, TextWriter output, bool verbose)
    {
        var pr = report.PullRequest;
        if (pr is not null)
        {
            output.WriteLine($"PR #{pr.Number}  {pr.Title}");
            output.WriteLine($"  author {pr.Author} · base {pr.BaseRef} · head {pr.HeadRef}{(pr.IsDraft ? " · draft" : "")}{(pr.State != "open" ? $" · {pr.State}" : "")}");
            output.WriteLine($"  {pr.Url}");
        }

        if (report.Refused is { } refused)
        {
            output.WriteLine();
            output.WriteLine($"Not reviewed: {refused}.");
            return;
        }

        if (report.Commits is { } commits)
        {
            output.WriteLine($"  head {commits.Head[..12]} · merge base {commits.MergeBase[..12]} · base tip {commits.BaseTip[..12]}");
        }

        if (report.Ci is { } ci)
        {
            output.WriteLine($"  CI: {ci.Summary.ToString().ToLowerInvariant()}{(ci.Checks.Count > 0 ? $" ({string.Join(", ", ci.Checks.Select(c => $"{c.Name} {c.Conclusion ?? c.Status}"))})" : "")}");
        }

        if (pr is not null && report.Diff is not null)
        {
            var open = report.Threads.Where(t => !t.IsResolved).ToList();
            output.WriteLine($"  Review threads: {open.Count} open, none of them rtfd's (follow-ups arrive in Phase 4)");
            output.WriteLine($"  Posting identity: {report.PostingIdentity}{(string.Equals(report.PostingIdentity, pr.Author, StringComparison.OrdinalIgnoreCase) ? " (the PR author)" : "")}");
        }

        if (report.Diff is { } diff)
        {
            var reviewed = diff.Reviewed.ToList();
            output.WriteLine($"  Files: {reviewed.Count} reviewed, {diff.Excluded.Count()} excluded, {diff.Skipped.Count()} skipped (+{reviewed.Sum(f => f.AddedLines)} -{reviewed.Sum(f => f.RemovedLines)})");
            foreach (var file in diff.Files)
            {
                var mark = file.Inclusion switch
                {
                    FileInclusion.Excluded => "  (excluded)",
                    FileInclusion.Skipped => "  (skipped: size cap)",
                    _ => "",
                };
                var renamed = file.OldPath is { } old ? $"{old} -> " : "";
                output.WriteLine($"    {Letter(file.Change)} {renamed}{file.Path}  +{file.AddedLines} -{file.RemovedLines}{(file.IsBinary ? " binary" : "")}{mark}");
            }
        }

        if (report.ConfigReset is { } reset)
        {
            if (reset.Restored.Count + reset.Deleted.Count > 0)
            {
                output.WriteLine($"  Agent config reset to base: {Joined("restored", reset.Restored)}{(reset.Restored.Count > 0 && reset.Deleted.Count > 0 ? "; " : "")}{Joined("deleted", reset.Deleted)}");
            }

            foreach (var change in reset.ChangedByPr)
            {
                output.WriteLine($"  The PR {change.Change.ToString().ToLowerInvariant()} agent config: {change.Path} ({change.Kind.ToString().ToLowerInvariant()})");
            }
        }

        if (report.PromptHash is { } hash)
        {
            output.WriteLine($"  Prompt: {request.Repo.PromptPath} (sha256 {hash})");
        }

        if (report.RunFolder is { } run && request.Keep)
        {
            output.WriteLine($"  Kept: {run.Path}");
            output.WriteLine($"        {run.WorktreePath}");
        }

        foreach (var attempt in report.Attempts)
        {
            output.WriteLine();
            output.WriteLine($"Attempt {attempt.Number}: {attempt.Kind.ToString().ToLowerInvariant()}{(attempt.Message is { } m && attempt.Errors.Count == 0 ? $": {m}" : "")}");
            foreach (var error in attempt.Errors)
            {
                output.WriteLine($"  - {error}");
            }

            if (verbose)
            {
                output.WriteLine("  --- agent output ---");
                foreach (var line in attempt.RawText.TrimEnd().Split('\n'))
                {
                    output.WriteLine($"  | {line.TrimEnd('\r')}");
                }
            }
        }

        if (report.Plan is { } plan)
        {
            PrintPlan(plan, output);
        }

        output.WriteLine();
        if (request.InputsOnly && report.Outcome is null)
        {
            output.WriteLine("Inputs only: the agent didn't run.");
            return;
        }

        output.WriteLine(report.Outcome switch
        {
            OutcomeKind.Ok => "Dry run: nothing was posted.",
            OutcomeKind.Limit when report.Limit is { } limit => $"Usage limit: {limit.Message}. The account would pause until {limit.ResetAt:yyyy-MM-dd HH:mm zzz}, and this attempt wouldn't count.",
            OutcomeKind.Fatal => $"Fatal: {report.Message}.",
            OutcomeKind.Invalid => $"Invalid: {report.Message}.",
            OutcomeKind.Transient => $"Failed: {report.Message}. A later run would retry.",
            _ => "Nothing ran.",
        });
    }

    private static void PrintPlan(ReviewPlan plan, TextWriter output)
    {
        output.WriteLine();
        foreach (var comment in plan.Inline.Where(c => c.ClaimedLine is not null))
        {
            output.WriteLine($"Snapped: {comment.Path} {comment.ClaimedLine} -> {comment.Line}");
        }

        output.WriteLine($"Review, as it would post: {ReviewBuilder.EventName(plan.Verdict.Event)}");
        output.WriteLine($"  Verdict: {plan.Verdict.Why}");
        output.WriteLine();
        output.WriteLine("--- summary ---");
        output.WriteLine(plan.Summary);
        foreach (var comment in plan.Inline.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Line))
        {
            output.WriteLine();
            output.WriteLine($"--- {comment.Path}:{comment.Line} ({comment.Side.ToString().ToLowerInvariant()}){(comment.ByRtfd ? " [rtfd]" : "")} ---");
            output.WriteLine(comment.Body);
        }

        foreach (var thread in plan.Threads)
        {
            output.WriteLine();
            output.WriteLine($"--- thread {thread.Id}: {thread.Action.ToString().ToLowerInvariant()} ---");
            if (thread.Reply is { } reply)
            {
                output.WriteLine(reply);
            }
        }

        if (plan.Redactions.Count > 0)
        {
            output.WriteLine();
            foreach (var redaction in plan.Redactions)
            {
                output.WriteLine(redaction.InDiff
                    ? $"Redacted {redaction.Redacted} in {redaction.Where} ({redaction.Rule}): it is in the diff."
                    : $"BLOCKED: {redaction.Redacted} in {redaction.Where} ({redaction.Rule}) isn't in the diff. Nothing would post, and the PR would stop as fatal.");
            }
        }
    }

    private static string Joined(string verb, IReadOnlyList<string> paths) => paths.Count == 0 ? "" : $"{verb} {string.Join(", ", paths)}";

    private static char Letter(FileChange change) => change switch
    {
        FileChange.Added => 'A',
        FileChange.Deleted => 'D',
        FileChange.Renamed => 'R',
        FileChange.Copied => 'C',
        FileChange.TypeChanged => 'T',
        _ => 'M',
    };
}
