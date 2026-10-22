using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Rtfd.Config;
using Rtfd.Git;

namespace Rtfd.Providers.Bitbucket;

/// <summary>
/// Bitbucket Cloud through its REST API 2.0. A repo is <c>workspace/repository</c>. What differs
/// from GitHub (checked against a real workspace on 2026-10-22, docs/implementation.md):
/// <list type="bullet">
/// <item>A review is a general comment (the summary), then a comment per inline comment, then the
/// approval or change request: no single request posts it (<see cref="ReviewPosting.PerThread"/>).
/// Each inline comment ends with a hidden line naming the summary (<see cref="ReviewLine"/>), which
/// rtfd strips when it reads the comment back, so a lost store can be rebuilt.</item>
/// <item>An HTML comment shows as text there, so the summary's marker is a link reference
/// definition, which renders as nothing (<see cref="Rtfd.Posting.Poster.Marker"/>).</item>
/// <item>The comment list doesn't say reliably whether a thread is resolved: each thread's first
/// comment is read on its own, which also says whether its line is outdated.</item>
/// <item>PRs name commits by their first 12 characters; rtfd asks for the full SHA once per commit.
/// There's no head ref, but any commit can be fetched by its SHA, a deleted branch's too.</item>
/// <item>An inline comment is accepted on any line, past a file's end or in a file the PR doesn't
/// touch, so only the validator keeps comments on the diff.</item>
/// <item>CI is the head commit's build statuses. The PR list and statuses answer ETags with 304.</item>
/// </list>
/// The token is an Atlassian API token as <c>email:token</c>, sent as basic auth; git takes the
/// token part with a fixed user name. A token without a colon (a workspace or repository access
/// token) goes as a bearer token.
/// </summary>
public sealed class BitbucketProvider : IProvider, IDisposable
{
    public const string Api = "https://api.bitbucket.org/2.0/";

    private const int MaxAttempts = 4;
    private const int MaxCachedPages = 200;
    private const int MaxCachedCommits = 1000;

    private static readonly Regex ReviewLinePattern = new(
        @"\n\n\[//\]: # \(rtfd review=(?<id>[0-9]{1,19})\)\s*$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly string _workspace;
    private readonly string _repository;
    private readonly string _repoPath;
    private readonly TokenSource _tokens;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, CachedPage> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _commits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private string? _uuid;

    /// <param name="slug"><c>workspace/repository</c>.</param>
    /// <param name="tokens">The token, sent with each request, and to git for fetches only.</param>
    /// <param name="handler">Tests pass recorded responses here.</param>
    /// <param name="delay">Waits between retries; tests pass one that doesn't.</param>
    public BitbucketProvider(string slug, TokenSource tokens, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        (_workspace, _repository) = (slug.Split('/')[0], slug.Split('/')[1]);
        _repoPath = $"repositories/{Uri.EscapeDataString(_workspace)}/{Uri.EscapeDataString(_repository)}";
        _tokens = tokens;
        _delay = delay ?? Task.Delay;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(Api);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("rtfd", EntryPoint.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public ReviewPosting Posting => ReviewPosting.PerThread;

    private string Slug => $"{_workspace}/{_repository}";

    /// <summary>
    /// The hidden last line of an inline comment, naming the summary of the review it belongs to:
    /// a link reference definition, which Bitbucket renders as nothing.
    /// </summary>
    public static string ReviewLine(string reviewId) => $"[//]: # (rtfd review={reviewId})";

    /// <summary>
    /// The account's nickname, which every author object carries too: Bitbucket shows no email or
    /// user name on PRs and comments.
    /// </summary>
    public async Task<string> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetAsync("user", BitbucketJson.Default.BbUser, cancellationToken).ConfigureAwait(false);
        _uuid = user.Uuid;
        return NameOf(user) ?? throw new ProviderException("Bitbucket didn't say who the token belongs to.", 401);
    }

    public async Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default)
    {
        var repo = await GetAsync(_repoPath, BitbucketJson.Default.BbRepository, cancellationToken).ConfigureAwait(false);
        return new RepositoryInfo(!repo.IsPrivate, repo.Mainbranch?.Name ?? "");
    }

    public async Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken cancellationToken = default)
    {
        var pulls = new List<PullRequest>();
        for (string? next = $"{_repoPath}/pullrequests?state=OPEN&pagelen=50"; next is not null;)
        {
            var page = await GetCachedAsync(next, BitbucketJson.Default.BbPageBbPullRequest, cancellationToken).ConfigureAwait(false);
            foreach (var pr in page.Values)
            {
                pulls.Add(await MapAsync(pr, cancellationToken).ConfigureAwait(false));
            }

            next = page.Next;
        }

        return pulls;
    }

