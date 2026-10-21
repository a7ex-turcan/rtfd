using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rtfd.Providers.AzureDevOps;

// The shapes rtfd reads from and sends to Azure DevOps' REST API (7.1), and only the fields it
// uses. Source-generated: no reflection, so they survive Native AOT.

internal sealed record AdoList<T>(List<T> Value);

internal sealed record AdoIdentity(string? Id, string? UniqueName, string? DisplayName);

internal sealed record AdoAccountProperty([property: JsonPropertyName("$value")] string? Value);

internal sealed record AdoUserProperties(AdoAccountProperty? Account);

internal sealed record AdoConnectedUser(string Id, string? ProviderDisplayName, AdoUserProperties? Properties);

internal sealed record AdoConnectionData(AdoConnectedUser? AuthenticatedUser);

internal sealed record AdoProjectRef(string Id, string Name, string? Visibility);

internal sealed record AdoRepository(string Id, string Name, string? DefaultBranch, bool? IsDisabled, AdoProjectRef Project);

internal sealed record AdoCommitRef(string CommitId);

internal sealed record AdoPullRequest(
    int PullRequestId, string Status, AdoIdentity CreatedBy, string Title, string? Description, string SourceRefName, string TargetRefName,
    bool IsDraft, AdoCommitRef? LastMergeSourceCommit, AdoCommitRef? LastMergeTargetCommit, JsonElement? ForkSource);

internal sealed record AdoIteration(int Id, AdoCommitRef? SourceRefCommit, AdoCommitRef? CommonRefCommit);

internal sealed record AdoChangeItem(string? Path);

internal sealed record AdoChangeEntry(int ChangeTrackingId, AdoChangeItem? Item);

internal sealed record AdoIterationChanges(List<AdoChangeEntry>? ChangeEntries, int? NextSkip, int? NextTop);

internal sealed record AdoPosition(int Line, int Offset);

internal sealed record AdoThreadContext(
    string? FilePath, AdoPosition? RightFileStart, AdoPosition? RightFileEnd, AdoPosition? LeftFileStart, AdoPosition? LeftFileEnd);

internal sealed record AdoIterationContext(int FirstComparingIteration, int SecondComparingIteration);

internal sealed record AdoTrackingCriteria(string? OrigFilePath, AdoPosition? OrigRightFileStart, AdoPosition? OrigLeftFileStart);

internal sealed record AdoPullRequestThreadContext(AdoIterationContext? IterationContext, int? ChangeTrackingId, AdoTrackingCriteria? TrackingCriteria);

internal sealed record AdoComment(int Id, int ParentCommentId, AdoIdentity? Author, string? Content, DateTimeOffset? PublishedDate, string? CommentType, bool? IsDeleted);

/// <summary>A property as Azure DevOps sends it back: <c>{"$type": "System.String", "$value": "..."}</c>.</summary>
internal sealed record AdoPropertyValue([property: JsonPropertyName("$value")] JsonElement Value);

internal sealed record AdoThread(
    int Id, string? Status, bool? IsDeleted, DateTimeOffset? PublishedDate, AdoThreadContext? ThreadContext,
    AdoPullRequestThreadContext? PullRequestThreadContext, List<AdoComment>? Comments, Dictionary<string, AdoPropertyValue>? Properties);

internal sealed record AdoReviewer(string? Id, int Vote);

internal sealed record AdoStatusContext(string? Name, string? Genre);

internal sealed record AdoStatus(int Id, string? State, int? IterationId, AdoStatusContext? Context, string? Description, string? TargetUrl);

internal sealed record AdoPolicyType(string Id, string? DisplayName);

internal sealed record AdoPolicyConfiguration(bool? IsEnabled, bool? IsBlocking, AdoPolicyType? Type, JsonElement? Settings);

internal sealed record AdoPolicyEvaluation(string Status, AdoPolicyConfiguration? Configuration, JsonElement? Context);

internal sealed record AdoError(string? Message, string? TypeKey);

// Requests. Enums go as numbers: thread status 1 is active, 2 fixed, 4 closed; comment type 1 is text.

internal sealed record AdoCommentRequest(int ParentCommentId, string Content, int CommentType);

/// <summary>A property as Azure DevOps takes it on a new thread.</summary>
internal sealed record AdoPropertyRequest(
    [property: JsonPropertyName("$type")] string Type,
    [property: JsonPropertyName("$value")] string Value);

internal sealed record AdoThreadRequest(
    List<AdoCommentRequest> Comments, int Status, AdoThreadContext? ThreadContext, AdoPullRequestThreadContext? PullRequestThreadContext,
    Dictionary<string, AdoPropertyRequest>? Properties);

internal sealed record AdoThreadStatusRequest(int Status);

internal sealed record AdoVoteRequest(int Vote);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AdoConnectionData))]
[JsonSerializable(typeof(AdoRepository))]
[JsonSerializable(typeof(AdoPullRequest))]
[JsonSerializable(typeof(AdoList<AdoPullRequest>))]
[JsonSerializable(typeof(AdoList<AdoIteration>))]
[JsonSerializable(typeof(AdoIterationChanges))]
[JsonSerializable(typeof(AdoThread))]
[JsonSerializable(typeof(AdoList<AdoThread>))]
[JsonSerializable(typeof(AdoComment))]
[JsonSerializable(typeof(AdoReviewer))]
[JsonSerializable(typeof(AdoList<AdoStatus>))]
[JsonSerializable(typeof(AdoList<AdoPolicyEvaluation>))]
[JsonSerializable(typeof(AdoError))]
[JsonSerializable(typeof(AdoCommentRequest))]
[JsonSerializable(typeof(AdoThreadRequest))]
[JsonSerializable(typeof(AdoThreadStatusRequest))]
[JsonSerializable(typeof(AdoVoteRequest))]
internal sealed partial class AzureDevOpsJson : JsonSerializerContext;
