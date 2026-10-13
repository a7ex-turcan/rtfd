using System.Security.Cryptography;
using System.Text;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Providers;
using Rtfd.Review;
using Rtfd.State;

namespace Rtfd.Posting;

public enum PostStatus
{
    /// <summary>Posted now.</summary>
    Posted,

    /// <summary>A review an earlier run posted was found on the PR by its body hashes; nothing was posted.</summary>
    Recovered,

    /// <summary>An earlier run's review never landed, so its recorded bodies were posted now.</summary>
    Reposted,

    /// <summary>The post failed in a way that doesn't say whether it landed. The next run checks before posting again.</summary>
    Unknown,

    /// <summary>The provider refused the review (a 422): the validator let something through.</summary>
    Rejected,

    /// <summary>The post failed and certainly didn't land.</summary>
    Failed,
}

/// <param name="Problems">What the read-back found wrong, empty when it was verified.</param>
public sealed record PostOutcome(PostStatus Status, string? ReviewId, string? Url, int Comments, IReadOnlyList<string> Problems, string? Message = null)
{
    public bool Landed => Status is PostStatus.Posted or PostStatus.Recovered or PostStatus.Reposted;
    public bool Verified => Landed && Problems.Count == 0;
}

/// <summary>
/// Posts one review, exactly once (spec "Posting and idempotency"). Before the post, the
/// posting and the SHA-256 of every body exactly as it will be sent go into the state store in
/// one transaction. After it, the ids that came back; then the review is read back and checked.
/// A posting a crash left open is matched against the posting identity's reviews by body hash:
/// found means it landed, and nothing is posted; not found means its recorded bodies are posted.
/// </summary>
public sealed class Poster(StateStore store, IProvider provider, RepoConfig repo, TimeProvider clock, TextWriter log)
{
    /// <summary>Allowance for the provider's clock running behind ours when matching after a crash.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The hidden marker in the review body (spec "A lost state store"): an HTML comment GitHub
    /// doesn't render, so there is no visible bot marker.
    /// </summary>
    public static string Marker(string head, long runId) => $"<!-- rtfd sha={head} run={runId} -->";

