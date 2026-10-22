using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Posting;
using Rtfd.Providers;
using Rtfd.Providers.Bitbucket;
using Rtfd.State;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// The Bitbucket provider against recorded answers (Recordings/bitbucket: the shapes of a real
/// workspace's answers, with every name and id replaced).
/// </summary>
public sealed class BitbucketProviderTests
{
    private const string Repo = "/2.0/repositories/contoso/web-app";
    private const string Head = "4f2c8e1a9b7d6c5e3f1a2b4c6d8e0f1a3b5c7d9e";
    private const string Base = "9b8a7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b";

    private static string Recording(string name) =>
        File.ReadAllText(Path.Combine(Configs.RepositoryRoot, "tests", "Rtfd.Tests", "Recordings", "bitbucket", name));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string ApiToken() => "pat@contoso.com:" + Tokens.Random(60);

    private static (BitbucketProvider Provider, RecordedHandler Handler, List<TimeSpan> Waits) Create(string? token = null)
    {
        var handler = new RecordedHandler();
        var waits = new List<TimeSpan>();
        var provider = new BitbucketProvider("contoso/web-app", TestTokens.Fixed(token ?? ApiToken()), handler, (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });
        return (provider, handler, waits);
    }

    /// <summary>What Bitbucket answers for a commit asked for by its first 12 characters.</summary>
    private static RecordedHandler Commits(RecordedHandler handler, params string[] shas)
    {
        foreach (var sha in shas)
        {
            handler.On($"{Repo}/commit/{sha[..12]}", HttpStatusCode.OK, $$"""{"hash": "{{sha}}", "type": "commit"}""");
        }

        return handler;
    }

    private static RecordedHandler WithComments(RecordedHandler handler) => handler
        .On($"{Repo}/pullrequests/7/comments?pagelen=100", HttpStatusCode.OK, Recording("comments.json"))
        .On($"{Repo}/pullrequests/7/comments/5002", HttpStatusCode.OK, Recording("comment-5002.json"))
        .On($"{Repo}/pullrequests/7/comments/5003", HttpStatusCode.OK, Recording("comment-5003.json"))
        .On($"{Repo}/pullrequests/7/comments/5007", HttpStatusCode.OK, Recording("comment-5007.json"));

    [Fact]
    public async Task Who_rtfd_posts_as_is_the_account_s_nickname()
    {
        var (provider, handler, _) = Create();
        handler.On("/2.0/user", HttpStatusCode.OK, Recording("user.json"));

        Assert.Equal("Pat Reviewer", await provider.WhoAmIAsync(Cancel));
    }

    [Fact]
    public async Task A_private_repo_s_main_branch_is_its_default()
    {
        var (provider, handler, _) = Create();
        handler.On(Repo, HttpStatusCode.OK, Recording("repository.json"));

        Assert.Equal(new RepositoryInfo(false, "main"), await provider.GetRepositoryAsync(Cancel));
    }

