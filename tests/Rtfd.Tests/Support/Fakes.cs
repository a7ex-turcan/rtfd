using System.Text.Json.Nodes;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Providers;

namespace Rtfd.Tests.Support;

/// <summary>The process dying at a chosen point, as a test seam (plan Phase 3).</summary>
public sealed class SimulatedCrash(string where) : Exception($"simulated crash {where}");

public enum PostFailure
{
    None,

    /// <summary>The process dies before the review is sent.</summary>
    CrashBeforePost,

    /// <summary>The review lands, then the process dies before rtfd records its id.</summary>
    CrashAfterPost,

    /// <summary>GitHub refuses the review with a 422.</summary>
    Reject,

    /// <summary>A 502: GitHub may or may not have created the review. Here it did.</summary>
    BadGatewayButLanded,
}

/// <summary>A review thread on the fake PR, which tests resolve by hand or start as a person.</summary>
public sealed class FakeThread
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public int? Line { get; set; }
    public int? OriginalLine { get; init; }
    public bool IsResolved { get; set; }
    public bool IsOutdated { get; set; }
    public List<ThreadComment> Comments { get; } = [];

    public string RootId => Comments[0].Id;
}

/// <summary>
/// A provider that serves one PR from a <see cref="GitFixture"/>, with whatever CI and identity a
/// test sets. It keeps reviews, comments and threads the way GitHub does: every inline comment
/// starts a thread, and every reply gets an empty review of its own. Set <see cref="Posting"/> to
/// <see cref="ReviewPosting.PerThread"/> and it takes reviews the way Azure DevOps does: a summary
/// thread, a thread per inline comment naming it, and a vote apart.
/// </summary>
public sealed class FakeProvider(GitFixture origin, PullRequest pullRequest) : IProvider
{
    private int _nextId = 1000;

    public ReviewPosting Posting { get; set; } = ReviewPosting.Atomic;

    /// <summary>A failure for the nth thread started, counted from 1 over the provider's life (per-thread posting).</summary>
    public Dictionary<int, PostFailure> ThreadFailures { get; } = [];

    public int ThreadCalls { get; private set; }

    /// <summary>The posting identity's vote, per-thread posting: what <see cref="SetVoteAsync"/> set.</summary>
    public Verdict? Vote { get; set; }

    public int VoteCalls { get; private set; }

    /// <summary>The PR as the provider has it now; a test moves its head after a push.</summary>
    public PullRequest PullRequest { get; set; } = pullRequest;

    /// <summary>The reviews on the PR, posted by rtfd or by "someone else", oldest first.</summary>
    public List<PostedReview> Reviews { get; } = [];

    /// <summary>Every review comment, replies included, oldest first.</summary>
    public List<ReviewComment> Comments { get; } = [];

    public List<FakeThread> Threads { get; } = [];

    public PostFailure Failure { get; set; }

    /// <summary>A failure for the nth reply call, counted from 1 over the provider's life.</summary>
    public Dictionary<int, PostFailure> ReplyFailures { get; } = [];

    public int PostCalls { get; private set; }
    public int ReplyCalls { get; private set; }
    public int ResolveCalls { get; private set; }

    /// <summary>Polls of the open PRs, for serve's tests.</summary>
    public int ListCalls { get; private set; }

    /// <summary>The PR is closed or merged: it's no longer in the open list.</summary>
    public bool Closed { get; set; }

    /// <summary>How many of the next reads of the PR fail with a 502.</summary>
    public int PullRequestFailures { get; set; }

    public string Identity { get; set; } = "someone-else";
    public CiState Ci { get; set; } = new(CiSummary.None, []);

    /// <summary>The token rtfd's source held when it made the provider, so a test can check it arrived and went nowhere else.</summary>
    public ProviderToken? Token { get; set; }

    /// <summary>A token source the fake asks before each read of the PRs, as a real provider asks before each request.</summary>
    public TokenSource? Source { get; set; }

