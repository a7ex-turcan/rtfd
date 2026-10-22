using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rtfd.Providers.Bitbucket;

// The shapes rtfd reads from and sends to Bitbucket Cloud's REST API (2.0), and only the fields it
// uses. Source-generated: no reflection, so they survive Native AOT.

/// <summary>A page of a list; <c>next</c> is the next page's full URL, absent on the last.</summary>
internal sealed record BbPage<T>(List<T> Values, string? Next);

/// <summary>An account. Bitbucket shows no email or user name on PRs and comments, only these.</summary>
internal sealed record BbUser(string? DisplayName, string? Nickname, string? Uuid, string? AccountId);

internal sealed record BbBranch(string Name);

internal sealed record BbRepository(string FullName, bool IsPrivate, BbBranch? Mainbranch);

/// <summary>A commit. In a PR's source and destination the hash is abbreviated to 12 characters.</summary>
internal sealed record BbCommit(string Hash);

internal sealed record BbRepositoryRef(string? FullName);

internal sealed record BbEndpoint(BbBranch? Branch, BbCommit? Commit, BbRepositoryRef? Repository);

internal sealed record BbLink(string Href);

internal sealed record BbLinks(BbLink? Html);

internal sealed record BbParticipant(BbUser? User, string? Role, bool Approved, string? State);

internal sealed record BbPullRequest(
    int Id, string Title, string? Description, string State, BbUser? Author, bool? Draft, BbEndpoint Source, BbEndpoint Destination,
    BbLinks? Links, List<BbParticipant>? Participants);

internal sealed record BbStatus(string Key, string? Name, string State, string? Url);

internal sealed record BbContent(string? Raw);

/// <summary>Where an inline comment sits: <c>to</c> is a line of the new file, <c>from</c> of the old one.</summary>
internal sealed record BbInline(string? Path, int? From, int? To, bool? Outdated);

internal sealed record BbCommentRef(long Id);

/// <summary>Set once a thread is resolved; only the single-comment endpoint reports it reliably.</summary>
internal sealed record BbResolution(string? Type, BbUser? User);

internal sealed record BbComment(
    long Id, BbContent? Content, BbUser? User, DateTimeOffset? CreatedOn, bool? Deleted, bool? Pending, BbInline? Inline, BbCommentRef? Parent,
    BbResolution? Resolution, BbLinks? Links);

internal sealed record BbErrorDetail(string? Message, string? Detail, JsonElement? Fields);

internal sealed record BbError(BbErrorDetail? Error);

// Requests.

internal sealed record BbContentRequest(string Raw);

internal sealed record BbInlineRequest(string Path, int? From, int? To);

internal sealed record BbCommentRequest(BbContentRequest Content, BbInlineRequest? Inline, BbCommentRef? Parent);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BbUser))]
[JsonSerializable(typeof(BbRepository))]
[JsonSerializable(typeof(BbCommit))]
[JsonSerializable(typeof(BbPullRequest))]
[JsonSerializable(typeof(BbPage<BbPullRequest>))]
[JsonSerializable(typeof(BbPage<BbStatus>))]
[JsonSerializable(typeof(BbComment))]
[JsonSerializable(typeof(BbPage<BbComment>))]
[JsonSerializable(typeof(BbParticipant))]
[JsonSerializable(typeof(BbError))]
[JsonSerializable(typeof(BbCommentRequest))]
internal sealed partial class BitbucketJson : JsonSerializerContext;
