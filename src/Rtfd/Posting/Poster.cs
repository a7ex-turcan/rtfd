using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>A review an earlier run posted was found on the PR by its body hashes; nothing was posted again.</summary>
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
    /// <summary>Replies that landed on rtfd's earlier threads.</summary>
    public int Replies { get; init; }

    /// <summary>Threads rtfd resolved.</summary>
    public int Resolved { get; init; }

    public bool Landed => Status is PostStatus.Posted or PostStatus.Recovered or PostStatus.Reposted;
    public bool Verified => Landed && Problems.Count == 0;
}

/// <summary>
/// Posts one review, exactly once (spec "Posting and idempotency"), then its replies and resolves
/// on rtfd's earlier threads (plan Phase 4). Before anything is sent, the posting and the SHA-256
/// of every body exactly as it will be sent go into the state store in one transaction. After
/// each request, the ids that came back; at the end the review and the replies are read back and
/// checked. A posting a crash left open is matched against the posting identity's reviews and
/// replies by body hash: what landed isn't sent again, and what didn't is sent from the record.
/// </summary>
public sealed class Poster(StateStore store, IProvider provider, RepoConfig repo, TimeProvider clock, TextWriter log)
{
    /// <summary>Allowance for the provider's clock running behind ours when matching after a crash.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    private static readonly Regex MarkerPattern = new(
        "<!-- rtfd sha=(?<head>[0-9a-f]{40}(?:[0-9a-f]{24})?)(?: base=(?<base>[0-9a-f]{40}(?:[0-9a-f]{24})?))? run=[0-9]{1,18} -->",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>
    /// The hidden marker in the review body (spec "A lost state store"): an HTML comment GitHub
    /// doesn't render, so there is no visible bot marker. It names the head and its merge base, so
    /// a lost store can be rebuilt with what the next interdiff needs.
    /// </summary>
    public static string Marker(string head, string? mergeBase, long runId) =>
        mergeBase is null ? $"<!-- rtfd sha={head} run={runId} -->" : $"<!-- rtfd sha={head} base={mergeBase} run={runId} -->";

    /// <summary>The head and merge base a review body's marker names, or null for a body without one.</summary>
    public static LastReview? ParseMarker(string body) =>
        MarkerPattern.Match(body) is { Success: true } match
            ? new LastReview(match.Groups["head"].Value, match.Groups["base"].Success ? match.Groups["base"].Value : null)
            : null;

