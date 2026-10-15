using System.Globalization;
using Rtfd.Config;
using Rtfd.Providers;
using Rtfd.State;

namespace Rtfd.Review;

public enum OwnThreadState
{
    Open,

    /// <summary>rtfd resolved it.</summary>
    ResolvedByRtfd,

    /// <summary>Resolved on the provider, and not by rtfd.</summary>
    ResolvedByHand,
}

/// <summary>One of rtfd's own threads as this run sees it: the store's record joined with the provider's thread.</summary>
public sealed record OwnThread
{
    /// <summary>The id the agent names it by: the inline comment that starts it.</summary>
    public required string Id { get; init; }

    /// <summary>The provider's id for the thread, which resolving takes.</summary>
    public required string ThreadId { get; init; }

    public required FindingSeverity Severity { get; init; }
    public required string Path { get; init; }

    /// <summary>The line it was posted on, at <see cref="PostedOn"/>.</summary>
    public int? PostedLine { get; init; }

    /// <summary>Its line at the PR head now, or null once the code under it changed.</summary>
    public int? Line { get; init; }

    public bool Outdated { get; init; }
    public string? Anchor { get; init; }
    public required string Body { get; init; }

    /// <summary>The head it was posted on.</summary>
    public required string PostedOn { get; init; }

    public required OwnThreadState State { get; init; }

    /// <summary>Whether it counts as open for the verdict: unresolved, or resolved by hand where <c>human_resolve_closes</c> is off.</summary>
    public required bool CountsOpen { get; init; }

    public bool HandResolveReported { get; init; }

    /// <summary>The replies after rtfd's comment, from the posting identity and people on the author allowlist.</summary>
    public IReadOnlyList<ThreadComment> Replies { get; init; } = [];

    /// <summary>Replies from anyone else, left out of the agent's inputs: outside text (spec decision 19).</summary>
    public int RepliesLeftOut { get; init; }

    /// <summary>Where it is, for a person: the line now, or the line it was posted on.</summary>
    public string Where => $"{Path}:{(Line ?? PostedLine)?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
}

public static class OwnThreads
{
    /// <summary>
    /// rtfd's own threads (spec "Posting and idempotency"): the stored comment ids whose thread on
    /// the provider was started by the posting identity. Both must hold. Whether a thread is
    /// resolved comes from the provider (spec decision 16); the store only says whether rtfd did
    /// it, because on GitHub rtfd resolves as the owner and the provider can't tell the two apart.
    /// </summary>
    public static IReadOnlyList<OwnThread> Join(IReadOnlyList<ThreadRow> stored, IReadOnlyList<ReviewThread> threads, string identity, RepoConfig repo)
    {
        var byRoot = new Dictionary<string, ReviewThread>(StringComparer.Ordinal);
        foreach (var thread in threads.Where(t => t.Comments.Count > 0))
        {
            byRoot.TryAdd(RootId(thread), thread);
        }

        var own = new List<OwnThread>();
        foreach (var row in stored)
        {
            if (!byRoot.TryGetValue(row.CommentId, out var thread)
                || !string.Equals(thread.Comments[0].Author, identity, StringComparison.OrdinalIgnoreCase)
                || Severity(row.Severity) is not { } severity)
            {
                continue;
            }

            var state = !thread.IsResolved ? OwnThreadState.Open
                : row.ResolvedByRtfd is not null ? OwnThreadState.ResolvedByRtfd
                : OwnThreadState.ResolvedByHand;
            var replies = thread.Comments.Skip(1).ToList();
            var shown = replies.Where(c => IsTrusted(c.Author, identity, repo)).ToList();
            own.Add(new OwnThread
            {
                Id = row.CommentId,
                ThreadId = thread.Id,
                Severity = severity,
                Path = row.Path,
                PostedLine = row.Line,
                Line = thread.Line,
                Outdated = thread.IsOutdated,
                Anchor = row.Anchor,
                Body = row.Body,
                PostedOn = row.Head,
                State = state,
                CountsOpen = state == OwnThreadState.Open || (state == OwnThreadState.ResolvedByHand && !repo.Settings.HumanResolveCloses),
                HandResolveReported = row.HandResolveReported is not null,
                Replies = shown,
                RepliesLeftOut = replies.Count - shown.Count,
            });
        }

        return own;
    }

    /// <summary>The comment that starts a thread, as the id rtfd stores.</summary>
    public static string RootId(ReviewThread thread) => thread.Comments[0].Id.ToString(CultureInfo.InvariantCulture);

    public static FindingSeverity? Severity(string? text) => text?.ToLowerInvariant() switch
    {
        "should" => FindingSeverity.Should,
        "could" => FindingSeverity.Could,
        "minor" => FindingSeverity.Minor,
        _ => null,
    };

    private static bool IsTrusted(string author, string identity, RepoConfig repo) =>
        string.Equals(author, identity, StringComparison.OrdinalIgnoreCase)
        || repo.Authors.Contains("*")
        || repo.Authors.Contains(author, StringComparer.OrdinalIgnoreCase);
}
