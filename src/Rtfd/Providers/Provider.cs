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
    public int? Line { get; init; }
    public required IReadOnlyList<ThreadComment> Comments { get; init; }
}

public sealed record RepositoryInfo(bool IsPublic, string DefaultBranch);

/// <summary>A refusal or failure from the provider, with a message a person can act on.</summary>
public sealed class ProviderException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;

    /// <summary>401 and 403 that aren't rate limits: retrying won't help (spec outcome <c>fatal</c>).</summary>
    public bool IsAuth => Status is 401 or 403;
}

/// <summary>The read side of a provider adapter (spec "Provider adapter"). Posting arrives in Phase 3.</summary>
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
}
