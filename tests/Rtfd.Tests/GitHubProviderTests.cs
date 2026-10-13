using System.Net;
using System.Text;
using Rtfd.Providers;
using Rtfd.Providers.GitHub;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// The GitHub read side against recorded responses (Recordings/github: real answers from the
/// public nutrx and rtfd repos, trimmed to the fields rtfd reads).
/// </summary>
public sealed class GitHubProviderTests
{
    private static string Recording(string name) =>
        File.ReadAllText(Path.Combine(Configs.RepositoryRoot, "tests", "Rtfd.Tests", "Recordings", "github", name));

    /// <summary>Answers from a queue per path, and keeps every request for the assertions.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _answers = new(StringComparer.Ordinal);

        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        public Handler On(string pathAndQuery, HttpStatusCode status, string body, params (string Name, string Value)[] headers)
        {
            if (!_answers.TryGetValue(pathAndQuery, out var queue))
            {
                _answers[pathAndQuery] = queue = new();
            }

            queue.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return response;
            });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var key = request.RequestUri!.PathAndQuery;
            if (!_answers.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message": "Not Found"}""") };
            }

            var answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return answer();
        }
    }

    private static (GitHubProvider Provider, Handler Handler, string Token, List<TimeSpan> Waits) Create(string slug = "a7ex-turcan/nutrx")
    {
        var handler = new Handler();
        var token = Tokens.GitHubLike();
        var waits = new List<TimeSpan>();
        var provider = new GitHubProvider(slug, new ProviderToken(token), handler, (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });
        return (provider, handler, token, waits);
    }

    [Fact]
    public async Task A_pull_request_maps_with_its_author_refs_and_fork_flag()
    {
        var (provider, handler, token, _) = Create();
        handler.On("/repos/a7ex-turcan/nutrx/pulls/36", HttpStatusCode.OK, Recording("pull-36.json"));

        var pr = await provider.GetPullRequestAsync(36, TestContext.Current.CancellationToken);

        Assert.Equal(36, pr.Number);
        Assert.Equal("a7ex-turcan", pr.Author);
        Assert.Equal("main", pr.BaseRef);
        Assert.Equal("fix/form-keyboard-scroll", pr.HeadRef);
        Assert.Equal(40, pr.HeadSha.Length);
        Assert.False(pr.IsFork);
        Assert.False(pr.IsDraft);
        var request = handler.Requests.Single().Request;
        Assert.Equal(("Bearer", token), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
    }

    [Fact]
    public async Task A_head_in_another_repo_or_a_deleted_fork_is_a_fork()
    {
        var (provider, handler, _, _) = Create();
        var fork = Recording("pull-36.json").Replace("\"full_name\": \"a7ex-turcan/nutrx\",\n      \"private\": false\n    }\n  },\n  \"base\"", "\"full_name\": \"someone/nutrx\",\n      \"private\": false\n    }\n  },\n  \"base\"", StringComparison.Ordinal);
        Assert.NotEqual(Recording("pull-36.json"), fork);
        handler.On("/repos/a7ex-turcan/nutrx/pulls/36", HttpStatusCode.OK, fork);

        Assert.True((await provider.GetPullRequestAsync(36, TestContext.Current.CancellationToken)).IsFork);
    }

    [Fact]
    public async Task Check_runs_and_statuses_sum_up_to_one_CI_state()
    {
        var (provider, handler, _, _) = Create("a7ex-turcan/rtfd");
        const string sha = "a817cb595f4d11c9091479ee21df5d6511ad524b";
        handler.On($"/repos/a7ex-turcan/rtfd/commits/{sha}/check-runs?per_page=100", HttpStatusCode.OK, Recording("check-runs.json"));
        handler.On($"/repos/a7ex-turcan/rtfd/commits/{sha}/status?per_page=100", HttpStatusCode.OK, Recording("status.json"));

        var ci = await provider.GetCiStateAsync(sha, TestContext.Current.CancellationToken);

        Assert.Equal(CiSummary.Success, ci.Summary);
        Assert.Equal(["aot", "build (ubuntu-latest)", "build (windows-latest)"], ci.Checks.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(new[] { "completed:success", "in_progress:" }, CiSummary.Pending)]
    [InlineData(new[] { "completed:success", "completed:failure" }, CiSummary.Failure)]
    [InlineData(new[] { "completed:success", "completed:skipped", "completed:neutral" }, CiSummary.Success)]
    [InlineData(new string[0], CiSummary.None)]
    public void The_CI_summary_waits_on_anything_running_and_fails_on_anything_failed(string[] checks, CiSummary expected)
    {
        var list = checks.Select(c => c.Split(':')).Select(p => new CiCheck("c", p[0], p[1].Length > 0 ? p[1] : null, null)).ToList();

        Assert.Equal(expected, GitHubProvider.Summarize(list));
    }

    [Fact]
    public async Task Review_threads_come_from_GraphQL_with_their_resolution_and_comments()
    {
        var (provider, handler, _, _) = Create();
        handler.On("/graphql", HttpStatusCode.OK, Recording("threads-36.json"));

        var threads = await provider.GetReviewThreadsAsync(36, TestContext.Current.CancellationToken);

        Assert.Equal(2, threads.Count);
        var first = threads[0];
        Assert.True(first.IsResolved);
        Assert.True(first.IsOutdated);
        Assert.Equal("a7ex-turcan", first.ResolvedBy);
        Assert.Null(first.Line);
        Assert.StartsWith("Could:", first.Comments[0].Body, StringComparison.Ordinal);
        Assert.Equal(87, threads[1].Line);
        Assert.Contains("\"number\":36", handler.Requests.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_error_is_retried_and_then_succeeds()
    {
        var (provider, handler, _, waits) = Create();
        handler
            .On("/user", HttpStatusCode.BadGateway, "{}")
            .On("/user", HttpStatusCode.OK, """{"login": "octocat"}""");

        Assert.Equal("octocat", await provider.WhoAmIAsync(TestContext.Current.CancellationToken));
        Assert.Single(waits);
    }

    [Fact]
    public async Task A_secondary_rate_limit_waits_as_long_as_Retry_After_says()
    {
        var (provider, handler, _, waits) = Create();
        handler
            .On("/user", HttpStatusCode.Forbidden, """{"message": "You have exceeded a secondary rate limit."}""", ("Retry-After", "7"))
            .On("/user", HttpStatusCode.OK, """{"login": "octocat"}""");

        await provider.WhoAmIAsync(TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(7)], waits);
    }

    [Fact]
    public async Task A_refused_token_is_an_auth_error_that_never_shows_the_token()
    {
        var (provider, handler, token, waits) = Create();
        handler.On("/user", HttpStatusCode.Unauthorized, """{"message": "Bad credentials"}""");

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.WhoAmIAsync(TestContext.Current.CancellationToken));

        Assert.True(error.IsAuth);
        Assert.Empty(waits);
        Assert.Contains("Bad credentials", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fetch_credential_is_basic_auth_for_git_and_prints_as_a_placeholder()
    {
        var (provider, _, token, _) = Create();
        var pr = FakeProvider.Pr(new string('a', 40), number: 12);

        var source = provider.FetchSource(pr);

        Assert.Equal("https://github.com/a7ex-turcan/nutrx.git", source.Url);
        Assert.Equal("refs/pull/12/head", source.HeadRef);
        Assert.Equal("[credential]", source.Credential!.ToString());
        Assert.Equal("[token]", new ProviderToken(token).ToString());
    }
}
