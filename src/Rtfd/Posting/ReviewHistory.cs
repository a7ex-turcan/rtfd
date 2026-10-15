using System.Text.RegularExpressions;
using Rtfd.Config;
using Rtfd.Providers;
using Rtfd.Review;
using Rtfd.State;

namespace Rtfd.Posting;

/// <summary>What rtfd reviewed on a PR before: the last head and its merge base, and its threads.</summary>
/// <param name="Last">Null when nothing of rtfd's landed on the PR yet: this is a first review.</param>
/// <param name="Rebuilt">Found on the provider, because the store didn't know the PR.</param>
public sealed record PriorReview(LastReview? Last, IReadOnlyList<ThreadRow> Threads, bool Rebuilt);

/// <summary>Where a run learns what rtfd reviewed on a PR before.</summary>
public interface IReviewHistory
{
    /// <param name="threads">The PR's review threads, already read: a rebuild needs to know which are resolved.</param>
    Task<PriorReview> LoadAsync(
        RepoConfig repo, int number, string identity, IProvider provider, IReadOnlyList<ReviewThread> threads, CancellationToken cancellationToken = default);
}

/// <summary>
/// The state store's record of a PR or, when the store doesn't know the PR (a lost or new
/// <c>state.db</c>), a rebuild from the provider (spec "A lost state store"; plan Phase 4). On
/// GitHub every review rtfd posted carries the hidden marker with its head and merge base, and its
/// inline comments start rtfd's threads, each leading with its severity. Which of them rtfd
/// resolved itself can't be rebuilt, so a thread already resolved counts as resolved by rtfd and is
/// never reported as a hand resolve. A live run keeps what it rebuilt; a dry run doesn't.
/// </summary>
public sealed class ReviewHistory(StateStore? store, bool persist, TimeProvider clock, TextWriter log) : IReviewHistory
{
    public async Task<PriorReview> LoadAsync(
        RepoConfig repo, int number, string identity, IProvider provider, IReadOnlyList<ReviewThread> threads, CancellationToken cancellationToken = default)
    {
        if (store is not null && store.HasRecord(repo.Name, number))
        {
            return new PriorReview(store.LastReviewed(repo.Name, number), store.Threads(repo.Name, number), Rebuilt: false);
        }

        var (last, rows) = await RebuildAsync(provider, number, identity, threads, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (persist && store is not null)
        {
            store.Rebuilt(repo.Name, number, last, rows);
        }

        if (last is not null)
        {
            log.WriteLine($"rtfd has no record of PR #{number}; rebuilt it from the review markers: last review of {last.Head[..8]}, {rows.Count} thread{(rows.Count == 1 ? "" : "s")}.");
        }

        return new PriorReview(last, rows, Rebuilt: true);
    }

    /// <summary>rtfd's reviews on the PR, found by the posting identity and the marker, and the threads their inline comments start.</summary>
    public static async Task<(LastReview? Last, IReadOnlyList<ThreadRow> Threads)> RebuildAsync(
        IProvider provider, int number, string identity, IReadOnlyList<ReviewThread> threads, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var marked = new Dictionary<string, LastReview>(StringComparer.Ordinal);
        LastReview? last = null;
        foreach (var review in await provider.ListReviewsAsync(number, cancellationToken).ConfigureAwait(false))
        {
            // Oldest first, as the provider lists them: the last one with a marker is the last review.
            if (string.Equals(review.Author, identity, StringComparison.OrdinalIgnoreCase) && Poster.ParseMarker(review.Body) is { } marker)
            {
                marked[review.Id] = marker;
                last = marker;
            }
        }

        if (last is null)
        {
            return (null, []);
        }

        var resolved = threads.Where(t => t.IsResolved && t.Comments.Count > 0).Select(OwnThreads.RootId).ToHashSet(StringComparer.Ordinal);
        var rows = new List<ThreadRow>();
        foreach (var comment in await provider.ListReviewCommentsAsync(number, cancellationToken).ConfigureAwait(false))
        {
            if (comment.InReplyTo is not null
                || comment.ReviewId is null
                || !marked.TryGetValue(comment.ReviewId, out var review)
                || !string.Equals(comment.Author, identity, StringComparison.OrdinalIgnoreCase)
                || LeadingSeverity(comment.Body) is not { } severity)
            {
                continue;
            }

            rows.Add(new ThreadRow(comment.Id, review.Head, severity, comment.Path, comment.OriginalLine ?? comment.Line, null, null, comment.Body,
                PostingId: null, ResolvedByRtfd: resolved.Contains(comment.Id) ? StateStore.Time(now) : null, HandResolveReported: null));
        }

        return (last, rows);
    }

    /// <summary>The severity a comment leads with (Should / Could / MINOR), as every comment rtfd posts does.</summary>
    public static string? LeadingSeverity(string body) =>
        Leading.Match(body) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : null;

    private static readonly Regex Leading = new(@"^\s*(Should|Could|Minor)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}
