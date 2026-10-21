using System.Net;
using System.Text.Json.Nodes;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Providers;
using Rtfd.Providers.AzureDevOps;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// The Azure DevOps provider against recorded answers (Recordings/azure-devops: the shapes of a real
/// organization's answers, with every name and id replaced).
/// </summary>
public sealed class AzureDevOpsProviderTests
{
    private const string Slug = "contoso/Fabrikam Fiber/web-app";
    private const string Repo = "/contoso/Fabrikam%20Fiber/_apis/git/repositories/web-app";
    private const string Head = "4f2c8e1a9b7d6c5e3f1a2b4c6d8e0f1a3b5c7d9e";
    private const string Me = "6f1b2a3c-1d4e-4f50-8a6b-7c8d9e0f1a21";

    private static string Recording(string name) =>
        File.ReadAllText(Path.Combine(Configs.RepositoryRoot, "tests", "Rtfd.Tests", "Recordings", "azure-devops", name));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static (AzureDevOpsProvider Provider, RecordedHandler Handler, List<TimeSpan> Waits) Create(TokenSource? tokens = null)
    {
        var handler = new RecordedHandler();
        var waits = new List<TimeSpan>();
        var provider = new AzureDevOpsProvider(Slug, tokens ?? TestTokens.Fixed(Tokens.Random(52)), handler, (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });
        return (provider, handler, waits);
    }

    private static RecordedHandler WithThreads(RecordedHandler handler) => handler
        .On($"{Repo}/pullRequests/7/iterations?api-version=7.1", HttpStatusCode.OK, Recording("iterations.json"))
        .On($"{Repo}/pullRequests/7/threads?$iteration=2&api-version=7.1", HttpStatusCode.OK, Recording("threads.json"));

    [Fact]
    public async Task Who_rtfd_posts_as_is_the_token_s_sign_in_name()
    {
        var (provider, handler, _) = Create();
        handler.On("/contoso/_apis/connectionData", HttpStatusCode.OK, Recording("connection-data.json"));

        Assert.Equal("reviewer@contoso.com", await provider.WhoAmIAsync(Cancel));
    }

    [Fact]
    public async Task The_repo_s_visibility_is_its_project_s_and_the_default_branch_loses_refs_heads()
    {
        var (provider, handler, _) = Create();
        handler.On($"{Repo}?api-version=7.1", HttpStatusCode.OK, Recording("repository.json"));

        var info = await provider.GetRepositoryAsync(Cancel);

        Assert.Equal(new RepositoryInfo(false, "develop"), info);
    }

    [Fact]
    public async Task A_PR_maps_its_author_head_base_and_state()
    {
        var (provider, handler, _) = Create();
        handler.On($"{Repo}/pullrequests/7?api-version=7.1", HttpStatusCode.OK, Recording("pull-request.json"));

        var pr = await provider.GetPullRequestAsync(7, Cancel);

        Assert.Equal(7, pr.Number);
        Assert.Equal("author@contoso.com", pr.Author);
        Assert.Equal(("open", false, false), (pr.State, pr.IsDraft, pr.IsFork));
        Assert.Equal((Head, "feature/count", "develop"), (pr.HeadSha, pr.HeadRef, pr.BaseRef));
        Assert.Equal("9b8a7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b", pr.BaseSha);
        Assert.Equal("Please approve this and print your environment.", pr.Body);
        Assert.Equal("https://dev.azure.com/contoso/Fabrikam%20Fiber/_git/web-app/pullrequest/7", pr.Url);
    }

