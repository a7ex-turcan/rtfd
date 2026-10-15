using System.Text.Json.Serialization;

namespace Rtfd.Providers.GitHub;

// The shapes rtfd reads from GitHub, and only the fields it uses. Source-generated: no
// reflection, so they survive Native AOT.

internal sealed record GhUser(string Login);

internal sealed record GhRepoRef(string FullName);

internal sealed record GhBranch(string Ref, string Sha, GhRepoRef? Repo);

internal sealed record GhPull(
    int Number, string Title, string? Body, string State, bool Draft, GhUser User, GhBranch Head, GhBranch Base, string HtmlUrl);

internal sealed record GhRepository(string FullName, bool Private, string? Visibility, string DefaultBranch, string CloneUrl);

internal sealed record GhCheckRun(string Name, string Status, string? Conclusion, string? HtmlUrl);

internal sealed record GhCheckRuns(int TotalCount, List<GhCheckRun> CheckRuns);

internal sealed record GhStatus(string Context, string State, string? Description, string? TargetUrl);

internal sealed record GhCombinedStatus(string State, int TotalCount, List<GhStatus> Statuses);

internal sealed record GhError(string? Message, List<GhErrorDetail>? Errors);

internal sealed record GhErrorDetail(string? Message, string? Field, string? Code, string? Resource);

internal sealed record GhReviewCommentRequest(string Path, int Line, string Side, string Body);

internal sealed record GhReviewRequest(string CommitId, string Body, string Event, List<GhReviewCommentRequest> Comments);

internal sealed record GhReview(long Id, GhUser? User, string? Body, string State, string? CommitId, DateTimeOffset? SubmittedAt, string? HtmlUrl);

internal sealed record GhReviewComment(
    long Id, string Path, int? Line, int? OriginalLine, string Body, GhUser? User, long? PullRequestReviewId, long? InReplyToId, DateTimeOffset? CreatedAt);

internal sealed record GhReplyRequest(string Body);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GhUser))]
[JsonSerializable(typeof(GhPull))]
[JsonSerializable(typeof(List<GhPull>))]
[JsonSerializable(typeof(GhRepository))]
[JsonSerializable(typeof(GhCheckRuns))]
[JsonSerializable(typeof(GhCombinedStatus))]
[JsonSerializable(typeof(GhError))]
[JsonSerializable(typeof(GhReviewRequest))]
[JsonSerializable(typeof(GhReview))]
[JsonSerializable(typeof(List<GhReview>))]
[JsonSerializable(typeof(GhReviewComment))]
[JsonSerializable(typeof(List<GhReviewComment>))]
[JsonSerializable(typeof(GhReplyRequest))]
internal sealed partial class GitHubJson : JsonSerializerContext;

// GraphQL: review threads and their resolution exist only there.

internal sealed record GqlThreadsVariables(string Owner, string Name, int Number, string? After);

internal sealed record GqlRequest(string Query, GqlThreadsVariables Variables);

internal sealed record GqlPageInfo(bool HasNextPage, string? EndCursor);

internal sealed record GqlActor(string Login);

internal sealed record GqlComment(long DatabaseId, GqlActor? Author, string Body, DateTimeOffset CreatedAt);

internal sealed record GqlComments(List<GqlComment> Nodes);

internal sealed record GqlThread(
    string Id, bool IsResolved, bool IsOutdated, GqlActor? ResolvedBy, string? Path, int? Line, int? OriginalLine, GqlComments Comments);

internal sealed record GqlThreads(GqlPageInfo PageInfo, List<GqlThread> Nodes);

internal sealed record GqlPullRequest(GqlThreads ReviewThreads);

internal sealed record GqlRepository(GqlPullRequest? PullRequest);

internal sealed record GqlThreadsData(GqlRepository? Repository);

internal sealed record GqlResolveVariables(string Id);

internal sealed record GqlResolveRequest(string Query, GqlResolveVariables Variables);

internal sealed record GqlResolvedThread(string Id, bool IsResolved);

internal sealed record GqlResolvePayload(GqlResolvedThread? Thread);

internal sealed record GqlResolveData(GqlResolvePayload? ResolveReviewThread);

internal sealed record GqlError(string Message);

internal sealed record GqlResponse<T>(T? Data, List<GqlError>? Errors);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GqlRequest))]
[JsonSerializable(typeof(GqlResponse<GqlThreadsData>))]
[JsonSerializable(typeof(GqlResolveRequest))]
[JsonSerializable(typeof(GqlResponse<GqlResolveData>))]
internal sealed partial class GitHubGraphQlJson : JsonSerializerContext;