    /// <summary>The hash of a body as the provider will keep it: line endings and trailing whitespace don't count.</summary>
    public static string Hash(string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd())));

    public async Task<PostOutcome> PostAsync(
        long runId, PullRequest pr, string head, string? mergeBase, string identity, ReviewPlan plan, CancellationToken cancellationToken = default)
    {
        var summary = $"{plan.Summary}\n\n{Marker(head, mergeBase, runId)}";
        var bodies = new List<PostedBody> { new(0, null, null, null, null, summary, Hash(summary), null) };
        foreach (var comment in plan.Inline)
        {
            bodies.Add(new PostedBody(bodies.Count, comment.Path, comment.Line, comment.Side == DiffSide.New ? "new" : "old",
                comment.Severity.ToString().ToLowerInvariant(), comment.Body, Hash(comment.Body), null)
            { Anchor = comment.Anchor });
        }

        foreach (var action in plan.Threads)
        {
            var reply = action.Reply ?? Validator.ResolveReply;
            bodies.Add(new PostedBody(bodies.Count, null, null, null, null, reply, Hash(reply), null)
            {
                ReplyTo = action.Id,
                Action = action.Action == ThreadActionKind.Resolve ? "resolve" : "reply",
            });
        }

        var verdict = ReviewBuilder.EventName(plan.Verdict.Event);
        var now = clock.GetUtcNow();
        var id = store.BeginPosting(runId, repo.Name, pr.Number, head, mergeBase, identity, verdict, now, bodies, plan.Reported);
        var posting = new PostingRow(id, runId, repo.Name, pr.Number, head, mergeBase, identity, verdict, now, "pending", null, null, null, plan.Reported);
        return await SendAsync(posting, bodies, PostStatus.Posted, cancellationToken).ConfigureAwait(false);
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
                log.WriteLine($"Finishing review {known}, which an earlier run posted but didn't see through...");
                outcome = await CompleteAsync(posting, bodies, PostStatus.Recovered, cancellationToken).ConfigureAwait(false);
            }
            else if (await FindAsync(posting, bodies[0].BodyHash, cancellationToken).ConfigureAwait(false) is { } found)
            {
                log.WriteLine($"An earlier run's review landed before rtfd could record it: review {found.Id}. Nothing is posted again.");
                store.RecordReview(posting.Id, found.Id, found.Url);
                outcome = await CompleteAsync(posting with { ReviewId = found.Id, ReviewUrl = found.Url }, bodies, PostStatus.Recovered, cancellationToken).ConfigureAwait(false);
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
                var now = clock.GetUtcNow();
                var id = store.BeginPosting(posting.RunId, repo.Name, number, head, posting.MergeBase, posting.Identity, posting.Event, now, fresh, posting.Reports);
                outcome = await SendAsync(posting with { Id = id, StartedAt = now, State = "pending" }, fresh, PostStatus.Reposted, cancellationToken).ConfigureAwait(false);
            }

            if (posting.Head == head)
            {
                forHead = outcome;
            }
        }

        return forHead;
    }

    private async Task<PostOutcome> SendAsync(PostingRow posting, IReadOnlyList<PostedBody> bodies, PostStatus success, CancellationToken cancellationToken)
    {
        var post = new ReviewPost(posting.Pr, posting.Head, ParseEvent(posting.Event), bodies[0].Body,
            [.. bodies.Where(b => b.IsInline).Select(b => new ReviewPostComment(b.Path!, b.Line!.Value, b.Side == "old" ? DiffSide.Old : DiffSide.New, b.Body))]);
        PostedReview posted;
        try
        {
            posted = await provider.PostReviewAsync(post, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (ex.Status == 422)
        {
            store.FinishPosting(posting.Id, "rejected", ex.Message, clock.GetUtcNow());
            return new PostOutcome(PostStatus.Rejected, null, null, 0, [], $"GitHub refused the review: {ex.Message}");
        }
        catch (ProviderException ex) when (ex.MayHaveLanded)
        {
            // Unknown: leave the record pending. A look now, and the next run looks again before posting.
            if (await FindAsync(posting, bodies[0].BodyHash, cancellationToken).ConfigureAwait(false) is { } found)
            {
                store.RecordReview(posting.Id, found.Id, found.Url);
                return await CompleteAsync(posting with { ReviewId = found.Id, ReviewUrl = found.Url }, bodies, success, cancellationToken).ConfigureAwait(false);
            }

            return new PostOutcome(PostStatus.Unknown, null, null, 0, [], $"{ex.Message}. The review may still land; the next run checks before posting again.");
        }
        catch (ProviderException ex)
        {
            store.FinishPosting(posting.Id, "lost", ex.Message, clock.GetUtcNow());
            return new PostOutcome(PostStatus.Failed, null, null, 0, [], ex.Message);
        }

        store.RecordReview(posting.Id, posted.Id, posted.Url);
        return await CompleteAsync(posting with { ReviewId = posted.Id, ReviewUrl = posted.Url }, bodies, success, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Once the review is on the PR: the replies and resolves, then the read-back, and the record of what landed.</summary>
    private async Task<PostOutcome> CompleteAsync(PostingRow posting, IReadOnlyList<PostedBody> recorded, PostStatus status, CancellationToken cancellationToken)
    {
        var bodies = recorded.ToList();
        var problems = new List<string>();
        var settled = await ThreadActionsAsync(posting, bodies, problems, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(posting, bodies, problems, cancellationToken).ConfigureAwait(false);

        // A reply that may or may not have landed keeps the posting open: the next run looks for it by hash.
        if (settled)
        {
            store.FinishPosting(posting.Id, problems.Count == 0 ? "verified" : "unverified", problems.Count == 0 ? null : string.Join("; ", problems), clock.GetUtcNow());
        }

        foreach (var body in bodies.Where(b => b.IsInline && b.CommentId is not null))
        {
            store.AddThread(repo.Name, posting.Pr, new ThreadRow(
                body.CommentId!, posting.Head, body.Severity ?? "minor", body.Path!, body.Line, body.Side, body.Anchor, body.Body, posting.Id, null, null));
        }

        store.MarkReported(repo.Name, posting.Pr, posting.Reports, clock.GetUtcNow());
        store.SetLastReviewed(repo.Name, posting.Pr, posting.Head, posting.MergeBase);
        var replies = bodies.Where(b => b.IsReply).ToList();
        return new PostOutcome(status, posting.ReviewId, posting.ReviewUrl, bodies.Count(b => b.IsInline), problems)
        {
            Replies = replies.Count(b => b.CommentId is not null),
            Resolved = replies.Count(b => b.Action == "resolve" && b.CommentId is not null && !problems.Any(p => p.StartsWith($"thread {b.ReplyTo} ", StringComparison.Ordinal))),
        };
    }

    /// <summary>
    /// Each reply, then the resolve after it. A reply with no recorded id is first looked for on
    /// its thread by hash, since a crash may have sent it already. Returns false when a reply may
    /// or may not have landed, which the next run settles.
    /// </summary>
    private async Task<bool> ThreadActionsAsync(PostingRow posting, List<PostedBody> bodies, List<string> problems, CancellationToken cancellationToken)
    {
        if (!bodies.Any(b => b.IsReply))
        {
            return true;
        }

        var settled = true;
        var threads = await provider.GetReviewThreadsAsync(posting.Pr, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < bodies.Count; i++)
        {
            var reply = bodies[i];
            if (!reply.IsReply)
            {
                continue;
            }

            var thread = threads.FirstOrDefault(t => t.Comments.Count > 0 && OwnThreads.RootId(t) == reply.ReplyTo);
            if (thread is null)
            {
                problems.Add($"thread {reply.ReplyTo} is gone from the PR");
                continue;
            }

            if (reply.CommentId is null)
            {
                var sent = thread.Comments.Skip(1).FirstOrDefault(c =>
                    string.Equals(c.Author, posting.Identity, StringComparison.OrdinalIgnoreCase)
                    && c.CreatedAt >= posting.StartedAt - ClockSkew
                    && Hash(c.Body) == reply.BodyHash);
                string commentId;
                if (sent is not null)
                {
                    commentId = sent.Id.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    try
                    {
                        commentId = (await provider.ReplyAsync(posting.Pr, reply.ReplyTo!, reply.Body, cancellationToken).ConfigureAwait(false)).Id;
                    }
                    catch (ProviderException ex) when (ex.MayHaveLanded)
                    {
                        problems.Add($"thread {reply.ReplyTo}: the reply may not have landed ({ex.Message}); the next run checks");
                        settled = false;
                        continue;
                    }
                    catch (ProviderException ex)
                    {
                        problems.Add($"thread {reply.ReplyTo}: the reply failed: {ex.Message}");
                        continue;
                    }
                }

                store.RecordComment(posting.Id, reply.Ordinal, commentId);
                bodies[i] = reply = reply with { CommentId = commentId };
            }

            if (reply.Action != "resolve")
            {
                continue;
            }

            // Recorded first: a crash between the resolve and the record must not read as a hand
            // resolve next time. If the resolve fails, the thread is still open on the provider,
            // which is what decides, so it counts as open anyway.
            store.MarkResolved(repo.Name, posting.Pr, reply.ReplyTo!, clock.GetUtcNow());
            if (!thread.IsResolved)
            {
                try
                {
                    await provider.ResolveThreadAsync(thread.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (ProviderException ex)
                {
                    // Still open on the provider, so it counts as open, and the next follow-up can resolve it again.
                    problems.Add($"thread {reply.ReplyTo} couldn't be resolved: {ex.Message}");
                }
            }
        }

        return settled;
    }

    /// <summary>Reads the review back (its body, its event, every inline comment by hash) and every reply, and records the comment ids.</summary>
    private async Task VerifyAsync(PostingRow posting, List<PostedBody> bodies, List<string> problems, CancellationToken cancellationToken)
    {
        var review = await provider.GetReviewAsync(posting.Pr, posting.ReviewId!, cancellationToken).ConfigureAwait(false);
        if (review is null)
        {
            problems.Add($"review {posting.ReviewId} can't be read back");
        }
        else
        {
            if (Hash(review.Body) != bodies[0].BodyHash)
            {
                problems.Add("the summary on GitHub differs from what was sent");
            }

            var expected = posting.Event switch
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
            for (var i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                if (!body.IsInline)
                {
                    continue;
                }

                var match = unmatched.FirstOrDefault(c => c.Path == body.Path && Hash(c.Body) == body.BodyHash);
                if (match is null)
                {
                    problems.Add($"the comment on {body.Path}:{body.Line} isn't on GitHub as sent");
                    continue;
                }

                unmatched.Remove(match);
                store.RecordComment(posting.Id, body.Ordinal, match.Id);
                bodies[i] = body with { CommentId = match.Id };
            }

            if (unmatched.Count > 0)
            {
                problems.Add($"the review has {unmatched.Count} comment(s) rtfd didn't send");
            }
        }

        var replies = bodies.Where(b => b.IsReply && b.CommentId is not null).ToList();
        if (replies.Count == 0)
        {
            return;
        }

        var threads = await provider.GetReviewThreadsAsync(posting.Pr, cancellationToken).ConfigureAwait(false);
        foreach (var reply in replies)
        {
            var thread = threads.FirstOrDefault(t => t.Comments.Count > 0 && OwnThreads.RootId(t) == reply.ReplyTo);
            var comment = thread?.Comments.FirstOrDefault(c => c.Id.ToString(CultureInfo.InvariantCulture) == reply.CommentId);
            if (comment is null || Hash(comment.Body) != reply.BodyHash)
            {
                problems.Add($"thread {reply.ReplyTo}: the reply isn't on GitHub as sent");
            }
            else if (reply.Action == "resolve" && !thread!.IsResolved && !problems.Any(p => p.StartsWith($"thread {reply.ReplyTo} ", StringComparison.Ordinal)))
            {
                problems.Add($"thread {reply.ReplyTo} isn't resolved");
            }
        }
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
