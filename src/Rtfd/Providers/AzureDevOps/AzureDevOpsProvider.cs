using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Rtfd.Config;
using Rtfd.Git;

namespace Rtfd.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps Services (<c>dev.azure.com</c>) through its REST API, version 7.1. A repo is
/// <c>organization/project/repository</c>. What differs from GitHub (checked against a real
/// organization on 2026-10-21, docs/implementation.md):
/// <list type="bullet">
/// <item>A review is a summary thread, then a thread per inline comment, then the vote: no single
/// request posts it (<see cref="ReviewPosting.PerThread"/>). Each inline thread keeps the summary
/// thread's id in a property (<see cref="ReviewProperty"/>), so a lost store can be rebuilt.</item>
/// <item>Comment ids count from 1 in each thread, so rtfd names a thread's first comment by the
/// thread's id and a reply by <c>thread.comment</c>.</item>
/// <item>There is no PR head ref (only <c>refs/pull/N/merge</c>), but Azure Repos serves any
/// commit by its SHA, a deleted branch's too, so git fetches the head by SHA.</item>
/// <item>CI is the PR's build policies, which say which head they ran on, and its statuses.</item>
/// <item>No ETags on these reads, so polls aren't conditional; at serve's intervals that's cheap.</item>
/// </list>
/// The token is a PAT (basic auth) or an Entra ID token (bearer), which the Azure CLI renews; a
/// wrong tenant or an expired token gets a sign-in page instead of a 401, which counts as a 401.
/// </summary>
public sealed class AzureDevOpsProvider : IProvider, IDisposable
{
    public const string Host = "https://dev.azure.com/";

    /// <summary>The thread property naming the summary thread of the review an inline thread belongs to.</summary>
    public const string ReviewProperty = "rtfd.review";

    private const string Version = "api-version=7.1";
    private const string BuildPolicy = "0609b952-1397-4640-95ec-e00a01b2c241";
    private const int MaxAttempts = 4;

    private readonly HttpClient _http;
    private readonly string _organization;
    private readonly string _project;
    private readonly string _repository;
    private readonly string _repoPath;
    private readonly TokenSource _tokens;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<(int, string), AdoPullRequestThreadContext?> _contexts = [];
    private string? _projectId;
    private string? _identityId;

    /// <param name="slug"><c>organization/project/repository</c>.</param>
    /// <param name="tokens">The token, sent with each request, and to git for fetches only.</param>
    /// <param name="handler">Tests pass recorded responses here.</param>
    /// <param name="delay">Waits between retries; tests pass one that doesn't.</param>
    public AzureDevOpsProvider(string slug, TokenSource tokens, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var parts = slug.Split('/');
        (_organization, _project, _repository) = (parts[0], parts[1], parts[2]);
        _repoPath = $"{Uri.EscapeDataString(_project)}/_apis/git/repositories/{Uri.EscapeDataString(_repository)}";
        _tokens = tokens;
        _delay = delay ?? Task.Delay;

        // A sign-in redirect must reach rtfd as what it is, not as a page of HTML.
        _http = handler is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri($"{Host}{Uri.EscapeDataString(_organization)}/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("rtfd", EntryPoint.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public ReviewPosting Posting => ReviewPosting.PerThread;

    private string Slug => $"{_organization}/{_project}/{_repository}";

    private string WebUrl => $"{Host}{Uri.EscapeDataString(_organization)}/{Uri.EscapeDataString(_project)}/_git/{Uri.EscapeDataString(_repository)}";

    /// <summary>The identity's unique name (its sign-in, usually an email), which PR authors and comment authors carry too.</summary>
    public async Task<string> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetAsync("_apis/connectionData", AzureDevOpsJson.Default.AdoConnectionData, cancellationToken).ConfigureAwait(false);
        var user = data.AuthenticatedUser ?? throw new ProviderException("Azure DevOps didn't say who the token belongs to.", 401);
        _identityId = user.Id;
        return user.Properties?.Account?.Value ?? user.ProviderDisplayName
            ?? throw new ProviderException("Azure DevOps didn't say who the token belongs to.", 401);
    }

    public async Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default)
    {
        var repo = await RepositoryAsync(cancellationToken).ConfigureAwait(false);
        if (repo.IsDisabled == true)
        {
            throw new ProviderException($"{Slug} is disabled on Azure DevOps.", 403);
        }

        return new RepositoryInfo(repo.Project.Visibility == "public", Branch(repo.DefaultBranch ?? ""));
    }