    /// <summary>The hash of a body as the provider will keep it: line endings and trailing whitespace don't count.</summary>
    public static string Hash(string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd())));

    public async Task<PostOutcome> PostAsync(long runId, PullRequest pr, string head, string identity, ReviewPlan plan, CancellationToken cancellationToken = default)
    {
        var summary = $"{plan.Summary}\n\n{Marker(head, runId)}";
        List<PostedBody> bodies =
        [
            new(0, null, null, null, null, summary, Hash(summary), null),
            .. plan.Inline.Select((c, i) => new PostedBody(
                i + 1, c.Path, c.Line, c.Side == DiffSide.New ? "new" : "old", c.Severity.ToString().ToLowerInvariant(), c.Body, Hash(c.Body), null)),
        ];

        var verdict = ReviewBuilder.EventName(plan.Verdict.Event);
        var postingId = store.BeginPosting(runId, repo.Name, pr.Number, head, identity, verdict, clock.GetUtcNow(), bodies);
        return await SendAsync(postingId, pr.Number, head, identity, verdict, clock.GetUtcNow(), bodies, PostStatus.Posted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles every posting an earlier run left open on this PR, before anything new posts.
    /// Returns the outcome for <paramref name="head"/> when one of them was for it.
    /// </summary>
    public async Task<PostOutcome?> RecoverAsync(int number, string head, CancellationToken cancellationToken = default)
    {
        PostOutcome? forHead = null;
        foreach (var posting in store.OpenPostings(repo.Name, number))
        {
            var bodies = store.Bodies(posting.Id);
            PostOutcome outcome;
            if (posting.ReviewId is { } known)
            {
                log.WriteLine($"Checking review {known}, which an earlier run posted but didn't verify...");
                outcome = await VerifyAsync(posting.Id, number, head, posting.Event, known, posting.ReviewUrl, bodies, PostStatus.Recovered, cancellationToken).ConfigureAwait(false);
            }
            else if (await FindAsync(posting, bodies[0].BodyHash, cancellationToken).ConfigureAwait(false) is { } found)
            {
                log.WriteLine($"An earlier run's review landed before rtfd could record it: review {found.Id}. Nothing is posted again.");
                store.RecordReview(posting.Id, found.Id, found.Url);
                outcome = await VerifyAsync(posting.Id, number, posting.Head, posting.Event, found.Id, found.Url, bodies, PostStatus.Recovered, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                store.FinishPosting(posting.Id, "lost", "not on the PR at the next run: it never landed", clock.GetUtcNow());
                if (posting.Head != head)
                {
                    log.WriteLine($"An earlier run's review of {posting.Head[..8]} never landed; the PR has moved on, so it isn't posted.");
                    continue;
                }

                log.WriteLine($"An earlier run's review of {head[..8]} never landed. Posting what it recorded...");
                var fresh = bodies.Select(b => b with { CommentId = null }).ToList();
                var id = store.BeginPosting(posting.RunId, repo.Name, number, head, posting.Identity, posting.Event, clock.GetUtcNow(), fresh);
                outcome = await SendAsync(id, number, head, posting.Identity, posting.Event, clock.GetUtcNow(), fresh, PostStatus.Reposted, cancellationToken).ConfigureAwait(false);
            }

            if (posting.Head == head)
            {
                forHead = outcome;
            }
        }

        return forHead;
    }

    private async Task<PostOutcome> SendAsync(
        long postingId, int number, string head, string identity, string verdict, DateTimeOffset startedAt, IReadOnlyList<PostedBody> bodies,
        PostStatus success, CancellationToken cancellationToken)
    {
        var post = new ReviewPost(number, head, ParseEvent(verdict), bodies[0].Body,
            [.. bodies.Skip(1).Select(b => new ReviewPostComment(b.Path!, b.Line!.Value, b.Side == "old" ? DiffSide.Old : DiffSide.New, b.Body))]);
        PostedReview posted;
        try
        {
            posted = await provider.PostReviewAsync(post, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (ex.Status == 422)
        {
            store.FinishPosting(postingId, "rejected", ex.Message, clock.GetUtcNow());
            return new PostOutcome(PostStatus.Rejected, null, null, 0, [], $"GitHub refused the review: {ex.Message}");
        }
        catch (ProviderException ex) when (ex.MayHaveLanded)
        {
            // Unknown: leave the record pending. A look now, and the next run looks again before posting.
            var posting = new PostingRow(postingId, 0, repo.Name, number, head, identity, verdict, startedAt, "pending", null, null, null);
            if (await FindAsync(posting, bodies[0].BodyHash, cancellationToken).ConfigureAwait(false) is { } found)
            {
                store.RecordReview(postingId, found.Id, found.Url);
                return await VerifyAsync(postingId, number, head, verdict, found.Id, found.Url, bodies, success, cancellationToken).ConfigureAwait(false);
            }

            return new PostOutcome(PostStatus.Unknown, null, null, 0, [], $"{ex.Message}. The review may still land; the next run checks before posting again.");
        }
        catch (ProviderException ex)
        {
            store.FinishPosting(postingId, "lost", ex.Message, clock.GetUtcNow());
            return new PostOutcome(PostStatus.Failed, null, null, 0, [], ex.Message);
        }

        store.RecordReview(postingId, posted.Id, posted.Url);
        return await VerifyAsync(postingId, number, head, verdict, posted.Id, posted.Url, bodies, success, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the review back: its body, its event, and every inline comment by hash. Records the comment ids.</summary>
    private async Task<PostOutcome> VerifyAsync(
        long postingId, int number, string head, string verdict, string reviewId, string? url, IReadOnlyList<PostedBody> bodies,
        PostStatus status, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var review = await provider.GetReviewAsync(number, reviewId, cancellationToken).ConfigureAwait(false);
        var inline = bodies.Where(b => b.Ordinal > 0).ToList();
        if (review is null)
        {
            problems.Add($"review {reviewId} can't be read back");
        }
        else
        {
            if (Hash(review.Body) != bodies[0].BodyHash)
            {
                problems.Add("the summary on GitHub differs from what was sent");
            }

            var expected = verdict switch
            {
                "APPROVE" => "APPROVED",
                "REQUEST_CHANGES" => "CHANGES_REQUESTED",
                _ => "COMMENTED",
            };
            if (review.State != expected)
            {
                problems.Add($"the review is {review.State}, not {expected}");
            }

            var unmatched = review.Comments.ToList();
            foreach (var body in inline)
            {
                var match = unmatched.FirstOrDefault(c => c.Path == body.Path && Hash(c.Body) == body.BodyHash);
                if (match is null)
                {
                    problems.Add($"the comment on {body.Path}:{body.Line} isn't on GitHub as sent");
                    continue;
                }

                unmatched.Remove(match);
                store.RecordComment(postingId, body.Ordinal, match.Id);
            }

            if (unmatched.Count > 0)
            {
                problems.Add($"the review has {unmatched.Count} comment(s) rtfd didn't send");
            }
        }

        store.FinishPosting(postingId, problems.Count == 0 ? "verified" : "unverified", problems.Count == 0 ? null : string.Join("; ", problems), clock.GetUtcNow());
        store.SetLastReviewedHead(repo.Name, number, head);
        return new PostOutcome(status, reviewId, url ?? review?.Url, inline.Count, problems);
    }

    /// <summary>The posting's review on the PR, if it landed: the same author, after it started, with the summary's hash.</summary>
    private async Task<PostedReview?> FindAsync(PostingRow posting, string summaryHash, CancellationToken cancellationToken)
    {
        var reviews = await provider.ListReviewsAsync(posting.Pr, cancellationToken).ConfigureAwait(false);
        return reviews.FirstOrDefault(r =>
            string.Equals(r.Author, posting.Identity, StringComparison.OrdinalIgnoreCase)
            && (r.SubmittedAt is null || r.SubmittedAt >= posting.StartedAt - ClockSkew)
            && Hash(r.Body) == summaryHash);
    }

    private static Verdict ParseEvent(string verdict) => verdict switch
    {
        "APPROVE" => Verdict.Approve,
        "REQUEST_CHANGES" => Verdict.RequestChanges,
        _ => Verdict.Comment,
    };
}