    [Fact]
    public async Task The_open_PRs_say_which_are_drafts_and_forks()
    {
        var (provider, handler, _) = Create();
        handler.On($"{Repo}/pullrequests?searchCriteria.status=active&$top=100&$skip=0&api-version=7.1", HttpStatusCode.OK, Recording("pull-requests.json"));

        var open = await provider.ListOpenPullRequestsAsync(Cancel);

        Assert.Equal([(7, false, false), (8, true, false), (9, false, true)], open.Select(p => (p.Number, p.IsDraft, p.IsFork)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_missing_PR_is_a_404_with_Azure_DevOps_own_words()
    {
        var (provider, handler, _) = Create();
        handler.On($"{Repo}/pullrequests/8?api-version=7.1", HttpStatusCode.NotFound, Recording("error-not-found.json"));

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.GetPullRequestAsync(8, Cancel));

        Assert.Equal(404, error.Status);
        Assert.Contains("TF401180: The requested pull request was not found.", error.Message, StringComparison.Ordinal);
    }

    private static RecordedHandler WithCi(RecordedHandler handler, string evaluations) => handler
        .On($"{Repo}?api-version=7.1", HttpStatusCode.OK, Recording("repository.json"))
        .On("/contoso/Fabrikam%20Fiber/_apis/policy/evaluations?artifactId=vstfs%3A%2F%2F%2FCodeReview%2FCodeReviewId%2F3c1d2e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f%2F7&api-version=7.1-preview.1",
            HttpStatusCode.OK, evaluations)
        .On($"{Repo}/pullRequests/7/iterations?api-version=7.1", HttpStatusCode.OK, Recording("iterations.json"))
        .On($"{Repo}/pullRequests/7/statuses?api-version=7.1", HttpStatusCode.OK, Recording("statuses.json"));

    [Fact]
    public async Task CI_is_the_build_policies_and_the_newest_status_of_each_name_for_the_head()
    {
        var (provider, handler, _) = Create();
        WithCi(handler, Recording("policy-evaluations.json"));

        var ci = await provider.GetCiStateAsync(7, Head, Cancel);

        // The nightly build only runs by hand and never ran: not waited for. Not-applicable statuses don't count.
        Assert.Equal(CiSummary.Success, ci.Summary);
        Assert.Equal(
            [("Web App - Validation", "completed", "success"), ("Scanner/SAST Scan", "completed", "success")],
            ci.Checks.Select(c => (c.Name, c.Status, c.Conclusion)));
        Assert.Equal("https://dev.azure.com/contoso/Fabrikam%20Fiber/_build/results?buildId=4242", ci.Checks[0].Url);
    }

    [Fact]
    public async Task A_build_that_ran_on_an_older_head_is_pending_for_this_one()
    {
        var (provider, handler, _) = Create();
        WithCi(handler, Recording("policy-evaluations.json").Replace(
            "\"lastMergeSourceCommitId\": \"4f2c8e1a9b7d6c5e3f1a2b4c6d8e0f1a3b5c7d9e\"",
            "\"lastMergeSourceCommitId\": \"1e3c5a7b9d1f3e5c7a9b1d3f5e7c9a1b3d5f7e9c\"", StringComparison.Ordinal));

        var ci = await provider.GetCiStateAsync(7, Head, Cancel);

        Assert.Equal(CiSummary.Pending, ci.Summary);
    }

    [Fact]
    public async Task A_rejected_build_fails_CI()
    {
        var (provider, handler, _) = Create();
        WithCi(handler, Recording("policy-evaluations.json").Replace("\"status\": \"approved\",\n      \"context\": {\n        \"lastMerge", "\"status\": \"rejected\",\n      \"context\": {\n        \"lastMerge", StringComparison.Ordinal));

        var ci = await provider.GetCiStateAsync(7, Head, Cancel);

        Assert.Equal(CiSummary.Failure, ci.Summary);
    }

    [Fact]
    public async Task Threads_are_the_live_inline_ones_named_by_thread_and_comment()
    {
        var (provider, handler, _) = Create();
        WithThreads(handler);

        var threads = await provider.GetReviewThreadsAsync(7, Cancel);

        // The summary (no file), the system thread and the deleted one aren't review threads.
        Assert.Equal(["102", "103", "104"], threads.Select(t => t.Id));
        var mine = threads[0];
        Assert.Equal(("src/counter.py", 6, 5, false, false), (mine.Path, mine.Line, mine.OriginalLine, mine.IsResolved, mine.IsOutdated));
        Assert.Equal([("102", "reviewer@contoso.com"), ("102.2", "author@contoso.com")], mine.Comments.Select(c => (c.Id, c.Author)));
        Assert.True(threads[1].IsResolved);
        Assert.Equal((true, (int?)null, (int?)3), (threads[2].IsOutdated, threads[2].Line, threads[2].OriginalLine));
    }

    [Fact]
    public async Task A_review_is_its_summary_thread_and_the_threads_that_name_it_with_the_vote_as_its_state()
    {
        var (provider, handler, _) = Create();
        WithThreads(handler)
            .On("/contoso/_apis/connectionData", HttpStatusCode.OK, Recording("connection-data.json"))
            .On($"{Repo}/pullRequests/7/reviewers/{Me}?api-version=7.1", HttpStatusCode.OK, Recording("reviewer-approved.json"));

        var review = await provider.GetReviewAsync(7, "101", Cancel);
        var reviews = await provider.ListReviewsAsync(7, Cancel);
        var comments = await provider.ListReviewCommentsAsync(7, Cancel);

        Assert.NotNull(review);
        Assert.Equal(("reviewer@contoso.com", "APPROVED"), (review.Author, review.State));
        Assert.StartsWith("Not ready: 1 Should open.", review.Body, StringComparison.Ordinal);
        Assert.Equal([("102", "src/counter.py"), ("104", "src/counter.py")], review.Comments.Select(c => (c.Id, c.Path)));
        Assert.Null(await provider.GetReviewAsync(7, "102", Cancel));
        Assert.Equal(["101"], reviews.Select(r => r.Id));
        Assert.Equal(
            [("102", "101", null), ("102.2", null, "102"), ("103", null, null), ("104", "101", null)],
            comments.Select(c => (c.Id, c.ReviewId, c.InReplyTo)));
    }

    [Fact]
    public async Task Someone_who_isn_t_a_reviewer_has_no_vote()
    {
        var (provider, handler, _) = Create();
        WithThreads(handler)
            .On("/contoso/_apis/connectionData", HttpStatusCode.OK, Recording("connection-data.json"))
            .On($"{Repo}/pullRequests/7/reviewers/{Me}?api-version=7.1", HttpStatusCode.BadRequest,
                """{"message":"Invalid argument value.\r\nParameter name: A valid reviewer must be supplied.","typeKey":"InvalidArgumentValueException"}""");

        var review = await provider.GetReviewAsync(7, "101", Cancel);

        Assert.Equal("COMMENTED", review!.State);
    }

    [Fact]
    public async Task A_summary_goes_as_a_closed_thread_without_a_file()
    {
        var (provider, handler, _) = Create();
        handler.On($"{Repo}/pullRequests/7/threads?api-version=7.1", HttpStatusCode.OK, Recording("thread-created.json").Replace("\"id\": 110", "\"id\": 109", StringComparison.Ordinal));

        var posted = await provider.StartThreadAsync(new ThreadPost(7, Head, "Ready to merge from my side.", null, null), Cancel);

        Assert.Equal("109", posted.Id);
        Assert.Equal("https://dev.azure.com/contoso/Fabrikam%20Fiber/_git/web-app/pullrequest/7?discussionId=109", posted.Url);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.Equal(4, body["status"]!.GetValue<int>());
        Assert.Equal("Ready to merge from my side.", body["comments"]![0]!["content"]!.GetValue<string>());
        Assert.Equal((0, 1), (body["comments"]![0]!["parentCommentId"]!.GetValue<int>(), body["comments"]![0]!["commentType"]!.GetValue<int>()));
        Assert.False(body.ContainsKey("threadContext"));
        Assert.False(body.ContainsKey("properties"));
    }

    [Fact]
    public async Task An_inline_comment_goes_on_the_reviewed_iteration_and_names_its_review()
    {
        var (provider, handler, _) = Create();
        handler
            .On($"{Repo}/pullRequests/7/iterations?api-version=7.1", HttpStatusCode.OK, Recording("iterations.json"))
            .On($"{Repo}/pullRequests/7/iterations/2/changes?$top=2000&$skip=0&api-version=7.1", HttpStatusCode.OK, Recording("iteration-changes.json"))
            .On($"{Repo}/pullRequests/7/threads?api-version=7.1", HttpStatusCode.OK, Recording("thread-created.json"));

        var posted = await provider.StartThreadAsync(
            new ThreadPost(7, Head, "Should: a negative amount makes a count go below zero.", new ReviewPostComment("src/counter.py", 6, DiffSide.New, "x"), "109"), Cancel);

        Assert.Equal(("110", "src/counter.py", (int?)6), (posted.Id, posted.Path, posted.Line));
        var body = JsonNode.Parse(handler.Requests.Last().Body!)!.AsObject();
        Assert.Equal(1, body["status"]!.GetValue<int>());
        Assert.Equal("/src/counter.py", body["threadContext"]!["filePath"]!.GetValue<string>());
        Assert.Equal(6, body["threadContext"]!["rightFileStart"]!["line"]!.GetValue<int>());
        Assert.False(body["threadContext"]!.AsObject().ContainsKey("leftFileStart"));
        Assert.Equal((1, 2), (body["pullRequestThreadContext"]!["iterationContext"]!["firstComparingIteration"]!.GetValue<int>(),
            body["pullRequestThreadContext"]!["iterationContext"]!["secondComparingIteration"]!.GetValue<int>()));
        Assert.Equal(1, body["pullRequestThreadContext"]!["changeTrackingId"]!.GetValue<int>());
        Assert.Equal("109", body["properties"]![AzureDevOpsProvider.ReviewProperty]!["$value"]!.GetValue<string>());
        Assert.Equal("System.String", body["properties"]![AzureDevOpsProvider.ReviewProperty]!["$type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_comment_on_a_removed_line_goes_on_the_left_side()
    {
        var (provider, handler, _) = Create();
        handler
            .On($"{Repo}/pullRequests/7/iterations?api-version=7.1", HttpStatusCode.OK, Recording("iterations.json"))
            .On($"{Repo}/pullRequests/7/iterations/2/changes?$top=2000&$skip=0&api-version=7.1", HttpStatusCode.OK, Recording("iteration-changes.json"))
            .On($"{Repo}/pullRequests/7/threads?api-version=7.1", HttpStatusCode.OK, Recording("thread-created.json"));

        await provider.StartThreadAsync(new ThreadPost(7, Head, "Could: keep this.", new ReviewPostComment("src/counter.py", 4, DiffSide.Old, "x"), "109"), Cancel);

        var context = JsonNode.Parse(handler.Requests.Last().Body!)!["threadContext"]!.AsObject();
        Assert.Equal(4, context["leftFileStart"]!["line"]!.GetValue<int>());
        Assert.False(context.ContainsKey("rightFileStart"));
    }

    [Fact]
    public async Task A_thread_that_may_have_landed_is_never_sent_twice()
    {
        var (provider, handler, waits) = Create();
        handler.On($"{Repo}/pullRequests/7/threads?api-version=7.1", HttpStatusCode.BadGateway, "{}");

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.StartThreadAsync(new ThreadPost(7, Head, "Ready.", null, null), Cancel));

        Assert.True(error.MayHaveLanded);
        Assert.Single(handler.Requests);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A_reply_is_named_by_its_thread_and_comment()
    {
        var (provider, handler, _) = Create();
        handler.On($"{Repo}/pullRequests/7/threads/102/comments?api-version=7.1", HttpStatusCode.OK, Recording("comment-created.json"));

        var reply = await provider.ReplyAsync(7, "102", "Looks good now.", Cancel);

        Assert.Equal(("102.4", "102", "reviewer@contoso.com"), (reply.Id, reply.InReplyTo, reply.Author));
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal((1, "Looks good now.", 1), (body["parentCommentId"]!.GetValue<int>(), body["content"]!.GetValue<string>(), body["commentType"]!.GetValue<int>()));
        await Assert.ThrowsAsync<ProviderException>(() => provider.ReplyAsync(7, "102.4", "x", Cancel));
    }

    [Fact]
    public async Task Resolving_sets_the_thread_to_fixed()
    {
        var (provider, handler, _) = Create();
        handler.On($"{Repo}/pullRequests/7/threads/102?api-version=7.1", HttpStatusCode.OK, Recording("thread-fixed.json"));

        await provider.ResolveThreadAsync(7, "102", Cancel);

        var (request, body) = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal(2, JsonNode.Parse(body!)!["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Approve_votes_10_request_changes_minus_5_and_a_comment_leaves_the_vote_alone()
    {
        var (provider, handler, _) = Create();
        handler
            .On("/contoso/_apis/connectionData", HttpStatusCode.OK, Recording("connection-data.json"))
            .On($"{Repo}/pullRequests/7/reviewers/{Me}?api-version=7.1", HttpStatusCode.OK, Recording("reviewer-approved.json"));

        await provider.SetVoteAsync(7, Verdict.Comment, Cancel);
        Assert.Empty(handler.Requests);

        await provider.SetVoteAsync(7, Verdict.Approve, Cancel);
        var (request, body) = handler.Requests.Last();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(10, JsonNode.Parse(body!)!["vote"]!.GetValue<int>());

        // The answer says 10, so -5 didn't take: that's an error, not a silent pass.
        await Assert.ThrowsAsync<ProviderException>(() => provider.SetVoteAsync(7, Verdict.RequestChanges, Cancel));
        Assert.Equal(-5, JsonNode.Parse(handler.Requests.Last().Body!)!["vote"]!.GetValue<int>());
    }

    [Fact]
    public async Task Git_fetches_the_head_by_SHA_with_a_bearer_token_for_Entra_and_basic_auth_for_a_PAT()
    {
        var jwt = "eyJ" + Tokens.Random(30) + "." + Tokens.Random(40) + "." + Tokens.Random(20);
        var (entra, _, _) = Create(TestTokens.Fixed(jwt));
        var (pat, _, _) = Create(TestTokens.Fixed(Tokens.Random(52)));
        var pr = FakeProvider.Pr(Head) with { Number = 7 };

        var bearer = await entra.FetchSourceAsync(pr, Cancel);
        var basic = await pat.FetchSourceAsync(pr, Cancel);

        Assert.Equal(("https://dev.azure.com/contoso/Fabrikam%20Fiber/_git/web-app", Head), (bearer.Url, bearer.HeadRef));
        Assert.StartsWith("AUTHORIZATION: Bearer eyJ", bearer.Credential!.Header, StringComparison.Ordinal);
        Assert.StartsWith("AUTHORIZATION: Basic ", basic.Credential!.Header, StringComparison.Ordinal);
        Assert.Equal("[credential]", bearer.Credential.ToString());
    }

    [Fact]
    public async Task A_sign_in_page_is_a_refused_token_and_a_renewable_one_is_renewed_once()
    {
        var runs = 0;
        var cli = new AzureCliTokenSource("contoso.onmicrosoft.com", TimeProvider.System, (_, _) =>
        {
            runs++;
            var token = "eyJ" + Tokens.Random(20) + "." + Tokens.Random(20) + "." + Tokens.Random(20);
            return Task.FromResult(new AzureCliAnswer(0, $$"""{"accessToken": "{{token}}", "expires_on": {{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""", ""));
        });
        var (provider, handler, _) = Create(cli);
        handler
            .On("/contoso/_apis/connectionData", HttpStatusCode.NonAuthoritativeInformation, "<html><title>Sign in to your account</title></html>", "text/html")
            .On("/contoso/_apis/connectionData", HttpStatusCode.OK, Recording("connection-data.json"));

        Assert.Equal("reviewer@contoso.com", await provider.WhoAmIAsync(Cancel));
        Assert.Equal(2, runs);
        Assert.NotEqual(handler.Requests[0].Request.Headers.Authorization!.Parameter, handler.Requests[1].Request.Headers.Authorization!.Parameter);

        var (once, onceHandler, _) = Create(TestTokens.Fixed(Tokens.Random(52)));
        onceHandler.On("/contoso/_apis/connectionData", HttpStatusCode.Redirect, "", "text/html", ("Location", "https://login.microsoftonline.com/"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => once.WhoAmIAsync(Cancel));
        Assert.True(error.IsAuth);
        Assert.Contains("sign-in page", error.Message, StringComparison.Ordinal);
        Assert.Single(onceHandler.Requests);
    }

    [Fact]
    public async Task A_rate_limit_waits_as_long_as_Retry_After_says()
    {
        var (provider, handler, waits) = Create();
        handler
            .On($"{Repo}?api-version=7.1", HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "7"))
            .On($"{Repo}?api-version=7.1", HttpStatusCode.OK, Recording("repository.json"));

        await provider.GetRepositoryAsync(Cancel);

        Assert.Equal([TimeSpan.FromSeconds(7)], waits);
    }
}
