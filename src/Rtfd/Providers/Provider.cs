using Rtfd.Config;
using Rtfd.Git;

namespace Rtfd.Providers;

/// <summary>
/// A provider token, read once from the variable <c>token_env</c> names and held in memory. It
/// prints as a placeholder, so string interpolation into a log can't leak it (CLAUDE.md rule 1).
/// </summary>
public sealed class ProviderToken(string value)
{
    /// <summary>The token itself. Only an <c>Authorization</c> header and the secret check read it.</summary>
    internal string Value { get; } = value;

    public override string ToString() => "[token]";
}

public sealed record PullRequest
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string Author { get; init; }
    public required bool IsDraft { get; init; }

    /// <summary>The head lives in another repo than the base.</summary>
    public required bool IsFork { get; init; }

    public required string State { get; init; }
    public required string HeadSha { get; init; }
    public required string HeadRef { get; init; }
    public required string BaseRef { get; init; }

    /// <summary>The base commit the provider last compared the PR with. For a closed PR it is the base as it was, not today's.</summary>
    public required string BaseSha { get; init; }

    public required string Url { get; init; }
}

public enum CiSummary
{
    /// <summary>The repo has no checks on this commit; nothing to wait for.</summary>
    None,
    Pending,
    Success,
    Failure,
}

public sealed record CiCheck(string Name, string Status, string? Conclusion, string? Url);

public sealed record CiState(CiSummary Summary, IReadOnlyList<CiCheck> Checks);

public sealed record ThreadComment(long Id, string Author, string Body, DateTimeOffset CreatedAt);

public sealed record ReviewThread
{
    public required string Id { get; init; }
    public required bool IsResolved { get; init; }
    public required bool IsOutdated { get; init; }
    public string? ResolvedBy { get; init; }
    public string? Path { get; init; }

    /// <summary>The line at the PR head now; null once the code under it changed (outdated).</summary>
    public int? Line { get; init; }

    /// <summary>The line it was posted on, at the commit it was posted on.</summary>
    public int? OriginalLine { get; init; }

    /// <summary>Oldest first: the comment that starts the thread, then the replies.</summary>
    public required IReadOnlyList<ThreadComment> Comments { get; init; }
}

public sealed record RepositoryInfo(bool IsPublic, string DefaultBranch);

/// <summary>An inline comment to post, on a line inside a hunk (the validator made sure).</summary>
public sealed record ReviewPostComment(string Path, int Line, DiffSide Side, string Body);

/// <summary>One review to post: the summary, the event and the inline comments, all on one commit.</summary>
/// <param name="CommitId">The reviewed head, so a push during the run doesn't misplace the comments.</param>
public sealed record ReviewPost(int Number, string CommitId, Verdict Event, string Body, IReadOnlyList<ReviewPostComment> Comments);

public sealed record PostedComment(string Id, string Path, int? Line, string Body);

/// <summary>A review comment as the provider lists it: an inline comment that starts a thread, or a reply on one.</summary>
/// <param name="ReviewId">The review it belongs to. On GitHub every reply gets an empty review of its own.</param>
/// <param name="InReplyTo">The comment that starts its thread, for a reply.</param>
/// <param name="OriginalLine">The line it was posted on, at the commit it was posted on.</param>
public sealed record ReviewComment(
    string Id, string Author, string? ReviewId, string? InReplyTo, string Path, int? Line, int? OriginalLine, string Body, DateTimeOffset? CreatedAt);

/// <summary>A review as the provider has it.</summary>
/// <param name="State">GitHub's: COMMENTED, APPROVED, CHANGES_REQUESTED, PENDING, DISMISSED.</param>
/// <param name="Comments">Its inline comments, when they were asked for.</param>
public sealed record PostedReview(
    string Id, string Author, string State, string Body, string? CommitId, DateTimeOffset? SubmittedAt, string? Url,
    IReadOnlyList<PostedComment> Comments);

/// <summary>A refusal or failure from the provider, with a message a person can act on.</summary>
public sealed class ProviderException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;

    /// <summary>401 and 403 that aren't rate limits: retrying won't help (spec outcome <c>fatal</c>).</summary>
    public bool IsAuth => Status is 401 or 403;

    /// <summary>A post that failed in a way that doesn't say whether it landed: a 5xx, a timeout, a dropped connection.</summary>
    public bool MayHaveLanded { get; init; }
}

/// <summary>A provider adapter (spec "Provider adapter"): GitHub today.</summary>
public interface IProvider
{
    /// <summary>The login the token posts as: the self-review check and recognising rtfd's own threads.</summary>
    Task<string> WhoAmIAsync(CancellationToken cancellationToken = default);

    Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken cancellationToken = default);

    Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default);

    Task<CiState> GetCiStateAsync(string sha, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>Where git fetches the PR from, with the credential for the fetch.</summary>
    FetchSource FetchSource(PullRequest pullRequest);

    /// <summary>
    /// Posts one review. Not retried on a 5xx or a network error: the review may have landed,
    /// and only the write-ahead record's body hashes can tell (spec "Posting and idempotency").
    /// </summary>
    Task<PostedReview> PostReviewAsync(ReviewPost post, CancellationToken cancellationToken = default);

    /// <summary>One review with its inline comments, or null when it doesn't exist.</summary>
    Task<PostedReview?> GetReviewAsync(int number, string reviewId, CancellationToken cancellationToken = default);

    /// <summary>Every review on the PR, without their comments: for matching a write-ahead record after a crash.</summary>
    Task<IReadOnlyList<PostedReview>> ListReviewsAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>Every review comment on the PR, replies included, with its review and line: for rebuilding a lost store.</summary>
    Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>A reply on the thread <paramref name="commentId"/> starts. Not retried on a 5xx or a network error, like a review.</summary>
    Task<ReviewComment> ReplyAsync(int number, string commentId, string body, CancellationToken cancellationToken = default);

    /// <summary>Marks a review thread resolved. Resolving one that is already resolved changes nothing.</summary>
    Task ResolveThreadAsync(string threadId, CancellationToken cancellationToken = default);
}
