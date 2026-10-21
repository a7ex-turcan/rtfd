using System.Net;
using System.Text;
using Rtfd.Providers;
using Rtfd.Providers.GitHub;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// The GitHub provider against recorded responses (Recordings/github: real answers from the
/// public nutrx and rtfd repos and from the sandbox, trimmed to the fields rtfd reads).
/// </summary>
public sealed class GitHubProviderTests
{
    private static string Recording(string name) =>
        File.ReadAllText(Path.Combine(Configs.RepositoryRoot, "tests", "Rtfd.Tests", "Recordings", "github", name));

    private static (GitHubProvider Provider, RecordedHandler Handler, string Token, List<TimeSpan> Waits) Create(string slug = "a7ex-turcan/nutrx")
    {
        var handler = new RecordedHandler();
        var token = Tokens.GitHubLike();
        var waits = new List<TimeSpan>();
        var provider = new GitHubProvider(slug, TestTokens.Fixed(token), handler, (wait, _) =>
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

        var ci = await provider.GetCiStateAsync(12, sha, TestContext.Current.CancellationToken);

        Assert.Equal(CiSummary.Success, ci.Summary);
        Assert.Equal(["aot", "build (ubuntu-latest)", "build (windows-latest)"], ci.Checks.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_poll_sends_the_last_ETag_and_a_304_reuses_the_page_it_stands_for()
    {
        var (provider, handler, _, _) = Create("a7ex-turcan/rtfd");
        const string sha = "a817cb595f4d11c9091479ee21df5d6511ad524b";
        var runs = $"/repos/a7ex-turcan/rtfd/commits/{sha}/check-runs?per_page=100";
        var status = $"/repos/a7ex-turcan/rtfd/commits/{sha}/status?per_page=100";
        handler.On(runs, HttpStatusCode.OK, Recording("check-runs.json"), ("ETag", "W/\"runs-1\""))
            .On(runs, HttpStatusCode.NotModified, "");
        handler.On(status, HttpStatusCode.OK, Recording("status.json"), ("ETag", "W/\"status-1\""))
            .On(status, HttpStatusCode.NotModified, "");

        var first = await provider.GetCiStateAsync(12, sha, TestContext.Current.CancellationToken);
        var second = await provider.GetCiStateAsync(12, sha, TestContext.Current.CancellationToken);

        Assert.Equal(first.Checks, second.Checks);
        Assert.Equal(CiSummary.Success, second.Summary);
        var conditional = handler.Requests.Skip(2).Select(r => r.Request.Headers.TryGetValues("If-None-Match", out var v) ? v.Single() : null).ToList();
        Assert.Equal(["W/\"runs-1\"", "W/\"status-1\""], conditional);
        Assert.All(handler.Requests.Take(2), r => Assert.False(r.Request.Headers.Contains("If-None-Match")));
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
    public async Task The_PR_s_review_comments_come_with_their_review_line_and_reply_target()
    {
        var (provider, handler, _, _) = Create("a7ex-turcan/rtfd-sandbox");
        handler.On("/repos/a7ex-turcan/rtfd-sandbox/pulls/1/comments?per_page=100", HttpStatusCode.OK, Recording("pull-comments.json"));

        var comments = await provider.ListReviewCommentsAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(3, comments.Count);
        var root = comments[0];
        Assert.Equal(("4260939504", "5506379986", null, 26, 26), (root.Id, root.ReviewId, root.InReplyTo, root.Line, root.OriginalLine));
        Assert.StartsWith("Should:", root.Body, StringComparison.Ordinal);
        Assert.Equal("4260939511", comments[2].InReplyTo);
        Assert.Equal("a7ex-turcan", comments[2].Author);
    }

    [Fact]
    public async Task A_reply_goes_to_the_thread_s_first_comment_and_isn_t_retried_after_a_502()
    {
        var (provider, handler, _, waits) = Create("a7ex-turcan/rtfd-sandbox");
        const string path = "/repos/a7ex-turcan/rtfd-sandbox/pulls/1/comments/4260939511/replies";
        handler.On(path, HttpStatusCode.Created, Recording("reply.json"));

        var reply = await provider.ReplyAsync(1, "4260939511", "MINOR: rtfd's live check of the replies endpoint (Phase 4).", TestContext.Current.CancellationToken);

        Assert.Equal(("4280645531", "4260939511"), (reply.Id, reply.InReplyTo));
        var sent = System.Text.Json.Nodes.JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.Equal(["body"], sent.Select(p => p.Key));
        Assert.Equal("MINOR: rtfd's live check of the replies endpoint (Phase 4).", sent["body"]!.GetValue<string>());

        var (failing, failingHandler, _, _) = Create("a7ex-turcan/rtfd-sandbox");
        failingHandler.On(path, HttpStatusCode.BadGateway, "{}");
        var error = await Assert.ThrowsAsync<ProviderException>(() => failing.ReplyAsync(1, "4260939511", "x", TestContext.Current.CancellationToken));
        Assert.True(error.MayHaveLanded);
        Assert.Single(failingHandler.Requests);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A_resolve_is_a_GraphQL_mutation_and_a_thread_that_isn_t_there_is_an_error()
    {
        var (provider, handler, _, _) = Create("a7ex-turcan/rtfd-sandbox");
        handler.On("/graphql", HttpStatusCode.OK, Recording("resolve.json"));

        await provider.ResolveThreadAsync(1, "PRRT_kwDOVHP2386r8isI", TestContext.Current.CancellationToken);

        var body = handler.Requests.Single().Body!;
        Assert.Contains("resolveReviewThread(input: {threadId: $id})", body, StringComparison.Ordinal);
        Assert.Contains("\"variables\":{\"id\":\"PRRT_kwDOVHP2386r8isI\"}", body, StringComparison.Ordinal);

        var (missing, missingHandler, _, _) = Create("a7ex-turcan/rtfd-sandbox");
        missingHandler.On("/graphql", HttpStatusCode.OK, Recording("resolve-not-found.json"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => missing.ResolveThreadAsync(1, "PRRT_doesnotexist", TestContext.Current.CancellationToken));
        Assert.Contains("Could not resolve to a node", error.Message, StringComparison.Ordinal);
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
    public async Task The_fetch_credential_is_basic_auth_for_git_and_prints_as_a_placeholder()
    {
        var (provider, _, token, _) = Create();
        var pr = FakeProvider.Pr(new string('a', 40), number: 12);

        var source = await provider.FetchSourceAsync(pr, TestContext.Current.CancellationToken);

        Assert.Equal("https://github.com/a7ex-turcan/nutrx.git", source.Url);
        Assert.Equal("refs/pull/12/head", source.HeadRef);
        Assert.Equal("[credential]", source.Credential!.ToString());
        Assert.Equal("[token]", new ProviderToken(token).ToString());
    }
}