    [Fact]
    public async Task A_PR_s_short_hashes_become_full_SHAs_asked_for_once_each()
    {
        var (provider, handler, _) = Create();
        Commits(handler, Head, Base).On($"{Repo}/pullrequests/7", HttpStatusCode.OK, Recording("pull-request.json"));

        var pr = await provider.GetPullRequestAsync(7, Cancel);
        await provider.GetPullRequestAsync(7, Cancel);

        Assert.Equal((7, "sam.author", "open", false, false), (pr.Number, pr.Author, pr.State, pr.IsDraft, pr.IsFork));
        Assert.Equal((Head, "feature/add", Base, "main"), (pr.HeadSha, pr.HeadRef, pr.BaseSha, pr.BaseRef));
        Assert.Equal("https://bitbucket.org/contoso/web-app/pull-requests/7", pr.Url);
        Assert.Equal(2, handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath.Contains("/commit/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_open_PRs_say_which_are_drafts_and_forks_and_an_unchanged_list_is_a_304()
    {
        var (provider, handler, _) = Create();
        Commits(handler, Head, Base, "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b", "aabbccddeeff00112233445566778899aabbccdd")
            .On($"{Repo}/pullrequests?state=OPEN&pagelen=50", HttpStatusCode.OK, Recording("pull-requests.json"), ("ETag", "\"272fbd16\""))
            .On($"{Repo}/pullrequests?state=OPEN&pagelen=50", HttpStatusCode.NotModified, "");

        var first = await provider.ListOpenPullRequestsAsync(Cancel);
        var second = await provider.ListOpenPullRequestsAsync(Cancel);

        Assert.Equal([(7, false, false), (8, true, false), (9, false, true)], first.Select(p => (p.Number, p.IsDraft, p.IsFork)));
        Assert.Equal(first, second);
        var lists = handler.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal)).ToList();
        Assert.Equal("\"272fbd16\"", lists[1].Request.Headers.IfNoneMatch.Single().ToString());
    }

    [Fact]
    public async Task A_missing_PR_is_a_404_even_in_plain_text()
    {
        var (provider, handler, _) = Create();
        handler.On($"{Repo}/pullrequests/404", HttpStatusCode.NotFound, "Not Found", "text/plain");

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.GetPullRequestAsync(404, Cancel));

        Assert.Equal(404, error.Status);
        Assert.Contains("Bitbucket has no /2.0/repositories/contoso/web-app/pullrequests/404", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CI_is_the_head_commit_s_statuses_and_a_stopped_build_fails_it()
    {
        var statuses = $"{Repo}/commit/{Head}/statuses?pagelen=100";
        var (running, runningHandler, _) = Create();
        runningHandler.On(statuses, HttpStatusCode.OK, Recording("statuses.json"));
        var (stopped, stoppedHandler, _) = Create();
        stoppedHandler.On(statuses, HttpStatusCode.OK, Recording("statuses.json").Replace("INPROGRESS", "SUCCESSFUL", StringComparison.Ordinal));
        var (passed, passedHandler, _) = Create();
        passedHandler.On(statuses, HttpStatusCode.OK, Recording("statuses.json").Replace("INPROGRESS", "SUCCESSFUL", StringComparison.Ordinal).Replace("STOPPED", "SUCCESSFUL", StringComparison.Ordinal));

        var ci = await running.GetCiStateAsync(7, Head, Cancel);

        Assert.Equal(CiSummary.Pending, ci.Summary);
        Assert.Equal(["ci/circleci: test", "ci/circleci: sonarcloud", "sonarcloud"], ci.Checks.Select(c => c.Name));
        Assert.Equal(("completed", "cancelled"), (ci.Checks[2].Status, ci.Checks[2].Conclusion));
        Assert.Equal(CiSummary.Failure, (await stopped.GetCiStateAsync(7, Head, Cancel)).Summary);
        Assert.Equal(CiSummary.Success, (await passed.GetCiStateAsync(7, Head, Cancel)).Summary);
    }

    [Fact]
    public async Task Threads_are_the_live_inline_ones_resolved_as_each_first_comment_says_on_its_own()
    {
        var (provider, handler, _) = Create();
        WithComments(handler);

        var threads = await provider.GetReviewThreadsAsync(7, Cancel);

        Assert.Equal(["5002", "5003", "5007"], threads.Select(t => t.Id));
        var fixedOne = threads[0];
        Assert.Equal((true, "sam.author", false, (int?)6, "src/counter.py"), (fixedOne.IsResolved, fixedOne.ResolvedBy, fixedOne.IsOutdated, fixedOne.Line, fixedOne.Path));
        Assert.Equal(["5002", "5004", "5005"], fixedOne.Comments.Select(c => c.Id));
        Assert.Equal("Should: a negative amount makes the count go below zero.", fixedOne.Comments[0].Body);
        Assert.Equal(["Pat Reviewer", "sam.author", "Pat Reviewer"], fixedOne.Comments.Select(c => c.Author));

        var outdated = threads[1];
        Assert.Equal((false, true, (int?)null, (int?)4), (outdated.IsResolved, outdated.IsOutdated, outdated.Line, outdated.OriginalLine));
        Assert.Equal("docs/Counting Guide.md", threads[2].Path);
    }

    [Fact]
    public async Task A_review_is_its_summary_and_the_inline_comments_naming_it_with_the_vote_as_its_state()
    {
        var (provider, handler, _) = Create();
        Commits(WithComments(handler), Head, Base)
            .On("/2.0/user", HttpStatusCode.OK, Recording("user.json"))
            .On($"{Repo}/pullrequests/7", HttpStatusCode.OK, Recording("pull-request.json"));

        var review = await provider.GetReviewAsync(7, "5001", Cancel);

        Assert.NotNull(review);
        Assert.Equal(("Pat Reviewer", "CHANGES_REQUESTED"), (review.Author, review.State));
        Assert.Equal(new LastReview(Head, Base), Poster.ParseMarker(review.Body));
        Assert.Equal([("5002", "src/counter.py", (int?)6), ("5003", "src/counter.py", (int?)4)], review.Comments.Select(c => (c.Id, c.Path, c.Line)));
        Assert.Equal("Could: keep the guard against a missing amount.", review.Comments[1].Body);
        Assert.Null(await provider.GetReviewAsync(7, "5002", Cancel));
    }

    [Fact]
    public async Task General_comments_are_candidate_summaries_and_every_inline_comment_knows_its_thread()
    {
        var (provider, handler, _) = Create();
        WithComments(handler);

        var reviews = await provider.ListReviewsAsync(7, Cancel);
        var comments = await provider.ListReviewCommentsAsync(7, Cancel);

        Assert.Equal(["5001", "5006"], reviews.Select(r => r.Id));
        Assert.Equal(
            [("5002", "5001", null), ("5003", "5001", null), ("5004", null, "5002"), ("5005", null, "5002"), ("5007", null, null)],
            comments.Select(c => (c.Id, c.ReviewId, c.InReplyTo)));
        Assert.Equal("Should: a negative amount makes the count go below zero.", comments[0].Body);
    }

    [Fact]
    public async Task A_summary_goes_as_a_general_comment_and_an_inline_one_names_its_review_in_a_hidden_line()
    {
        var (provider, handler, _) = Create();
        handler.On($"{Repo}/pullrequests/7/comments", HttpStatusCode.Created, Recording("comment-created.json"));

        await provider.StartThreadAsync(new ThreadPost(7, Head, "Ready to merge from my side.", null, null), Cancel);
        var posted = await provider.StartThreadAsync(
            new ThreadPost(7, Head, "Should: a negative amount makes the count go below zero.", new ReviewPostComment("src/counter.py", 6, DiffSide.New, "x"), "5010"), Cancel);
        await provider.StartThreadAsync(new ThreadPost(7, Head, "Could: keep this.", new ReviewPostComment("src/counter.py", 4, DiffSide.Old, "x"), "5010"), Cancel);

        var summary = JsonNode.Parse(handler.Requests[0].Body!)!.AsObject();
        Assert.Equal("Ready to merge from my side.", summary["content"]!["raw"]!.GetValue<string>());
        Assert.False(summary.ContainsKey("inline"));

        var inline = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("Should: a negative amount makes the count go below zero.\n\n[//]: # (rtfd review=5010)", inline["content"]!["raw"]!.GetValue<string>());
        Assert.Equal(("src/counter.py", 6), (inline["inline"]!["path"]!.GetValue<string>(), inline["inline"]!["to"]!.GetValue<int>()));
        Assert.False(inline["inline"]!.AsObject().ContainsKey("from"));
        Assert.Equal(("5011", "https://bitbucket.org/contoso/web-app/pull-requests/7/_/diff#comment-5011"), (posted.Id, posted.Url));
        Assert.Equal("Should: a negative amount makes the count go below zero.", posted.Body);

        var removed = JsonNode.Parse(handler.Requests[2].Body!)!["inline"]!.AsObject();
        Assert.Equal(4, removed["from"]!.GetValue<int>());
        Assert.False(removed.ContainsKey("to"));
    }

    [Fact]
    public async Task A_comment_that_may_have_landed_is_never_sent_twice()
    {
        var (provider, handler, waits) = Create();
        handler.On($"{Repo}/pullrequests/7/comments", HttpStatusCode.BadGateway, "{}");

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.StartThreadAsync(new ThreadPost(7, Head, "Ready.", null, null), Cancel));

        Assert.True(error.MayHaveLanded);
        Assert.Single(handler.Requests);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A_reply_names_its_parent_and_resolving_twice_is_fine()
    {
        var (provider, handler, _) = Create();
        handler
            .On($"{Repo}/pullrequests/7/comments", HttpStatusCode.Created, Recording("comment-created.json").Replace("\"id\": 5011", "\"id\": 5012", StringComparison.Ordinal))
            .On($"{Repo}/pullrequests/7/comments/5002/resolve", HttpStatusCode.OK, Recording("comment-5002.json"))
            .On($"{Repo}/pullrequests/7/comments/5002/resolve", HttpStatusCode.Conflict, Recording("resolve-already.json"));

        var reply = await provider.ReplyAsync(7, "5002", "Looks good now.", Cancel);
        await provider.ResolveThreadAsync(7, "5002", Cancel);
        await provider.ResolveThreadAsync(7, "5002", Cancel);

        Assert.Equal(("5012", "5002"), (reply.Id, reply.InReplyTo));
        var body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal(("Looks good now.", 5002L), (body["content"]!["raw"]!.GetValue<string>(), body["parent"]!["id"]!.GetValue<long>()));
        Assert.Equal(3, handler.Requests.Count);
        await Assert.ThrowsAsync<ProviderException>(() => provider.ReplyAsync(7, "5002.1", "x", Cancel));
    }

    [Fact]
    public async Task Approve_and_request_changes_check_the_state_that_comes_back_and_a_comment_leaves_the_vote_alone()
    {
        var (provider, handler, _) = Create();
        handler
            .On($"{Repo}/pullrequests/7/approve", HttpStatusCode.OK, Recording("participant-approved.json"))
            .On($"{Repo}/pullrequests/7/request-changes", HttpStatusCode.OK, Recording("participant-approved.json"));

        await provider.SetVoteAsync(7, Verdict.Comment, Cancel);
        Assert.Empty(handler.Requests);

        await provider.SetVoteAsync(7, Verdict.Approve, Cancel);
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Request.Method);

        // The answer still says approved, so the change request didn't take: an error, not a silent pass.
        await Assert.ThrowsAsync<ProviderException>(() => provider.SetVoteAsync(7, Verdict.RequestChanges, Cancel));
    }

    [Fact]
    public async Task An_API_token_is_basic_auth_and_git_gets_its_token_part_under_a_fixed_user_name()
    {
        var token = ApiToken();
        var (provider, handler, _) = Create(token);
        handler.On("/2.0/user", HttpStatusCode.OK, Recording("user.json"));

        await provider.WhoAmIAsync(Cancel);
        var source = await provider.FetchSourceAsync(FakeProvider.Pr(Head) with { Number = 7 }, Cancel);

        var rest = handler.Requests.Single().Request.Headers.Authorization!;
        Assert.Equal(("Basic", token), (rest.Scheme, Encoding.UTF8.GetString(Convert.FromBase64String(rest.Parameter!))));
        Assert.Equal(("https://bitbucket.org/contoso/web-app.git", Head), (source.Url, source.HeadRef));
        var git = source.Credential!.Header;
        Assert.StartsWith("AUTHORIZATION: basic ", git, StringComparison.Ordinal);
        Assert.Equal("x-bitbucket-api-token-auth:" + token.Split(':', 2)[1], Encoding.UTF8.GetString(Convert.FromBase64String(git["AUTHORIZATION: basic ".Length..])));
    }

    [Fact]
    public async Task An_access_token_without_a_colon_is_a_bearer_token()
    {
        var token = Tokens.Random(60);
        var (provider, handler, _) = Create(token);
        handler.On("/2.0/user", HttpStatusCode.OK, Recording("user.json"));

        await provider.WhoAmIAsync(Cancel);
        var source = await provider.FetchSourceAsync(FakeProvider.Pr(Head), Cancel);

        Assert.Equal(("Bearer", token), (handler.Requests.Single().Request.Headers.Authorization!.Scheme, handler.Requests.Single().Request.Headers.Authorization!.Parameter));
        Assert.Equal("x-token-auth:" + token, Encoding.UTF8.GetString(Convert.FromBase64String(source.Credential!.Header["AUTHORIZATION: basic ".Length..])));
    }

    [Fact]
    public async Task A_refused_token_says_what_the_token_should_be()
    {
        var (provider, handler, _) = Create();
        handler.On("/2.0/user", HttpStatusCode.Unauthorized, "", "text/plain");

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.WhoAmIAsync(Cancel));

        Assert.True(error.IsAuth);
        Assert.Contains("refused the token in GH_TOKEN (401)", error.Message, StringComparison.Ordinal);
        Assert.Contains("email:token", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_rate_limit_waits_as_long_as_Retry_After_says()
    {
        var (provider, handler, waits) = Create();
        handler
            .On(Repo, HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "7"))
            .On(Repo, HttpStatusCode.OK, Recording("repository.json"));

        await provider.GetRepositoryAsync(Cancel);

        Assert.Equal([TimeSpan.FromSeconds(7)], waits);
    }

    [Fact]
    public void The_secret_check_knows_the_token_part_of_email_token_on_its_own()
    {
        using var temp = new TempHome();
        var config = temp.Load(Configs.Minimal().Replace("provider: github", "provider: bitbucket").Replace("token_env: GH_TOKEN", "token_env: BB_TOKEN")).Config!;
        var token = ApiToken();

        var known = TestTokens.From(name => name == "BB_TOKEN" ? token : null).Known(config);

        Assert.Equal([token, token.Split(':', 2)[1]], known);
    }

    [Fact]
    public void Bitbucket_s_marker_is_a_link_reference_and_both_kinds_parse()
    {
        var bitbucket = Poster.Marker(Head, Base, 3, ProviderKind.Bitbucket);
        var github = Poster.Marker(Head, null, 3);

        Assert.Equal($"[//]: # (rtfd sha={Head} base={Base} run=3)", bitbucket);
        Assert.Equal(new LastReview(Head, Base), Poster.ParseMarker($"Summary.\n\n{bitbucket}"));
        Assert.Equal(new LastReview(Head, null), Poster.ParseMarker($"Summary.\n\n{github}"));
        Assert.Null(Poster.ParseMarker($"[//]: # (rtfd sha={Head} run=3 -->"));
    }
}