    private async Task<AdoRepository> RepositoryAsync(CancellationToken cancellationToken)
    {
        var repo = await GetAsync($"{_repoPath}?{Version}", AzureDevOpsJson.Default.AdoRepository, cancellationToken).ConfigureAwait(false);
        _projectId = repo.Project.Id;
        return repo;
    }

    public async Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken cancellationToken = default)
    {
        const int page = 100;
        var pulls = new List<PullRequest>();
        for (var skip = 0; ; skip += page)
        {
            var list = await GetAsync($"{_repoPath}/pullrequests?searchCriteria.status=active&$top={page}&$skip={skip}&{Version}",
                AzureDevOpsJson.Default.AdoListAdoPullRequest, cancellationToken).ConfigureAwait(false);
            pulls.AddRange(list.Value.Select(Map));
            if (list.Value.Count < page)
            {
                return pulls;
            }
        }
    }

    public async Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default) =>
        Map(await GetAsync($"{_repoPath}/pullrequests/{number}?{Version}", AzureDevOpsJson.Default.AdoPullRequest, cancellationToken).ConfigureAwait(false));

    private PullRequest Map(AdoPullRequest pr) => new()
    {
        Number = pr.PullRequestId,
        Title = pr.Title,
        Body = pr.Description ?? "",
        Author = pr.CreatedBy.UniqueName ?? pr.CreatedBy.DisplayName ?? "unknown",
        IsDraft = pr.IsDraft,
        IsFork = pr.ForkSource is { ValueKind: JsonValueKind.Object },

        // Active is open; completed (merged) and abandoned are closed.
        State = pr.Status == "active" ? "open" : "closed",
        HeadSha = pr.LastMergeSourceCommit?.CommitId ?? "",
        HeadRef = Branch(pr.SourceRefName),
        BaseRef = Branch(pr.TargetRefName),
        BaseSha = pr.LastMergeTargetCommit?.CommitId ?? "",
        Url = $"{WebUrl}/pullrequest/{pr.PullRequestId}",
    };

    private static string Branch(string refName) => refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;

    // --- CI -----------------------------------------------------------------

    /// <summary>
    /// The PR's build policies and its statuses. A build that ran on an older head is pending for
    /// this one; a build only queued by hand that never started isn't waited for. Statuses count
    /// for the iteration that pushed <paramref name="sha"/>, the newest per name.
    /// </summary>
    public async Task<CiState> GetCiStateAsync(int number, string sha, CancellationToken cancellationToken = default)
    {
        if (_projectId is null)
        {
            await RepositoryAsync(cancellationToken).ConfigureAwait(false);
        }

        var checks = new List<CiCheck>();
        var artifact = Uri.EscapeDataString($"vstfs:///CodeReview/CodeReviewId/{_projectId}/{number}");
        var evaluations = await GetAsync($"{Uri.EscapeDataString(_project)}/_apis/policy/evaluations?artifactId={artifact}&api-version=7.1-preview.1",
            AzureDevOpsJson.Default.AdoListAdoPolicyEvaluation, cancellationToken).ConfigureAwait(false);
        foreach (var evaluation in evaluations.Value.Where(e => e.Configuration?.Type?.Id == BuildPolicy && e.Configuration.IsEnabled != false))
        {
            var name = Text(evaluation.Context, "buildDefinitionName") ?? Text(evaluation.Configuration!.Settings, "displayName") ?? "build";
            var buildId = Number(evaluation.Context, "buildId");
            var url = buildId is { } id ? $"{Host}{Uri.EscapeDataString(_organization)}/{Uri.EscapeDataString(_project)}/_build/results?buildId={id}" : null;
            var ranOn = Text(evaluation.Context, "lastMergeSourceCommitId");
            if (buildId is null && evaluation.Status == "queued" && Flag(evaluation.Configuration!.Settings, "manualQueueOnly"))
            {
                continue;
            }

            checks.Add(evaluation.Status switch
            {
                _ when ranOn is not null && !string.Equals(ranOn, sha, StringComparison.OrdinalIgnoreCase) => new CiCheck(name, "pending", null, url),
                "approved" => new CiCheck(name, "completed", "success", url),
                "rejected" or "broken" => new CiCheck(name, "completed", "failure", url),
                "notApplicable" => new CiCheck(name, "completed", "skipped", url),
                _ => new CiCheck(name, "pending", null, url),
            });
        }

        var iteration = (await IterationsAsync(number, cancellationToken).ConfigureAwait(false)).LastOrDefault(i => i.SourceRefCommit?.CommitId == sha);
        var statuses = await GetAsync($"{_repoPath}/pullRequests/{number}/statuses?{Version}", AzureDevOpsJson.Default.AdoListAdoStatus, cancellationToken).ConfigureAwait(false);
        foreach (var latest in statuses.Value
            .Where(s => iteration is null || s.IterationId is null || s.IterationId == iteration.Id)
            .GroupBy(s => $"{s.Context?.Genre}/{s.Context?.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(s => s.Id)!))
        {
            var name = latest.Context?.Genre is { Length: > 0 } genre ? $"{genre}/{latest.Context.Name}" : latest.Context?.Name ?? "status";
            var check = latest.State switch
            {
                "succeeded" => new CiCheck(name, "completed", "success", latest.TargetUrl),
                "failed" or "error" => new CiCheck(name, "completed", "failure", latest.TargetUrl),
                "notApplicable" => null,
                _ => new CiCheck(name, "pending", null, latest.TargetUrl),
            };
            if (check is not null)
            {
                checks.Add(check);
            }
        }

        return new CiState(GitHub.GitHubProvider.Summarize(checks), checks);
    }

    private static string? Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static bool Flag(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private async Task<List<AdoIteration>> IterationsAsync(int number, CancellationToken cancellationToken) =>
        (await GetAsync($"{_repoPath}/pullRequests/{number}/iterations?{Version}", AzureDevOpsJson.Default.AdoListAdoIteration, cancellationToken).ConfigureAwait(false)).Value;

    // --- threads ------------------------------------------------------------

    /// <summary>Every live text thread on the PR, with its position tracked to the newest iteration.</summary>
    private async Task<List<AdoThread>> ThreadsAsync(int number, CancellationToken cancellationToken)
    {
        var iterations = await IterationsAsync(number, cancellationToken).ConfigureAwait(false);
        var tracked = iterations.Count > 0 ? $"$iteration={iterations[^1].Id}&" : "";
        var threads = await GetAsync($"{_repoPath}/pullRequests/{number}/threads?{tracked}{Version}", AzureDevOpsJson.Default.AdoListAdoThread, cancellationToken).ConfigureAwait(false);
        return [.. threads.Value
            .Where(t => t.IsDeleted != true && Live(t).FirstOrDefault()?.CommentType == "text")
            .OrderBy(t => t.Id)];
    }

    /// <summary>A thread's comments that weren't deleted, oldest first.</summary>
    private static IEnumerable<AdoComment> Live(AdoThread thread) =>
        (thread.Comments ?? []).Where(c => c.IsDeleted != true).OrderBy(c => c.Id);

    private static bool IsInline(AdoThread thread) => thread.ThreadContext?.FilePath is { Length: > 0 };

    /// <summary>Fixed, won't fix, closed and by design all show as resolved; active and pending don't.</summary>
    private static bool IsResolved(AdoThread thread) => thread.Status is "fixed" or "wontFix" or "closed" or "byDesign";

    private static string PathOf(AdoThread thread) =>
        (thread.PullRequestThreadContext?.TrackingCriteria?.OrigFilePath ?? thread.ThreadContext!.FilePath!).TrimStart('/');

    /// <summary>The line now, tracked to the newest iteration; null once the code under it is gone.</summary>
    private static int? LineOf(AdoThread thread) =>
        thread.ThreadContext?.RightFileStart?.Line ?? thread.ThreadContext?.LeftFileStart?.Line;

    /// <summary>The line it was posted on: where tracking moved it from, else where it is.</summary>
    private static int? OriginalLineOf(AdoThread thread) =>
        thread.PullRequestThreadContext?.TrackingCriteria is { } moved
            ? moved.OrigRightFileStart?.Line ?? moved.OrigLeftFileStart?.Line
            : LineOf(thread);

    private static string AuthorOf(AdoComment comment) => comment.Author?.UniqueName ?? comment.Author?.DisplayName ?? "unknown";

    private static string? ReviewOf(AdoThread thread) =>
        thread.Properties?.GetValueOrDefault(ReviewProperty)?.Value is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string CommentId(AdoThread thread, AdoComment comment) =>
        comment.ParentCommentId == 0 && comment.Id == Live(thread).First().Id
            ? thread.Id.ToString(CultureInfo.InvariantCulture)
            : $"{thread.Id.ToString(CultureInfo.InvariantCulture)}.{comment.Id.ToString(CultureInfo.InvariantCulture)}";

    public async Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(int number, CancellationToken cancellationToken = default)
    {
        var threads = await ThreadsAsync(number, cancellationToken).ConfigureAwait(false);
        return [.. threads.Where(IsInline).Select(t => new ReviewThread
        {
            Id = t.Id.ToString(CultureInfo.InvariantCulture),
            IsResolved = IsResolved(t),
            IsOutdated = LineOf(t) is null,
            Path = PathOf(t),
            Line = LineOf(t),
            OriginalLine = OriginalLineOf(t),
            Comments = [.. Live(t).Select(c => new ThreadComment(CommentId(t, c), AuthorOf(c), c.Content ?? "", c.PublishedDate ?? DateTimeOffset.MinValue))],
        })];
    }

    public async Task<FetchSource> FetchSourceAsync(PullRequest pullRequest, CancellationToken cancellationToken = default)
    {
        var token = await _tokens.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // No head ref on Azure Repos; the head commit itself, by SHA, works even after the branch is deleted.
        return new FetchSource(WebUrl, pullRequest.HeadSha, new GitCredential($"AUTHORIZATION: {Scheme(token)} {Credentials(token)}"));
    }

    // --- posting ------------------------------------------------------------

    /// <summary>Azure DevOps takes a review a thread at a time (<see cref="ReviewPosting.PerThread"/>).</summary>
    public Task<PostedReview> PostReviewAsync(ReviewPost post, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Azure DevOps posts a review a thread at a time");

    /// <summary>
    /// The summary goes as a closed thread, since it asks nothing of the author and a comment
    /// policy would otherwise hold the PR until someone resolved it; an inline comment as an
    /// active one, anchored to the iteration that pushed the reviewed head.
    /// </summary>
    public async Task<PostedComment> StartThreadAsync(ThreadPost post, CancellationToken cancellationToken = default)
    {
        AdoThreadContext? where = null;
        AdoPullRequestThreadContext? iteration = null;
        if (post.Inline is { } inline)
        {
            var start = new AdoPosition(inline.Line, 1);
            var end = new AdoPosition(inline.Line + 1, 1);
            where = inline.Side == DiffSide.Old
                ? new AdoThreadContext("/" + inline.Path, null, null, start, end)
                : new AdoThreadContext("/" + inline.Path, start, end, null, null);
            iteration = await IterationContextAsync(post.Number, post.CommitId, inline.Path, cancellationToken).ConfigureAwait(false);
        }

        var request = new AdoThreadRequest(
            [new AdoCommentRequest(0, post.Body, 1)],
            post.Inline is null ? 4 : 1,
            where,
            iteration,
            post.ReviewId is { } review ? new() { [ReviewProperty] = new AdoPropertyRequest("System.String", review) } : null);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_repoPath}/pullRequests/{post.Number}/threads?{Version}")
        {
            Content = JsonContent.Create(request, AzureDevOpsJson.Default.AdoThreadRequest),
        }, cancellationToken, idempotent: false).ConfigureAwait(false);

        var thread = await ReadAsync(response, AzureDevOpsJson.Default.AdoThread, cancellationToken).ConfigureAwait(false);
        return new PostedComment(thread.Id.ToString(CultureInfo.InvariantCulture), post.Inline?.Path ?? "", post.Inline?.Line, post.Body)
        {
            Url = $"{WebUrl}/pullrequest/{post.Number}?discussionId={thread.Id.ToString(CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>
    /// The iteration that pushed <paramref name="commitId"/>, compared with the PR's start, and the
    /// file's change tracking id in it: so the comment sits on the reviewed head's lines even when
    /// the PR moved on during the review. Null when the head isn't an iteration (it then goes on
    /// the newest one).
    /// </summary>
    private async Task<AdoPullRequestThreadContext?> IterationContextAsync(int number, string commitId, string path, CancellationToken cancellationToken)
    {
        if (_contexts.TryGetValue((number, commitId + "/" + path), out var known))
        {
            return known;
        }

        var iteration = (await IterationsAsync(number, cancellationToken).ConfigureAwait(false)).LastOrDefault(i => i.SourceRefCommit?.CommitId == commitId);
        AdoPullRequestThreadContext? context = null;
        if (iteration is not null)
        {
            int? tracking = null;
            for (int? skip = 0; skip is { } s && tracking is null;)
            {
                var changes = await GetAsync($"{_repoPath}/pullRequests/{number}/iterations/{iteration.Id}/changes?$top=2000&$skip={s}&{Version}",
                    AzureDevOpsJson.Default.AdoIterationChanges, cancellationToken).ConfigureAwait(false);
                tracking = changes.ChangeEntries?.FirstOrDefault(c => string.Equals(c.Item?.Path, "/" + path, StringComparison.Ordinal))?.ChangeTrackingId;
                skip = changes.NextSkip is > 0 and var next ? next : null;
            }

            context = new AdoPullRequestThreadContext(new AdoIterationContext(1, iteration.Id), tracking, null);
        }

        _contexts[(number, commitId + "/" + path)] = context;
        return context;
    }

    /// <summary>Approve is vote 10, request changes -5 (waiting for the author); a comment leaves the vote alone.</summary>
    public async Task SetVoteAsync(int number, Verdict verdict, CancellationToken cancellationToken = default)
    {
        var vote = VoteOf(verdict);
        if (vote == 0)
        {
            return;
        }

        var me = await IdentityIdAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"{_repoPath}/pullRequests/{number}/reviewers/{me}?{Version}")
        {
            Content = JsonContent.Create(new AdoVoteRequest(vote), AzureDevOpsJson.Default.AdoVoteRequest),
        }, cancellationToken).ConfigureAwait(false);
        var reviewer = await ReadAsync(response, AzureDevOpsJson.Default.AdoReviewer, cancellationToken).ConfigureAwait(false);
        if (reviewer.Vote != vote)
        {
            throw new ProviderException($"Azure DevOps kept the vote at {reviewer.Vote}, not {vote}.");
        }
    }

    internal static int VoteOf(Verdict verdict) => verdict switch
    {
        Verdict.Approve => 10,
        Verdict.RequestChanges => -5,
        _ => 0,
    };

    private async Task<string> IdentityIdAsync(CancellationToken cancellationToken)
    {
        if (_identityId is null)
        {
            await WhoAmIAsync(cancellationToken).ConfigureAwait(false);
        }

        return _identityId!;
    }

    /// <summary>The posting identity's vote as a GitHub review state, for the read-back.</summary>
    private async Task<string> VoteStateAsync(int number, CancellationToken cancellationToken)
    {
        var me = await IdentityIdAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reviewer = await GetAsync($"{_repoPath}/pullRequests/{number}/reviewers/{me}?{Version}", AzureDevOpsJson.Default.AdoReviewer, cancellationToken).ConfigureAwait(false);
            return reviewer.Vote switch
            {
                >= 5 => "APPROVED",
                < 0 => "CHANGES_REQUESTED",
                _ => "COMMENTED",
            };
        }
        catch (ProviderException ex) when (ex.Status is 400 or 404)
        {
            // Not a reviewer of the PR: Azure DevOps says 400, "a valid reviewer must be supplied".
            return "COMMENTED";
        }
    }

    /// <summary>The summary thread <paramref name="reviewId"/> and the inline threads that name it, with the identity's vote as the state.</summary>
    public async Task<PostedReview?> GetReviewAsync(int number, string reviewId, CancellationToken cancellationToken = default)
    {
        var threads = await ThreadsAsync(number, cancellationToken).ConfigureAwait(false);
        var summary = threads.FirstOrDefault(t => t.Id.ToString(CultureInfo.InvariantCulture) == reviewId && !IsInline(t));
        if (summary is null)
        {
            return null;
        }

        var root = Live(summary).First();
        var comments = threads.Where(t => IsInline(t) && ReviewOf(t) == reviewId)
            .Select(t => new PostedComment(t.Id.ToString(CultureInfo.InvariantCulture), PathOf(t), OriginalLineOf(t), Live(t).First().Content ?? ""))
            .ToList();
        return new PostedReview(reviewId, AuthorOf(root), await VoteStateAsync(number, cancellationToken).ConfigureAwait(false), root.Content ?? "", null,
            root.PublishedDate, $"{WebUrl}/pullrequest/{number}?discussionId={reviewId}", comments);
    }

    /// <summary>The PR's general threads, each a candidate summary, oldest first.</summary>
    public async Task<IReadOnlyList<PostedReview>> ListReviewsAsync(int number, CancellationToken cancellationToken = default)
    {
        var threads = await ThreadsAsync(number, cancellationToken).ConfigureAwait(false);
        return [.. threads.Where(t => !IsInline(t)).Select(t =>
        {
            var root = Live(t).First();
            var id = t.Id.ToString(CultureInfo.InvariantCulture);
            return new PostedReview(id, AuthorOf(root), "COMMENTED", root.Content ?? "", null, root.PublishedDate, $"{WebUrl}/pullrequest/{number}?discussionId={id}", []);
        })];
    }

    /// <summary>Every comment on an inline thread, the first naming its review through <see cref="ReviewProperty"/>.</summary>
    public async Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(int number, CancellationToken cancellationToken = default)
    {
        var comments = new List<ReviewComment>();
        foreach (var thread in (await ThreadsAsync(number, cancellationToken).ConfigureAwait(false)).Where(IsInline))
        {
            var root = thread.Id.ToString(CultureInfo.InvariantCulture);
            foreach (var comment in Live(thread))
            {
                var id = CommentId(thread, comment);
                comments.Add(new ReviewComment(id, AuthorOf(comment), id == root ? ReviewOf(thread) : null, id == root ? null : root,
                    PathOf(thread), LineOf(thread), OriginalLineOf(thread), comment.Content ?? "", comment.PublishedDate));
            }
        }

        return comments;
    }

    public async Task<ReviewComment> ReplyAsync(int number, string commentId, string body, CancellationToken cancellationToken = default)
    {
        var threadId = ThreadIdOf(commentId);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_repoPath}/pullRequests/{number}/threads/{threadId}/comments?{Version}")
        {
            Content = JsonContent.Create(new AdoCommentRequest(1, body, 1), AzureDevOpsJson.Default.AdoCommentRequest),
        }, cancellationToken, idempotent: false).ConfigureAwait(false);

        var comment = await ReadAsync(response, AzureDevOpsJson.Default.AdoComment, cancellationToken).ConfigureAwait(false);
        return new ReviewComment($"{threadId}.{comment.Id.ToString(CultureInfo.InvariantCulture)}", AuthorOf(comment), null, threadId.ToString(CultureInfo.InvariantCulture),
            "", null, null, comment.Content ?? body, comment.PublishedDate);
    }

    /// <summary>Resolves as "fixed", what Azure DevOps' own Resolve button sets. Again on a resolved thread changes nothing.</summary>
    public async Task ResolveThreadAsync(int number, string threadId, CancellationToken cancellationToken = default)
    {
        var id = ThreadIdOf(threadId);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"{_repoPath}/pullRequests/{number}/threads/{id}?{Version}")
        {
            Content = JsonContent.Create(new AdoThreadStatusRequest(2), AzureDevOpsJson.Default.AdoThreadStatusRequest),
        }, cancellationToken).ConfigureAwait(false);
        var thread = await ReadAsync(response, AzureDevOpsJson.Default.AdoThread, cancellationToken).ConfigureAwait(false);
        if (!IsResolved(thread))
        {
            throw new ProviderException($"Azure DevOps didn't resolve thread {id}: it is {thread.Status}.");
        }
    }

    /// <summary>A thread's id, from rtfd's name for its first comment; anything else isn't one of its threads.</summary>
    private static int ThreadIdOf(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var thread) && thread > 0
            ? thread
            : throw new ProviderException($"'{id}' isn't an Azure DevOps thread id.", 404);

    public void Dispose() => _http.Dispose();

    // --- HTTP -----------------------------------------------------------------

    private static string Scheme(ProviderToken token) => token.IsJwt ? "Bearer" : "Basic";

    /// <summary>A JWT goes as it is; a PAT as basic auth with an empty user name.</summary>
    private static string Credentials(ProviderToken token) =>
        token.IsJwt ? token.Value : Convert.ToBase64String(Encoding.UTF8.GetBytes($":{token.Value}"));

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
                ?? throw new ProviderException($"Azure DevOps sent an empty answer to {response.RequestMessage?.RequestUri?.AbsolutePath}");
        }
        catch (JsonException ex)
        {
            throw new ProviderException($"Azure DevOps sent an answer rtfd can't read ({response.RequestMessage?.RequestUri?.AbsolutePath}): {ex.Message}");
        }
    }

    /// <summary>
    /// Sends with the token. A read retries 5xx, 429 and network errors; a request that isn't
    /// <paramref name="idempotent"/> (starting a thread, a reply) retries only a 429, which Azure
    /// DevOps answers before doing anything. A 401 from a token the source can renew is retried
    /// once with a new one. A sign-in page (a redirect, a 203, or HTML) counts as a 401.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancellationToken, bool idempotent = true)
    {
        var renew = false;
        for (var attempt = 1; ; attempt++)
        {
            using var request = create();
            var token = await _tokens.GetAsync(renew, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue(Scheme(token), Credentials(token));
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
                throw new ProviderException($"can't reach Azure DevOps: {ex.Message}") { MayHaveLanded = !idempotent };
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!idempotent)
                {
                    throw new ProviderException("Azure DevOps didn't answer in time") { MayHaveLanded = true };
                }

                if (attempt < MaxAttempts)
                {
                    await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new ProviderException("Azure DevOps didn't answer in time");
            }

            var signIn = IsSignInPage(response);
            if (response.IsSuccessStatusCode && !signIn)
            {
                return response;
            }

            var status = signIn ? 401 : (int)response.StatusCode;
            var message = signIn ? "it answered with a sign-in page" : await ErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
            var limited = response.StatusCode == HttpStatusCode.TooManyRequests;
            var wait = limited || status >= 500 ? RetryWait(response, attempt) : (TimeSpan?)null;
            response.Dispose();

            // A sign-in page answers before anything happens, so even a post may go again.
            if (status == 401 && !renew && !_tokens.ReadOnce)
            {
                renew = true;
                continue;
            }

            if (wait is { } delay && attempt < MaxAttempts && (idempotent || limited))
            {
                await _delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new ProviderException(Describe(status, request.RequestUri?.OriginalString ?? "", message), status)
            {
                MayHaveLanded = !idempotent && status >= 500,
            };
        }
    }

    /// <summary>What a wrong tenant or an expired token gets: a redirect to sign in, a 203, or a page of HTML.</summary>
    private static bool IsSignInPage(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.NonAuthoritativeInformation or HttpStatusCode.RedirectKeepVerb
        || (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/html");

    private static TimeSpan RetryWait(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delta > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : delta;
        }

        return Backoff(attempt);
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(text, AzureDevOpsJson.Default.AdoError)?.Message ?? "";
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
            return "";
        }
    }

    private string Describe(int status, string path, string message)
    {
        var detail = message.Length > 0 ? $": {message}" : "";
        var where = path.Split('?')[0];
        return status switch
        {
            401 => $"Azure DevOps refused the token from {_tokens.Describe} (401){detail}. It may have expired, or belong to another tenant.",
            403 => $"Azure DevOps refused {where} (403){detail}. The token may lack access to {Slug}.",
            404 => $"Azure DevOps has no {where} (404), or the token can't see it{detail}.",
            _ => $"Azure DevOps answered {status} to {where}{detail}.",
        };
    }
}
