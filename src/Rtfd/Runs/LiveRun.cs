using Rtfd.Agents;
using Rtfd.Posting;
using Rtfd.Providers;
using Rtfd.Review;
using Rtfd.State;

namespace Rtfd.Runs;

/// <summary>How a live run went: an earlier posting settled, or a review run and maybe posted.</summary>
public sealed record LiveOutcome
{
    public required PullRequest PullRequest { get; init; }

    /// <summary>An earlier run's posting for this head, settled before anything else ran. Nothing else did.</summary>
    public PostOutcome? Recovered { get; init; }

    /// <summary>The review's event, for what the caller prints: COMMENT, APPROVE or REQUEST_CHANGES.</summary>
    public string? Event { get; init; }

    public RunReport? Report { get; init; }
    public PostOutcome? Posted { get; init; }

    /// <summary>The usage limit the run hit, now recorded on the agent's account.</summary>
    public AccountPause? Paused { get; init; }
}

/// <summary>
/// One live review, as <c>rtfd run</c> does it and <c>serve</c> does it on every due head (plan
/// Phases 3 to 5): settle what an earlier run left open, review through the state store's gate,
/// post through the write-ahead record, and record how it ended. A usage limit pauses the agent's
/// account for every repo on it, whoever hit it.
/// </summary>
/// <remarks>
/// Posting never takes the caller's cancellation: once a review is decided it goes out, and a
/// stop request waits for it. A kill mid-post is what the write-ahead record is for.
/// </remarks>
public static class LiveRun
{
    public static async Task<LiveOutcome> RunAsync(
        RunRequest request, StateStore store, IProvider provider, Action<RunReport>? reviewed = null, CancellationToken cancellationToken = default)
    {
        var (repo, clock, log) = (request.Repo, request.Clock, request.Log);
        var poster = new Poster(store, provider, repo, clock, log);

        // What an earlier run left open settles first, so a crash never ends in a second review.
        var pr = await provider.GetPullRequestAsync(request.Number, cancellationToken).ConfigureAwait(false);
        if (await poster.RecoverAsync(pr.Number, pr.HeadSha, CancellationToken.None).ConfigureAwait(false) is { } recovered)
        {
            return new LiveOutcome { PullRequest = pr, Recovered = recovered, Event = store.LandedPosting(repo.Name, pr.Number, pr.HeadSha)?.Event ?? "review" };
        }

        var hooks = new StoreHooks(store, repo, clock);
        var history = new ReviewHistory(store, persist: true, clock, log);
        RunReport report;
        try
        {
            report = await ReviewRun.RunAsync(request with { Hooks = hooks, History = history }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped mid-review: the agent's process tree is gone, and the attempt didn't count.
            hooks.Finish(new RunReport(), "abandoned", "rtfd stopped during the review");
            throw;
        }

        reviewed?.Invoke(report);
        pr = report.PullRequest ?? pr;

        AccountPause? paused = null;
        if (report.Outcome == OutcomeKind.Limit && report.Limit is { } hit)
        {
            store.PauseAccount(request.Agent.Account, hit.ResetAt, hit.Message);
            paused = new AccountPause(request.Agent.Account, hit.ResetAt, hit.Message);
        }

        if (report.Unchanged is not null && report.Commits is { } same)
        {
            // Nothing of the author's changed: this head counts as reviewed, and the next follow-up starts here.
            store.SetLastReviewed(repo.Name, pr.Number, same.Head, same.MergeBase);
            hooks.Finish(report, "unchanged", report.Unchanged);
            return new LiveOutcome { PullRequest = pr, Report = report };
        }

        if (report.Refused is not null || report.Outcome != OutcomeKind.Ok || report.Plan is not { } plan || hooks.RunId is not { } runId)
        {
            hooks.Finish(report);
            return new LiveOutcome { PullRequest = pr, Report = report, Paused = paused };
        }

        var posted = await poster.PostAsync(runId, pr, report.Commits!.Head, report.Commits.MergeBase, report.PostingIdentity!, plan, CancellationToken.None).ConfigureAwait(false);
        if (posted.Status == PostStatus.Rejected)
        {
            // GitHub refusing a review means the validator let something through: count it invalid.
            hooks.Finish(report, "invalid", posted.Message);
        }
        else
        {
            hooks.Finish(report);
        }

        return new LiveOutcome { PullRequest = pr, Report = report, Posted = posted, Event = ReviewBuilder.EventName(plan.Verdict.Event) };
    }
}
