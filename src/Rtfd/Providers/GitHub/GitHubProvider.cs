using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Rtfd.Git;

namespace Rtfd.Providers.GitHub;

/// <summary>
/// GitHub's read side: REST for PRs, checks, the user and the repo; GraphQL for review threads,
/// which exist only there. 5xx answers and rate limits are retried, honouring <c>Retry-After</c>.
/// </summary>
public sealed class GitHubProvider : IProvider, IDisposable
{
    public const string DefaultApi = "https://api.github.com/";

    private const int MaxAttempts = 4;

    private const string ThreadsQuery = """
        query($owner: String!, $name: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              reviewThreads(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id isResolved isOutdated resolvedBy { login } path line
                  comments(first: 100) { nodes { databaseId author { login } body createdAt } }
                }
              }
            }
          }
        }
        """;

    private readonly HttpClient _http;
    private readonly string _slug;
    private readonly string _owner;
    private readonly string _name;
    private readonly ProviderToken _token;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="slug"><c>owner/name</c>.</param>
    /// <param name="token">Sent as the bearer token, and to git for fetches only.</param>
    /// <param name="handler">Tests pass recorded responses here.</param>
    /// <param name="delay">Waits between retries; tests pass one that doesn't.</param>
    public GitHubProvider(string slug, ProviderToken token, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _slug = slug;
        (_owner, _name) = (slug.Split('/')[0], slug.Split('/')[1]);
        _token = token;
        _delay = delay ?? Task.Delay;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(DefaultApi);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("rtfd", EntryPoint.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<string> WhoAmIAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync("user", GitHubJson.Default.GhUser, cancellationToken).ConfigureAwait(false)).Login;

    public async Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default)
    {
        var repo = await GetAsync($"repos/{_slug}", GitHubJson.Default.GhRepository, cancellationToken).ConfigureAwait(false);
        return new RepositoryInfo(!repo.Private && repo.Visibility is null or "public", repo.DefaultBranch);
    }

