using System.Text;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Providers;
using Rtfd.Review.Secrets;

namespace Rtfd.Review;

/// <param name="Ready">Policy says the PR is ready.</param>
/// <param name="Event">What would post: COMMENT, APPROVE or REQUEST_CHANGES.</param>
/// <param name="Why">Why this event, for the printout.</param>
/// <param name="Headline">The review's first line, which rtfd writes so the text always matches the verdict.</param>
public sealed record VerdictDecision(bool Ready, Verdict Event, string Why, string Headline);

/// <summary>A secret rtfd found in outgoing text, never shown in full.</summary>
/// <param name="Where">The summary, a comment's path and line, or a reply.</param>
public sealed record Redaction(string Rule, string Redacted, string Where, bool InDiff);

/// <summary>Everything a review would post, after validation, rtfd's own findings, the secret check and the verdict.</summary>
public sealed record ReviewPlan
{
    public required string Summary { get; init; }
    public required IReadOnlyList<InlineComment> Inline { get; init; }
    public required IReadOnlyList<ThreadAction> Threads { get; init; }
    public required VerdictDecision Verdict { get; init; }
    public required IReadOnlyList<Redaction> Redactions { get; init; }

    /// <summary>A secret from outside the diff: the post is blocked and the PR is <c>fatal</c> (spec "Validator").</summary>
    public bool Blocked => Redactions.Any(r => !r.InDiff);

    /// <summary>Every body that would post, for the secret check and the write-ahead hashes.</summary>
    public IEnumerable<string> Bodies => [Summary, .. Inline.Select(c => c.Body), .. Threads.Select(t => t.Reply ?? "")];
}

/// <summary>What the review builder needs to know about the run.</summary>
public sealed record ReviewContext
{
    public required PullRequest PullRequest { get; init; }
    public required DiffModel Diff { get; init; }
    public required CiState Ci { get; init; }
    public required ConfigReset ConfigReset { get; init; }
    public required RepoConfig Repo { get; init; }

    /// <summary>The login the token posts as: an approve or request-changes on your own PR becomes a comment.</summary>
    public required string PostingIdentity { get; init; }
}

/// <summary>
/// Turns a valid findings document into the review rtfd would post: adds rtfd's own findings
/// (agent configuration, a committed credential) and notes (partial review, CI not finished),
/// runs the secret check over every body, and computes the verdict from policy (spec decision 3).
/// rtfd's own text follows the same convention as the prompt: Should / Could / MINOR, short, no
/// bot marker.
/// </summary>
public static class ReviewBuilder
{
    public static ReviewPlan Build(FindingsDocument document, ValidationResult validation, ReviewContext context, SecretScanner scanner)
    {
        var inline = validation.Inline.ToList();
        var general = validation.General.ToList();
        var notes = new List<string>();

        AddConfigFindings(context, inline, general, notes);
        var committed = AddCommittedCredentials(context.Diff, scanner, inline);

        if (context.Diff.IsPartial)
        {
            var skipped = context.Diff.Skipped.Select(f => f.Path).ToList();
            notes.Add($"This is a partial review: the diff is over {context.Repo.Settings.MaxDiffLines} changed lines, so I didn't review {Listed(skipped)}.");
        }

        if (context.Ci.Summary == CiSummary.Pending)
        {
            notes.Add("CI hadn't finished when I reviewed this.");
        }

        var summary = Summary(document.Summary, general, validation.Folded, notes);

        // The secret check covers every body, rtfd's own included (CLAUDE.md rule 3).
        var redactions = new List<Redaction>();
        var changedLines = context.Diff.ChangedLineTexts.ToList();
        string Check(string text, string where)
        {
            foreach (var match in scanner.Scan(text))
            {
                var inDiff = changedLines.Any(l => l.Contains(match.Secret, StringComparison.Ordinal));
                redactions.Add(new Redaction(match.Rule, SecretScanner.Redact(match.Secret), where, inDiff));
                text = text.Replace(match.Secret, SecretScanner.Redact(match.Secret), StringComparison.Ordinal);
                if (inDiff && !committed.Contains(match.Secret))
                {
                    // In the diff but not on an added line (a removed line, or a token rtfd holds): still worth a Should.
                    committed.Add(match.Secret);
                    general.Add(new GeneralComment(FindingSeverity.Should, CredentialBody(match.Secret), ByRtfd: true));
                }
            }

            return text;
        }

        summary = Check(summary, "the summary");
        inline = [.. inline.Select(c => c with { Body = Check(c.Body, $"{c.Path}:{c.Line}") })];
        var threads = validation.Threads.Select(t => t.Reply is null ? t : t with { Reply = Check(t.Reply, $"the reply on thread {t.Id}") }).ToList();

        // A Should added by the check above lands in the summary too.
        var lateShoulds = general.Where(g => g.ByRtfd && !summary.Contains(g.Body, StringComparison.Ordinal)).ToList();
        if (lateShoulds.Count > 0)
        {
            summary = string.Join("\n\n", [summary, .. lateShoulds.Select(g => g.Body)]);
        }

        var severities = inline.Select(c => c.Severity).Concat(general.Select(g => g.Severity)).Concat(validation.Folded.Select(f => f.Severity));
        var verdict = Decide(severities, context.Diff.IsPartial, context.Repo, context.PullRequest.Author, context.PostingIdentity);
        return new ReviewPlan
        {
            // The agent's summary never says whether the PR is ready: rtfd decides, and says so first.
            Summary = summary.Length > 0 ? $"{verdict.Headline}\n\n{summary}" : verdict.Headline,
            Inline = inline,
            Threads = threads,
            Verdict = verdict,
            Redactions = redactions,
        };
    }

