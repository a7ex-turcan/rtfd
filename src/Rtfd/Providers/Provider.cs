using Rtfd.Config;
using Rtfd.Git;

namespace Rtfd.Providers;

/// <summary>
/// A provider token, held in memory only: read once from the variable <c>token_env</c> names, or
/// handed over by the Azure CLI (<see cref="TokenSource"/>). It prints as a placeholder, so string
/// interpolation into a log can't leak it (CLAUDE.md rule 1).
/// </summary>
public sealed class ProviderToken(string value)
{
    /// <summary>The token itself. Only an <c>Authorization</c> header and the secret check read it.</summary>
    internal string Value { get; } = value;

    /// <summary>An Entra ID (OAuth) access token rather than a PAT: a JWT, three base64url parts, the first a JSON header.</summary>
    internal bool IsJwt => Value.StartsWith("eyJ", StringComparison.Ordinal) && Value.Count(c => c == '.') == 2;

    /// <summary>
    /// What must never leave rtfd: the value, and for <c>user:secret</c> (Bitbucket's
    /// <c>email:token</c>) the secret on its own too, since git and a careless paste use it alone.
    /// </summary>
    internal IEnumerable<string> Secrets =>
        Value.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? [Value, Value[(colon + 1)..]] : [Value];

    public override string ToString() => "[token]";
}

/// <summary>The providers' names, for what rtfd tells people.</summary>
public static class ProviderNames
{
    public static string Of(ProviderKind kind) => kind switch
    {
        ProviderKind.AzureDevOps => "Azure DevOps",
        ProviderKind.Bitbucket => "Bitbucket",
        _ => "GitHub",
    };
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

/// <param name="Id">Unique on the PR. On Azure DevOps, where comment ids count from 1 in each
/// thread, the first comment's is the thread's id and a reply's is <c>thread.comment</c>.</param>
public sealed record ThreadComment(string Id, string Author, string Body, DateTimeOffset CreatedAt);

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

public sealed record PostedComment(string Id, string Path, int? Line, string Body)
{
    /// <summary>Where a person sees it, when the provider says.</summary>
    public string? Url { get; init; }
}

/// <summary>A review comment as the provider lists it: an inline comment that starts a thread, or a reply on one.</summary>
/// <param name="ReviewId">The review it belongs to. On GitHub every reply gets an empty review of its own.</param>
/// <param name="InReplyTo">The comment that starts its thread, for a reply.</param>
/// <param name="OriginalLine">The line it was posted on, at the commit it was posted on.</param>
public sealed record ReviewComment(
    string Id, string Author, string? ReviewId, string? InReplyTo, string Path, int? Line, int? OriginalLine, string Body, DateTimeOffset? CreatedAt);

/// <summary>A review as the provider has it.</summary>
/// <param name="State">GitHub's: COMMENTED, APPROVED, CHANGES_REQUESTED, PENDING, DISMISSED. On
/// Azure DevOps, where the vote isn't part of a review, the posting identity's vote now.</param>
/// <param name="Comments">Its inline comments, when they were asked for.</param>
public sealed record PostedReview(
    string Id, string Author, string State, string Body, string? CommitId, DateTimeOffset? SubmittedAt, string? Url,
    IReadOnlyList<PostedComment> Comments);

/// <summary>A refusal or failure from the provider, with a message a person can act on.</summary>
public sealed class ProviderException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;

    /// <summary>
    /// 401 and 403 that aren't rate limits, or no token to send at all: retrying with the same
    /// token won't help (spec outcome <c>fatal</c>).
    /// </summary>
    public bool IsAuth => Status is 401 or 403 || NoToken;

    /// <summary>The token source had nothing to give: an unset variable, or an Azure CLI that isn't logged in.</summary>
    public bool NoToken { get; init; }

    /// <summary>A post that failed in a way that doesn't say whether it landed: a 5xx, a timeout, a dropped connection.</summary>
    public bool MayHaveLanded { get; init; }
}

/// <summary>How a provider takes a review.</summary>
public enum ReviewPosting
{
    /// <summary>GitHub: the summary, the event and every inline comment in one request, which lands whole or not at all.</summary>
    Atomic,

    /// <summary>Azure DevOps and Bitbucket: a thread per request, the summary first, then the vote. A crash can leave part of a review.</summary>
    PerThread,
}

/// <summary>One thread to start, for a provider that posts a thread at a time.</summary>
/// <param name="CommitId">The reviewed head, which the inline comment's line is on.</param>
/// <param name="Inline">Where an inline comment goes; null for the summary.</param>
/// <param name="ReviewId">The summary's thread, kept on each inline thread, so a lost store can find which review a thread came from.</param>
public sealed record ThreadPost(int Number, string CommitId, string Body, ReviewPostComment? Inline, string? ReviewId);

/// <summary>A provider adapter (spec "Provider adapter"): GitHub, Azure DevOps and Bitbucket.</summary>
public interface IProvider
{
    ReviewPosting Posting { get; }

    /// <summary>The login the token posts as: the self-review check and recognising rtfd's own threads.</summary>
    Task<string> WhoAmIAsync(CancellationToken cancellationToken = default);

    Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken cancellationToken = default);

    Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>The CI state of the PR's head <paramref name="sha"/>: GitHub's is per commit, Azure DevOps' per PR.</summary>
    Task<CiState> GetCiStateAsync(int number, string sha, CancellationToken cancellationToken = default);

    /// <summary>The PR's inline review threads, with their comments, oldest first.</summary>
    Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>Where git fetches the PR from, with the credential for the fetch.</summary>
    Task<FetchSource> FetchSourceAsync(PullRequest pullRequest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts one review, on an <see cref="ReviewPosting.Atomic"/> provider. Not retried on a 5xx
    /// or a network error: the review may have landed, and only the write-ahead record's body
    /// hashes can tell (spec "Posting and idempotency").
    /// </summary>
    Task<PostedReview> PostReviewAsync(ReviewPost post, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts one thread of a review, on a <see cref="ReviewPosting.PerThread"/> provider: the
    /// summary, or an inline comment. Not retried on a 5xx or a network error, like a review.
    /// </summary>
    Task<PostedComment> StartThreadAsync(ThreadPost post, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the posting identity's vote, on a <see cref="ReviewPosting.PerThread"/> provider:
    /// approve or request changes. Setting the same vote twice changes nothing.
    /// </summary>
    Task SetVoteAsync(int number, Verdict verdict, CancellationToken cancellationToken = default);

    /// <summary>
    /// One review with its inline comments, or null when it doesn't exist. On a per-thread
    /// provider the review is its summary thread and the threads that name it.
    /// </summary>
    Task<PostedReview?> GetReviewAsync(int number, string reviewId, CancellationToken cancellationToken = default);

    /// <summary>Every review on the PR, without their comments: for matching a write-ahead record after a crash.</summary>
    Task<IReadOnlyList<PostedReview>> ListReviewsAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>Every review comment on the PR, replies included, with its review and line: for rebuilding a lost store.</summary>
    Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>A reply on the thread <paramref name="commentId"/> starts. Not retried on a 5xx or a network error, like a review.</summary>
    Task<ReviewComment> ReplyAsync(int number, string commentId, string body, CancellationToken cancellationToken = default);

    /// <summary>Marks a review thread resolved. Resolving one that is already resolved changes nothing.</summary>
    Task ResolveThreadAsync(int number, string threadId, CancellationToken cancellationToken = default);
}