    public async Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken cancellationToken = default)
    {
        var pulls = new List<PullRequest>();
        string? next = $"repos/{_slug}/pulls?state=open&per_page=100";
        while (next is not null)
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, next), cancellationToken).ConfigureAwait(false);
            var page = await ReadAsync(response, GitHubJson.Default.ListGhPull, cancellationToken).ConfigureAwait(false);
            pulls.AddRange(page.Select(Map));
            next = NextLink(response);
        }

        return pulls;
    }

    public async Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default) =>
        Map(await GetAsync($"repos/{_slug}/pulls/{number}", GitHubJson.Default.GhPull, cancellationToken).ConfigureAwait(false));

    public async Task<CiState> GetCiStateAsync(string sha, CancellationToken cancellationToken = default)
    {
        var checks = new List<CiCheck>();
        var runs = new List<GhCheckRun>();
        string? next = $"repos/{_slug}/commits/{sha}/check-runs?per_page=100";
        while (next is not null)
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, next), cancellationToken).ConfigureAwait(false);
            runs.AddRange((await ReadAsync(response, GitHubJson.Default.GhCheckRuns, cancellationToken).ConfigureAwait(false)).CheckRuns);
            next = NextLink(response);
        }

        var status = await GetAsync($"repos/{_slug}/commits/{sha}/status?per_page=100", GitHubJson.Default.GhCombinedStatus, cancellationToken).ConfigureAwait(false);

        checks.AddRange(runs.Select(r => new CiCheck(r.Name, r.Status, r.Conclusion, r.HtmlUrl)));
        checks.AddRange(status.Statuses.Select(s => new CiCheck(s.Context, s.State == "pending" ? "pending" : "completed", s.State == "pending" ? null : s.State, s.TargetUrl)));
        return new CiState(Summarize(checks), checks);
    }

    /// <summary>Pending while anything runs, failure if anything failed, success otherwise; none without checks.</summary>
    internal static CiSummary Summarize(IReadOnlyList<CiCheck> checks)
    {
        if (checks.Count == 0)
        {
            return CiSummary.None;
        }

        if (checks.Any(c => c.Status != "completed"))
        {
            return CiSummary.Pending;
        }

        return checks.Any(c => c.Conclusion is "failure" or "timed_out" or "cancelled" or "action_required" or "startup_failure" or "error")
            ? CiSummary.Failure
            : CiSummary.Success;
    }

    public async Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(int number, CancellationToken cancellationToken = default)
    {
        var threads = new List<ReviewThread>();
        string? after = null;
        do
        {
            var request = new GqlRequest(ThreadsQuery, new GqlThreadsVariables(_owner, _name, number, after));
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = JsonContent.Create(request, GitHubGraphQlJson.Default.GqlRequest),
            }, cancellationToken).ConfigureAwait(false);

            var body = await ReadAsync(response, GitHubGraphQlJson.Default.GqlResponseGqlThreadsData, cancellationToken).ConfigureAwait(false);
            if (body.Errors is { Count: > 0 } errors)
            {
                throw new ProviderException($"GitHub GraphQL: {string.Join("; ", errors.Select(e => e.Message))}");
            }

            var page = body.Data?.Repository?.PullRequest?.ReviewThreads
                ?? throw new ProviderException($"GitHub GraphQL: no pull request #{number} in {_slug}");
            threads.AddRange(page.Nodes.Select(t => new ReviewThread
            {
                Id = t.Id,
                IsResolved = t.IsResolved,
                IsOutdated = t.IsOutdated,
                ResolvedBy = t.ResolvedBy?.Login,
                Path = t.Path,
                Line = t.Line,
                Comments = [.. t.Comments.Nodes.Select(c => new ThreadComment(c.DatabaseId, c.Author?.Login ?? "ghost", c.Body, c.CreatedAt))],
            }));
            after = page.PageInfo.HasNextPage ? page.PageInfo.EndCursor : null;
        }
        while (after is not null);

        return threads;
    }

    public FetchSource FetchSource(PullRequest pullRequest)
    {
        // What actions/checkout sends: basic auth with the token as the password.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{_token.Value}"));
        return new FetchSource($"https://github.com/{_slug}.git", $"refs/pull/{pullRequest.Number}/head", new GitCredential($"AUTHORIZATION: basic {basic}"));
    }

    public void Dispose() => _http.Dispose();

    private static PullRequest Map(GhPull pull) => new()
    {
        Number = pull.Number,
        Title = pull.Title,
        Body = pull.Body ?? "",
        Author = pull.User.Login,
        IsDraft = pull.Draft,

        // A fork whose repo was deleted has no head repo at all.
        IsFork = pull.Head.Repo is null || !string.Equals(pull.Head.Repo.FullName, pull.Base.Repo?.FullName, StringComparison.OrdinalIgnoreCase),
        State = pull.State,
        HeadSha = pull.Head.Sha,
        HeadRef = pull.Head.Ref,
        BaseRef = pull.Base.Ref,
        BaseSha = pull.Base.Sha,
        Url = pull.HtmlUrl,
    };

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
                ?? throw new ProviderException($"GitHub sent an empty answer to {response.RequestMessage?.RequestUri?.AbsolutePath}");
        }
        catch (JsonException ex)
        {
            throw new ProviderException($"GitHub sent an answer rtfd can't read ({response.RequestMessage?.RequestUri?.AbsolutePath}): {ex.Message}");
        }
    }

    /// <summary>Sends with the token, retrying 5xx, 429, secondary rate limits and network errors.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Value);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _ = ex;
                await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderException($"can't reach GitHub: {ex.Message}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < MaxAttempts)
                {
                    await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new ProviderException("GitHub didn't answer in time");
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = (int)response.StatusCode;
            var message = await ErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
            var wait = RetryWait(response, message, attempt);
            response.Dispose();
            if (wait is { } delay && attempt < MaxAttempts)
            {
                await _delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new ProviderException(Describe(status, request.RequestUri?.OriginalString ?? "", message), status);
        }
    }

    /// <summary>How long to wait before retrying, or null when retrying won't help.</summary>
    private static TimeSpan? RetryWait(HttpResponseMessage response, string message, int attempt)
    {
        var status = response.StatusCode;
        var rateLimited = status == HttpStatusCode.TooManyRequests
            || (status == HttpStatusCode.Forbidden && (response.Headers.RetryAfter is not null
                || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || Header(response, "x-ratelimit-remaining") == "0"));

        if (!rateLimited && (int)status < 500)
        {
            return null;
        }

        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return Cap(delta);
        }

        if (rateLimited && long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
        {
            return Cap(DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow);
        }

        return Backoff(attempt);
    }

    private static TimeSpan Cap(TimeSpan wait) => wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : wait;

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(text, GitHubJson.Default.GhError)?.Message ?? "";
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
            return "";
        }
    }

    private string Describe(int status, string path, string message)
    {
        var detail = message.Length > 0 ? $": {message}" : "";
        return status switch
        {
            401 => $"GitHub refused the token (401){detail}. Check the variable token_env names.",
            403 => $"GitHub refused {path} (403){detail}. The token may lack access to {_slug}.",
            404 => $"GitHub has no {path} (404), or the token can't see it{detail}.",
            _ => $"GitHub answered {status} to {path}{detail}.",
        };
    }

    private static string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var part in string.Join(',', values).Split(','))
        {
            var pieces = part.Split(';');
            if (pieces.Length == 2 && pieces[1].Trim() == "rel=\"next\"")
            {
                return pieces[0].Trim().TrimStart('<').TrimEnd('>');
            }
        }

        return null;
    }
}