    public async Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default) =>
        await MapAsync(await GetAsync($"{_repoPath}/pullrequests/{number}", BitbucketJson.Default.BbPullRequest, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    private async Task<PullRequest> MapAsync(BbPullRequest pr, CancellationToken cancellationToken) => new()
    {
        Number = pr.Id,
        Title = pr.Title,
        Body = pr.Description ?? "",
        Author = AuthorOf(pr.Author),
        IsDraft = pr.Draft == true,

        // A fork whose repo was deleted has no source repo at all.
        IsFork = pr.Source.Repository?.FullName is not { } source || !string.Equals(source, pr.Destination.Repository?.FullName, StringComparison.OrdinalIgnoreCase),

        // Open is open; merged, declined and superseded are closed.
        State = pr.State == "OPEN" ? "open" : "closed",
        HeadSha = await FullShaAsync(pr.Source.Commit?.Hash, cancellationToken).ConfigureAwait(false),
        HeadRef = pr.Source.Branch?.Name ?? "",
        BaseRef = pr.Destination.Branch?.Name ?? "",
        BaseSha = await FullShaAsync(pr.Destination.Commit?.Hash, cancellationToken).ConfigureAwait(false),
        Url = pr.Links?.Html?.Href ?? $"https://bitbucket.org/{Slug}/pull-requests/{pr.Id}",
    };

    /// <summary>The full SHA of a commit a PR names by its first 12 characters, asked for once per commit.</summary>
    private async Task<string> FullShaAsync(string? hash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length == 40)
        {
            return hash ?? "";
        }

        lock (_lock)
        {
            if (_commits.TryGetValue(hash, out var known))
            {
                return known;
            }
        }

        var commit = await GetAsync($"{_repoPath}/commit/{Uri.EscapeDataString(hash)}", BitbucketJson.Default.BbCommit, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (_commits.Count >= MaxCachedCommits)
            {
                _commits.Clear();
            }

            _commits[hash] = commit.Hash;
        }

        return commit.Hash;
    }

    private static string? NameOf(BbUser? user) => user?.Nickname ?? user?.DisplayName;

    private static string AuthorOf(BbUser? user) => NameOf(user) ?? "unknown";

    // --- CI -----------------------------------------------------------------

    /// <summary>The build statuses on <paramref name="sha"/>: one per key, kept up to date by whatever reports it.</summary>
    public async Task<CiState> GetCiStateAsync(int number, string sha, CancellationToken cancellationToken = default)
    {
        var checks = new List<CiCheck>();
        for (string? next = $"{_repoPath}/commit/{Uri.EscapeDataString(sha)}/statuses?pagelen=100"; next is not null;)
        {
            var page = await GetCachedAsync(next, BitbucketJson.Default.BbPageBbStatus, cancellationToken).ConfigureAwait(false);
            checks.AddRange(page.Values.Select(s =>
            {
                var name = s.Name is { Length: > 0 } named ? named : s.Key;
                return s.State switch
                {
                    "SUCCESSFUL" => new CiCheck(name, "completed", "success", s.Url),
                    "FAILED" => new CiCheck(name, "completed", "failure", s.Url),
                    "STOPPED" => new CiCheck(name, "completed", "cancelled", s.Url),
                    _ => new CiCheck(name, "pending", null, s.Url),
                };
            }));
            next = page.Next;
        }

        return new CiState(GitHub.GitHubProvider.Summarize(checks), checks);
    }

    // --- comments -----------------------------------------------------------

    /// <summary>Every comment on the PR, deleted ones too (a reply's thread is found through them), oldest first.</summary>
    private async Task<List<BbComment>> CommentsAsync(int number, CancellationToken cancellationToken)
    {
        var comments = new List<BbComment>();
        for (string? next = $"{_repoPath}/pullrequests/{number}/comments?pagelen=100"; next is not null;)
        {
            var page = await GetAsync(next, BitbucketJson.Default.BbPageBbComment, cancellationToken).ConfigureAwait(false);
            comments.AddRange(page.Values);
            next = page.Next;
        }

        return [.. comments.OrderBy(c => c.Id)];
    }

    /// <summary>Published and not deleted. A pending comment is a draft only its author sees.</summary>
    private static bool IsLive(BbComment comment) => comment.Deleted != true && comment.Pending != true;

    private static bool IsInline(BbComment comment) => comment.Inline?.Path is { Length: > 0 };

    /// <summary>Only the single-comment endpoint fills this in; the list leaves it empty or out.</summary>
    private static bool IsResolved(BbComment comment) => comment.Resolution?.Type is { Length: > 0 };

    /// <summary>The comment that starts <paramref name="comment"/>'s thread: replies can answer replies.</summary>
    private static long RootOf(BbComment comment, Dictionary<long, BbComment> byId)
    {
        var current = comment;
        for (var hops = 0; hops < 1000 && current.Parent is { } parent && byId.TryGetValue(parent.Id, out var up); hops++)
        {
            current = up;
        }

        return current.Id;
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A comment's text as rtfd sent it: without the hidden line naming its review.</summary>
    private static string BodyOf(BbComment comment)
    {
        var raw = comment.Content?.Raw ?? "";
        return ReviewLinePattern.Match(raw) is { Success: true } match ? raw[..match.Index] : raw;
    }

    /// <summary>The review an inline comment's hidden line names, if it has one.</summary>
    private static string? ReviewOf(BbComment comment) =>
        ReviewLinePattern.Match(comment.Content?.Raw ?? "") is { Success: true } match ? match.Groups["id"].Value : null;

    private static int? OriginalLineOf(BbInline inline) => inline.To ?? inline.From;

    public async Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(int number, CancellationToken cancellationToken = default)
    {
        var comments = await CommentsAsync(number, cancellationToken).ConfigureAwait(false);
        var byId = comments.ToDictionary(c => c.Id);
        var replies = comments.Where(c => c.Parent is not null && IsLive(c)).ToLookup(c => RootOf(c, byId));
        var threads = new List<ReviewThread>();
        foreach (var root in comments.Where(c => c.Parent is null && IsLive(c) && IsInline(c)))
        {
            // The list doesn't say reliably whether a thread is resolved; the comment on its own does.
            var single = await GetAsync($"{_repoPath}/pullrequests/{number}/comments/{Id(root.Id)}", BitbucketJson.Default.BbComment, cancellationToken).ConfigureAwait(false);
            var inline = single.Inline ?? root.Inline!;
            var outdated = inline.Outdated == true;
            threads.Add(new ReviewThread
            {
                Id = Id(root.Id),
                IsResolved = IsResolved(single),
                IsOutdated = outdated,
                ResolvedBy = IsResolved(single) ? NameOf(single.Resolution!.User) : null,
                Path = inline.Path,
                Line = outdated ? null : OriginalLineOf(inline),
                OriginalLine = OriginalLineOf(inline),
                Comments = [.. replies[root.Id].Prepend(root).Select(c => new ThreadComment(Id(c.Id), AuthorOf(c.User), BodyOf(c), c.CreatedOn ?? DateTimeOffset.MinValue))],
            });
        }

        return threads;
    }

    public async Task<FetchSource> FetchSourceAsync(PullRequest pullRequest, CancellationToken cancellationToken = default)
    {
        var token = await _tokens.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // No head ref on Bitbucket; the head commit itself, by SHA, works even after the branch is deleted.
        return new FetchSource($"https://bitbucket.org/{Slug}.git", pullRequest.HeadSha, new GitCredential($"AUTHORIZATION: basic {GitCredentials(token)}"));
    }

    // --- posting ------------------------------------------------------------

    /// <summary>Bitbucket takes a review a comment at a time (<see cref="ReviewPosting.PerThread"/>).</summary>
    public Task<PostedReview> PostReviewAsync(ReviewPost post, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Bitbucket posts a review a comment at a time");

    /// <summary>
    /// The summary goes as a general comment; an inline comment on its line, <c>to</c> in the new
    /// file or <c>from</c> in the old one, with the hidden line naming the summary. Bitbucket anchors
    /// it to the PR as it is now, so a push during the review can move it.
    /// </summary>
    public async Task<PostedComment> StartThreadAsync(ThreadPost post, CancellationToken cancellationToken = default)
    {
        var raw = post.ReviewId is { } review ? $"{post.Body}\n\n{ReviewLine(review)}" : post.Body;
        var inline = post.Inline is not { } where ? null
            : where.Side == DiffSide.Old ? new BbInlineRequest(where.Path, where.Line, null) : new BbInlineRequest(where.Path, null, where.Line);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_repoPath}/pullrequests/{post.Number}/comments")
        {
            Content = JsonContent.Create(new BbCommentRequest(new BbContentRequest(raw), inline, null), BitbucketJson.Default.BbCommentRequest),
        }, cancellationToken, idempotent: false).ConfigureAwait(false);

        var comment = await ReadAsync(response, BitbucketJson.Default.BbComment, cancellationToken).ConfigureAwait(false);
        return new PostedComment(Id(comment.Id), post.Inline?.Path ?? "", post.Inline?.Line, post.Body) { Url = comment.Links?.Html?.Href };
    }

    /// <summary>
    /// Approve or request changes; each replaces the other, and setting one again changes nothing.
    /// A comment leaves the vote alone. Bitbucket takes a vote on a draft, and on your own PR, which
    /// the self-review check keeps rtfd from casting.
    /// </summary>
    public async Task SetVoteAsync(int number, Verdict verdict, CancellationToken cancellationToken = default)
    {
        var (path, state) = verdict switch
        {
            Verdict.Approve => ("approve", "approved"),
            Verdict.RequestChanges => ("request-changes", "changes_requested"),
            _ => ((string?)null, (string?)null),
        };
        if (path is null)
        {
            return;
        }

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_repoPath}/pullrequests/{number}/{path}"), cancellationToken).ConfigureAwait(false);
        var participant = await ReadAsync(response, BitbucketJson.Default.BbParticipant, cancellationToken).ConfigureAwait(false);
        if (participant.State != state)
        {
            throw new ProviderException($"Bitbucket left the vote at {participant.State ?? "none"}, not {state}.");
        }
    }

    /// <summary>
    /// Takes the posting identity's approval or change request back. rtfd never does this to a
    /// PR; the live tests do, to leave the scratch PR without a vote.
    /// </summary>
    public async Task RemoveVoteAsync(int number, CancellationToken cancellationToken = default)
    {
        var path = await VoteStateAsync(number, cancellationToken).ConfigureAwait(false) switch
        {
            "APPROVED" => "approve",
            "CHANGES_REQUESTED" => "request-changes",
            _ => null,
        };
        if (path is not null)
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{_repoPath}/pullrequests/{number}/{path}"), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The posting identity's vote as a GitHub review state, for the read-back.</summary>
    private async Task<string> VoteStateAsync(int number, CancellationToken cancellationToken)
    {
        if (_uuid is null)
        {
            await WhoAmIAsync(cancellationToken).ConfigureAwait(false);
        }

        var pr = await GetAsync($"{_repoPath}/pullrequests/{number}", BitbucketJson.Default.BbPullRequest, cancellationToken).ConfigureAwait(false);
        return pr.Participants?.FirstOrDefault(p => p.User?.Uuid == _uuid) switch
        {
            { State: "approved" } or { Approved: true } => "APPROVED",
            { State: "changes_requested" } => "CHANGES_REQUESTED",
            _ => "COMMENTED",
        };
    }

    /// <summary>The summary comment <paramref name="reviewId"/> and the inline comments that name it, with the identity's vote as the state.</summary>
    public async Task<PostedReview?> GetReviewAsync(int number, string reviewId, CancellationToken cancellationToken = default)
    {
        var comments = await CommentsAsync(number, cancellationToken).ConfigureAwait(false);
        var summary = comments.FirstOrDefault(c => Id(c.Id) == reviewId && c.Parent is null && IsLive(c) && !IsInline(c));
        if (summary is null)
        {
            return null;
        }

        var inline = comments.Where(c => c.Parent is null && IsLive(c) && IsInline(c) && ReviewOf(c) == reviewId)
            .Select(c => new PostedComment(Id(c.Id), c.Inline!.Path!, OriginalLineOf(c.Inline), BodyOf(c)) { Url = c.Links?.Html?.Href })
            .ToList();
        return new PostedReview(reviewId, AuthorOf(summary.User), await VoteStateAsync(number, cancellationToken).ConfigureAwait(false), BodyOf(summary), null,
            summary.CreatedOn, summary.Links?.Html?.Href, inline);
    }

    /// <summary>The PR's general comments that start a thread, each a candidate summary, oldest first.</summary>
    public async Task<IReadOnlyList<PostedReview>> ListReviewsAsync(int number, CancellationToken cancellationToken = default)
    {
        var comments = await CommentsAsync(number, cancellationToken).ConfigureAwait(false);
        return [.. comments.Where(c => c.Parent is null && IsLive(c) && !IsInline(c))
            .Select(c => new PostedReview(Id(c.Id), AuthorOf(c.User), "COMMENTED", BodyOf(c), null, c.CreatedOn, c.Links?.Html?.Href, []))];
    }

    /// <summary>Every comment on an inline thread, the first naming its review through its hidden line.</summary>
    public async Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(int number, CancellationToken cancellationToken = default)
    {
        var comments = await CommentsAsync(number, cancellationToken).ConfigureAwait(false);
        var byId = comments.ToDictionary(c => c.Id);

        // A reply carries its thread's anchor too, so every comment on an inline thread is inline.
        return [.. comments.Where(c => IsLive(c) && IsInline(c)).Select(c =>
        {
            var root = c.Parent is null;
            return new ReviewComment(Id(c.Id), AuthorOf(c.User), root ? ReviewOf(c) : null, root ? null : Id(RootOf(c, byId)), c.Inline!.Path!,
                c.Inline.Outdated == true ? null : OriginalLineOf(c.Inline), OriginalLineOf(c.Inline), BodyOf(c), c.CreatedOn);
        })];
    }

    public async Task<ReviewComment> ReplyAsync(int number, string commentId, string body, CancellationToken cancellationToken = default)
    {
        var parent = IdOf(commentId);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_repoPath}/pullrequests/{number}/comments")
        {
            Content = JsonContent.Create(new BbCommentRequest(new BbContentRequest(body), null, new BbCommentRef(parent)), BitbucketJson.Default.BbCommentRequest),
        }, cancellationToken, idempotent: false).ConfigureAwait(false);

        var comment = await ReadAsync(response, BitbucketJson.Default.BbComment, cancellationToken).ConfigureAwait(false);
        return new ReviewComment(Id(comment.Id), AuthorOf(comment.User), null, commentId, comment.Inline?.Path ?? "", null, null,
            comment.Content?.Raw is null ? body : BodyOf(comment), comment.CreatedOn);
    }

    /// <summary>Resolves the thread <paramref name="threadId"/> starts. Bitbucket answers 409 for one already resolved, which is what was asked for.</summary>
    public async Task ResolveThreadAsync(int number, string threadId, CancellationToken cancellationToken = default)
    {
        var id = IdOf(threadId);
        try
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_repoPath}/pullrequests/{number}/comments/{Id(id)}/resolve"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (ex.Status == 409)
        {
        }
    }

    /// <summary>A comment id, as rtfd names it; anything else isn't one of its comments.</summary>
    private static long IdOf(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var comment) && comment > 0
            ? comment
            : throw new ProviderException($"'{id}' isn't a Bitbucket comment id.", 404);

    public void Dispose() => _http.Dispose();

    // --- HTTP -----------------------------------------------------------------

    /// <summary><c>email:token</c> is an Atlassian API token, basic auth; a token without a colon is an access token, bearer.</summary>
    private static AuthenticationHeaderValue Authorization(ProviderToken token) =>
        token.Value.Contains(':', StringComparison.Ordinal)
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(token.Value)))
            : new AuthenticationHeaderValue("Bearer", token.Value);

    /// <summary>
    /// Git refuses the email as the user name: an API token goes with <c>x-bitbucket-api-token-auth</c>
    /// (checked 2026-10-22), an access token with <c>x-token-auth</c>.
    /// </summary>
    private static string GitCredentials(ProviderToken token)
    {
        var colon = token.Value.IndexOf(':', StringComparison.Ordinal);
        var pair = colon >= 0 ? $"x-bitbucket-api-token-auth:{token.Value[(colon + 1)..]}" : $"x-token-auth:{token.Value}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(pair));
    }

    /// <summary>
    /// A GET that <c>serve</c> repeats every poll: sent with the ETag of the last answer, so
    /// Bitbucket says 304 when nothing changed (checked 2026-10-22).
    /// </summary>
    private async Task<T> GetCachedAsync<T>(string url, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        CachedPage? cached;
        lock (_lock)
        {
            cached = _pages.GetValueOrDefault(url);
        }

        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cached is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            }

            return request;
        }, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            Remember(url, cached with { Used = DateTimeOffset.UtcNow });
            return Parse(cached.Body, type, url);
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (response.Headers.ETag is { } etag)
        {
            Remember(url, new CachedPage(etag.ToString(), body, DateTimeOffset.UtcNow));
        }

        return Parse(body, type, url);
    }

    private void Remember(string url, CachedPage page)
    {
        lock (_lock)
        {
            // Statuses are per commit, so pages for old heads pile up over days: keep the last hour's.
            if (_pages.Count >= MaxCachedPages && !_pages.ContainsKey(url))
            {
                foreach (var stale in _pages.Where(p => p.Value.Used < DateTimeOffset.UtcNow.AddHours(-1)).Select(p => p.Key).ToList())
                {
                    _pages.Remove(stale);
                }

                if (_pages.Count >= MaxCachedPages)
                {
                    _pages.Clear();
                }
            }

            _pages[url] = page;
        }
    }

    private sealed record CachedPage(string ETag, byte[] Body, DateTimeOffset Used);

    private static T Parse<T>(byte[] body, JsonTypeInfo<T> type, string url)
    {
        try
        {
            return JsonSerializer.Deserialize(body, type) ?? throw new ProviderException($"Bitbucket sent an empty answer to {Path(url)}");
        }
        catch (JsonException ex)
        {
            throw new ProviderException($"Bitbucket sent an answer rtfd can't read ({Path(url)}): {ex.Message}");
        }
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, type, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException($"Bitbucket sent an empty answer to {response.RequestMessage?.RequestUri?.AbsolutePath}");
        }
        catch (JsonException ex)
        {
            throw new ProviderException($"Bitbucket sent an answer rtfd can't read ({response.RequestMessage?.RequestUri?.AbsolutePath}): {ex.Message}");
        }
    }

    /// <summary>
    /// Sends with the token. A read retries 5xx, 429 and network errors; a request that isn't
    /// <paramref name="idempotent"/> (a comment, a reply) retries only a 429, which Bitbucket answers
    /// before doing anything. After a 5xx or a dropped connection the comment may have landed, so
    /// that's for the write-ahead record to find out, not a retry.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancellationToken, bool idempotent = true)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = create();
            request.Headers.Authorization = Authorization(await _tokens.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (idempotent && attempt < MaxAttempts)
            {
                await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderException($"can't reach Bitbucket: {ex.Message}") { MayHaveLanded = !idempotent };
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!idempotent)
                {
                    throw new ProviderException("Bitbucket didn't answer in time") { MayHaveLanded = true };
                }

                if (attempt < MaxAttempts)
                {
                    await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new ProviderException("Bitbucket didn't answer in time");
            }

            // A 304 only ever answers a conditional GET, whose caller has the page it stands for.
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
            {
                return response;
            }

            var status = (int)response.StatusCode;
            var message = await ErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
            var limited = response.StatusCode == HttpStatusCode.TooManyRequests;
            var wait = limited || status >= 500 ? RetryWait(response, attempt) : (TimeSpan?)null;
            response.Dispose();
            if (wait is { } delay && attempt < MaxAttempts && (idempotent || limited))
            {
                await _delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new ProviderException(Describe(status, request.RequestUri?.AbsolutePath ?? "", message), status)
            {
                MayHaveLanded = !idempotent && status >= 500,
            };
        }
    }

    private static TimeSpan RetryWait(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delta > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : delta;
        }

        return Backoff(attempt);
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>Bitbucket's <c>{"type": "error", "error": {"message", "detail", "fields"}}</c>, as one line.</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var error = JsonSerializer.Deserialize(text, BitbucketJson.Default.BbError)?.Error;
            var fields = error?.Fields is { ValueKind: JsonValueKind.Object } map
                ? map.EnumerateObject().Select(f => $"{f.Name}: {(f.Value.ValueKind == JsonValueKind.Array ? string.Join(" ", f.Value.EnumerateArray().Select(v => v.ToString())) : f.Value.ToString())}")
                : [];
            return string.Join("; ", new[] { error?.Message, error?.Detail }.Concat(fields).OfType<string>().Where(p => p.Length > 0));
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
            return "";
        }
    }

    private static string Path(string url) => url.Split('?')[0];

    private string Describe(int status, string url, string message)
    {
        var detail = message.Length > 0 ? $": {message}" : "";
        var where = Path(url);
        return status switch
        {
            401 => $"Bitbucket refused the token in {_tokens.Describe} (401){detail}. It should be your Atlassian account's email and an API token, as email:token.",
            403 => $"Bitbucket refused {where} (403){detail}. The token may lack a scope, or access to {Slug}.",
            404 => $"Bitbucket has no {where} (404), or the token can't see it{detail}.",
            _ => $"Bitbucket answered {status} to {where}{detail}.",
        };
    }
}