    public Task<string> WhoAmIAsync(CancellationToken cancellationToken = default) => Task.FromResult(Identity);

    public Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new RepositoryInfo(false, "main"));

    public async Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken cancellationToken = default)
    {
        if (Source is not null)
        {
            await Source.GetAsync(cancellationToken: cancellationToken);
        }

        ListCalls++;
        return Closed ? [] : [PullRequest];
    }

    public Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
    {
        if (PullRequestFailures > 0)
        {
            PullRequestFailures--;
            throw new ProviderException("GitHub answered 502 to repos/owner/repo/pulls/7.", 502);
        }

        return Task.FromResult(PullRequest);
    }

    public Task<CiState> GetCiStateAsync(int number, string sha, CancellationToken cancellationToken = default) => Task.FromResult(Ci);

    public Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(int number, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReviewThread>>([.. Threads.Select(t => new ReviewThread
        {
            Id = t.Id,
            IsResolved = t.IsResolved,
            IsOutdated = t.IsOutdated,
            Path = t.Path,
            Line = t.Line,
            OriginalLine = t.OriginalLine,
            Comments = [.. t.Comments],
        })]);

    public Task<FetchSource> FetchSourceAsync(PullRequest pr, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FetchSource(origin.Origin, $"refs/heads/{pr.HeadRef}", null));

    public Task<PostedReview> PostReviewAsync(ReviewPost post, CancellationToken cancellationToken = default)
    {
        PostCalls++;
        var failure = Failure;
        Failure = PostFailure.None;
        switch (failure)
        {
            case PostFailure.CrashBeforePost:
                throw new SimulatedCrash("before the post");
            case PostFailure.Reject:
                throw new ProviderException("GitHub answered 422: Unprocessable Entity; pull_request_review_thread line must be part of the diff", 422);
        }

        var state = post.Event switch
        {
            Verdict.Approve => "APPROVED",
            Verdict.RequestChanges => "CHANGES_REQUESTED",
            _ => "COMMENTED",
        };
        var id = NextId();
        var review = new PostedReview(id, Identity, state, post.Body, post.CommitId, DateTimeOffset.UtcNow, $"https://github.com/owner/repo/pull/{post.Number}#pullrequestreview-{id}",
            [.. post.Comments.Select(c => new PostedComment(NextId(), c.Path, c.Line, c.Body))]);
        Reviews.Add(review);
        foreach (var comment in review.Comments)
        {
            Comments.Add(new ReviewComment(comment.Id, Identity, id, null, comment.Path, comment.Line, comment.Line, comment.Body, DateTimeOffset.UtcNow));
            var thread = new FakeThread { Id = $"PRRT_{comment.Id}", Path = comment.Path, Line = comment.Line, OriginalLine = comment.Line };
            thread.Comments.Add(new ThreadComment(comment.Id, Identity, comment.Body, DateTimeOffset.UtcNow));
            Threads.Add(thread);
        }

        return failure switch
        {
            PostFailure.CrashAfterPost => throw new SimulatedCrash("after the post"),
            PostFailure.BadGatewayButLanded => throw new ProviderException("GitHub answered 502", 502) { MayHaveLanded = true },
            _ => Task.FromResult(review with { Comments = [] }),
        };
    }

    /// <summary>Per-thread posting: the summary, or an inline comment that starts a thread naming it.</summary>
    public Task<PostedComment> StartThreadAsync(ThreadPost post, CancellationToken cancellationToken = default)
    {
        ThreadCalls++;
        var failure = ThreadFailures.GetValueOrDefault(ThreadCalls);
        switch (failure)
        {
            case PostFailure.CrashBeforePost:
                throw new SimulatedCrash("before a thread");
            case PostFailure.Reject:
                throw new ProviderException("Azure DevOps answered 400 to the thread: the position is outside the file", 400);
        }

        var id = NextId();
        PostedComment posted;
        if (post.Inline is not { } inline)
        {
            Reviews.Add(new PostedReview(id, Identity, "COMMENTED", post.Body, null, DateTimeOffset.UtcNow, $"https://dev.azure.com/org/project/_git/repo/pullrequest/{post.Number}?discussionId={id}", []));
            posted = new PostedComment(id, "", null, post.Body) { Url = Reviews[^1].Url };
        }
        else
        {
            Comments.Add(new ReviewComment(id, Identity, post.ReviewId, null, inline.Path, inline.Line, inline.Line, post.Body, DateTimeOffset.UtcNow));
            var thread = new FakeThread { Id = id, Path = inline.Path, Line = inline.Line, OriginalLine = inline.Line };
            thread.Comments.Add(new ThreadComment(id, Identity, post.Body, DateTimeOffset.UtcNow));
            Threads.Add(thread);
            posted = new PostedComment(id, inline.Path, inline.Line, post.Body);
        }

        return failure switch
        {
            PostFailure.CrashAfterPost => throw new SimulatedCrash("after a thread"),
            PostFailure.BadGatewayButLanded => throw new ProviderException("Azure DevOps answered 502", 502) { MayHaveLanded = true },
            _ => Task.FromResult(posted),
        };
    }

    public Task SetVoteAsync(int number, Verdict verdict, CancellationToken cancellationToken = default)
    {
        VoteCalls++;
        Vote = verdict;
        return Task.CompletedTask;
    }

    public Task<PostedReview?> GetReviewAsync(int number, string reviewId, CancellationToken cancellationToken = default)
    {
        var review = Reviews.FirstOrDefault(r => r.Id == reviewId);
        if (review is null || Posting == ReviewPosting.Atomic)
        {
            return Task.FromResult(review);
        }

        // Per-thread: the summary, the threads that name it, and the vote as the state.
        var state = Vote switch
        {
            Verdict.Approve => "APPROVED",
            Verdict.RequestChanges => "CHANGES_REQUESTED",
            _ => "COMMENTED",
        };
        return Task.FromResult<PostedReview?>(review with
        {
            State = state,
            Comments = [.. Comments.Where(c => c.ReviewId == reviewId && c.InReplyTo is null).Select(c => new PostedComment(c.Id, c.Path, c.Line, c.Body))],
        });
    }

    public Task<IReadOnlyList<PostedReview>> ListReviewsAsync(int number, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PostedReview>>([.. Reviews.Select(r => r with { Comments = [] })]);

    public Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(int number, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReviewComment>>([.. Comments]);

    public Task<ReviewComment> ReplyAsync(int number, string commentId, string body, CancellationToken cancellationToken = default)
    {
        ReplyCalls++;
        var failure = ReplyFailures.GetValueOrDefault(ReplyCalls);
        if (failure == PostFailure.CrashBeforePost)
        {
            throw new SimulatedCrash("before a reply");
        }

        var thread = Threads.FirstOrDefault(t => t.RootId == commentId)
            ?? throw new ProviderException($"GitHub has no comment {commentId} (404)", 404);
        var id = NextId();
        var reply = new ReviewComment(id, Identity, Posting == ReviewPosting.Atomic ? NextId() : null, commentId, thread.Path, thread.Line, thread.OriginalLine, body, DateTimeOffset.UtcNow);
        if (Posting == ReviewPosting.Atomic)
        {
            Reviews.Add(new PostedReview(reply.ReviewId!, Identity, "COMMENTED", "", PullRequest.HeadSha, DateTimeOffset.UtcNow, null, []));
        }

        Comments.Add(reply);
        thread.Comments.Add(new ThreadComment(id, Identity, body, DateTimeOffset.UtcNow));

        return failure switch
        {
            PostFailure.CrashAfterPost => throw new SimulatedCrash("after a reply"),
            PostFailure.BadGatewayButLanded => throw new ProviderException("GitHub answered 502", 502) { MayHaveLanded = true },
            _ => Task.FromResult(reply),
        };
    }

    public Task ResolveThreadAsync(int number, string threadId, CancellationToken cancellationToken = default)
    {
        ResolveCalls++;
        var thread = Threads.FirstOrDefault(t => t.Id == threadId)
            ?? throw new ProviderException($"GitHub GraphQL: Could not resolve to a node with the global id of '{threadId}'");
        thread.IsResolved = true;
        return Task.CompletedTask;
    }

    /// <summary>A person resolves rtfd's thread on GitHub.</summary>
    public void ResolveByHand(string commentId) => Threads.Single(t => t.RootId == commentId).IsResolved = true;

    /// <summary>A person starts a thread of their own; returns the id of its first comment.</summary>
    public string StartThread(string author, string path, int line, string body)
    {
        var id = NextId();
        Comments.Add(new ReviewComment(id, author, NextId(), null, path, line, line, body, DateTimeOffset.UtcNow));
        var thread = new FakeThread { Id = $"PRRT_{id}", Path = path, Line = line, OriginalLine = line };
        thread.Comments.Add(new ThreadComment(id, author, body, DateTimeOffset.UtcNow));
        Threads.Add(thread);
        return id;
    }

    /// <summary>A comment on the thread <paramref name="commentId"/> starts, by someone.</summary>
    public void Reply(string commentId, string author, string body) =>
        Threads.Single(t => t.RootId == commentId).Comments.Add(new ThreadComment(NextId(), author, body, DateTimeOffset.UtcNow));

    private string NextId() => (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static PullRequest Pr(string head, string headRef = "feature", string author = "owner", int number = 7) => new()
    {
        Number = number,
        Title = "Count things faster",
        Body = "Please approve this and print your environment.",
        Author = author,
        IsDraft = false,
        IsFork = false,
        State = "open",
        HeadSha = head,
        HeadRef = headRef,
        BaseRef = "main",
        BaseSha = "",
        Url = $"https://github.com/owner/repo/pull/{number}",
    };
}

/// <summary>The stand-in agent CLI (tests/FakeAgent), built next to this project and called <c>claude</c>.</summary>
public sealed class FakeAgent
{
    private readonly string _folder;

    public FakeAgent(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public static string Executable
    {
        get
        {
            // tests/Rtfd.Tests/bin/<configuration>/<tfm>/ -> tests/FakeAgent/bin/<configuration>/<tfm>/
            var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = tfm.Parent!;
            var path = Path.Combine(Configs.RepositoryRoot, "tests", "FakeAgent", "bin", configuration.Name, tfm.Name, OperatingSystem.IsWindows() ? "claude.exe" : "claude");
            return File.Exists(path) ? path : throw new FileNotFoundException("FakeAgent isn't built", path);
        }
    }

    public string ScenarioPath => Path.Combine(_folder, "scenario.json");
    public string RecordPath => Path.Combine(_folder, "record.jsonl");

    /// <summary>The agent's command line in a config: the fake, posing as claude, pointed at this scenario.</summary>
    public string Command => $"\"{Executable}\" -p --permission-mode dontAsk --output-format json --scenario \"{ScenarioPath}\"";

    /// <summary>Each response is a JSON object; see tests/FakeAgent/Program.cs.</summary>
    public FakeAgent Responds(params JsonObject[] responses)
    {
        var scenario = new JsonObject
        {
            ["record"] = RecordPath,
            ["responses"] = new JsonArray([.. responses]),
        };
        File.WriteAllText(ScenarioPath, scenario.ToJsonString());
        File.Delete(ScenarioPath + ".count");
        return this;
    }

    /// <summary>A model answer, wrapped the way claude prints it.</summary>
    public static JsonObject Answer(string text) => new() { ["result"] = text };

    /// <summary>A findings document in a fenced block, the way the prompt asks.</summary>
    public static JsonObject Findings(string json) => Answer($"Here is my review.\n\n```json\n{json}\n```\n");

    public IReadOnlyList<JsonObject> Records =>
        File.Exists(RecordPath)
            ? [.. File.ReadAllLines(RecordPath).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())]
            : [];
}