    /// <summary>
    /// Open Shoulds (this run's; Phase 4 adds earlier unresolved ones) mean not ready, and a
    /// partial review is never ready. The verdict maps to an event, filtered by
    /// <c>verdicts:</c> and by the self-review check, falling back to a comment.
    /// </summary>
    public static VerdictDecision Decide(IEnumerable<FindingSeverity> open, bool partial, RepoConfig repo, string author, string postingIdentity)
    {
        var blocking = open.Where(s => repo.Settings.NotReadyIfOpen.Contains(s)).OrderBy(s => s).ToList();
        var openText = string.Join(" and ", blocking.GroupBy(s => s).Select(g => Counted(g.Count(), Name(g.Key))));
        var ready = !partial && blocking.Count == 0;
        var reason = partial ? "the review is partial"
            : blocking.Count > 0 ? $"{openText} open"
            : "nothing blocking is open";

        var headline = ready ? "Ready to merge from my side."
            : blocking.Count > 0 ? $"Not ready: {openText} open."
            : "Not ready: this is a partial review.";

        var wanted = ready ? Verdict.Approve : Verdict.RequestChanges;
        var label = ready ? "ready" : "not ready";
        if (!repo.Verdicts.Contains(wanted))
        {
            return new VerdictDecision(ready, Verdict.Comment, $"{label} ({reason}); {EventName(wanted)} isn't in verdicts, so COMMENT", headline);
        }

        if (string.Equals(author, postingIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return new VerdictDecision(ready, Verdict.Comment, $"{label} ({reason}); {EventName(wanted)} downgraded to COMMENT: the posting identity is the PR author", headline);
        }

        return new VerdictDecision(ready, wanted, $"{label} ({reason})", headline);
    }

    public static string EventName(Verdict verdict) => verdict switch
    {
        Verdict.Approve => "APPROVE",
        Verdict.RequestChanges => "REQUEST_CHANGES",
        _ => "COMMENT",
    };

    private static void AddConfigFindings(ReviewContext context, List<InlineComment> inline, List<GeneralComment> general, List<string> notes)
    {
        foreach (var change in context.ConfigReset.ChangedByPr)
        {
            var verb = change.Change switch
            {
                FileChange.Added => "adds",
                FileChange.Deleted => "removes",
                _ => "changes",
            };

            if (change.Kind == ConfigFileKind.Executable)
            {
                // Always a Should (spec decision 29): it runs on the machine of anyone who opens the repo with that agent.
                var body = $"Should: this {verb} agent configuration that can run code (`{change.Path}`): hooks, tools, permissions or MCP servers apply to anyone who opens the repo with that agent. It needs its own careful review.";
                var line = change.Change == FileChange.Deleted ? null : FirstAddedLine(context.Diff.Find(change.Path));
                if (line is { } l)
                {
                    inline.Add(new InlineComment(FindingSeverity.Should, change.Path, l, DiffSide.New, body, ByRtfd: true));
                }
                else
                {
                    general.Add(new GeneralComment(FindingSeverity.Should, body, ByRtfd: true));
                }
            }
            else
            {
                // v0 reviews allowlisted authors only, so instructions are a note, not a Should.
                notes.Add($"This {verb} agent instructions (`{change.Path}`). I reviewed it with the base branch's version loaded.");
            }
        }
    }

    /// <summary>A credential on an added line gets rtfd's own Should on that line, whatever the agent said.</summary>
    private static HashSet<string> AddCommittedCredentials(DiffModel diff, SecretScanner scanner, List<InlineComment> inline)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in diff.Files)
        {
            foreach (var line in file.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == LineKind.Added))
            {
                foreach (var match in scanner.Scan(line.Text, file.Path))
                {
                    if (found.Add(match.Secret))
                    {
                        inline.Add(new InlineComment(FindingSeverity.Should, file.Path, line.NewLine!.Value, DiffSide.New, CredentialBody(match.Secret), ByRtfd: true));
                    }
                }
            }
        }

        return found;
    }

    private static string CredentialBody(string secret) =>
        $"Should: this commits a credential (`{SecretScanner.Redact(secret)}`). Remove it and rotate it: it stays in the history.";

    private static int? FirstAddedLine(DiffFile? file) =>
        file?.Hunks.SelectMany(h => h.Lines).FirstOrDefault(l => l.Kind == LineKind.Added)?.NewLine;

    private static string Summary(string agentSummary, List<GeneralComment> general, IReadOnlyList<FoldedFinding> folded, List<string> notes)
    {
        var text = new StringBuilder(agentSummary.Trim());
        void Paragraph(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            if (list.Count == 0)
            {
                return;
            }

            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            text.Append(string.Join('\n', list));
        }

        // One paragraph per finding and per note, as a person would write them; the folded
        // findings are one list.
        foreach (var comment in general)
        {
            Paragraph([comment.Body]);
        }

        if (folded.Count > 0)
        {
            Paragraph(["Outside the diff:", .. folded.Select(f => $"- `{f.Path}:{f.Line}`: {f.Body}")]);
        }

        foreach (var note in notes)
        {
            Paragraph([note]);
        }

        return text.ToString();
    }

    private static string Name(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Should => "Should",
        FindingSeverity.Could => "Could",
        _ => "MINOR",
    };

    private static string Counted(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Listed(List<string> paths) =>
        paths.Count <= 5 ? string.Join(", ", paths.Select(p => $"`{p}`")) : $"{string.Join(", ", paths.Take(5).Select(p => $"`{p}`"))} and {paths.Count - 5} more";
}
