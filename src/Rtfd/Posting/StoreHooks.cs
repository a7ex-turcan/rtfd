using Rtfd.Agents;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Providers;
using Rtfd.Runs;
using Rtfd.State;

namespace Rtfd.Posting;

/// <summary>
/// The state store's part in a live run: one review per head, attempts per commit (spec
/// "Policy"), a secret block that waits for <c>rtfd retry</c>, and the run's record with the
/// prompt's hash (spec "State store").
/// </summary>
public sealed class StoreHooks(StateStore store, RepoConfig repo, TimeProvider clock) : IRunHooks
{
    /// <summary>The run's id in the store, once the head passed the gate; the review's marker names it.</summary>
    public long? RunId { get; private set; }

    public string? Head { get; private set; }

    private int _number;

    public Task<RunGate> BeforeReviewAsync(PullRequest pullRequest, ReviewCommits commits, string promptHash, CancellationToken cancellationToken)
    {
        var head = commits.Head;
        var short_ = head[..8];
        if (store.LandedPosting(repo.Name, pullRequest.Number, head) is { } landed)
        {
            return Task.FromResult(new RunGate($"{short_} is already reviewed{(landed.ReviewUrl is { } url ? $" ({url})" : "")}. One review per head: a new push gets the next one", 0));
        }

        // Reviewed by a run whose posting the store lost, or a push that changed nothing of the author's.
        if (store.LastReviewed(repo.Name, pullRequest.Number)?.Head == head)
        {
            return Task.FromResult(new RunGate($"{short_} is already reviewed. One review per head: a new push gets the next one", 0));
        }

        var attempts = store.Attempts(repo.Name, pullRequest.Number, head);
        if (attempts.Blocked is { } reason)
        {
            return Task.FromResult(new RunGate($"{short_} is blocked: {reason}. Look at the run, then `rtfd retry {repo.Name} --pr {pullRequest.Number}` lifts the block", 0));
        }

        var left = repo.Settings.MaxAttempts - attempts.Used;
        if (left <= 0)
        {
            return Task.FromResult(new RunGate($"all {repo.Settings.MaxAttempts} attempts on {short_} are used up. `rtfd retry {repo.Name} --pr {pullRequest.Number}` gives them back", 0));
        }

        _number = pullRequest.Number;
        Head = head;
        RunId = store.StartRun(repo.Name, pullRequest.Number, head, promptHash, clock.GetUtcNow());
        return Task.FromResult(new RunGate(null, left));
    }

    public Task AttemptFinishedAsync(OutcomeKind kind, CancellationToken cancellationToken)
    {
        if (kind != OutcomeKind.Limit && Head is { } head)
        {
            store.CountAttempt(repo.Name, _number, head);
        }

        return Task.CompletedTask;
    }

    /// <summary>Records how the run ended; a secret from outside the diff blocks the head.</summary>
    public void Finish(RunReport report, string? outcomeOverride = null, string? messageOverride = null)
    {
        if (RunId is not { } id || Head is not { } head)
        {
            return;
        }

        var outcome = outcomeOverride ?? report.Outcome?.ToString().ToLowerInvariant() ?? "none";
        store.FinishRun(id, outcome, messageOverride ?? report.Message, clock.GetUtcNow());
        if (report.Outcome == OutcomeKind.Fatal && report.Plan?.Blocked == true)
        {
            store.Block(repo.Name, _number, head, $"a review of it held a secret that isn't in the diff (run {id})");
        }
    }
}
