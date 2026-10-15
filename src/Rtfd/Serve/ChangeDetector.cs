using Rtfd.Config;
using Rtfd.Providers;
using Rtfd.Runs;

namespace Rtfd.Serve;

/// <summary>What the change detector hears about a PR: from a poll today, from a webhook one day (spec decision 1).</summary>
public abstract record PrEvent(int Number, DateTimeOffset At);

/// <summary>The PR is open with this head. A head the detector hadn't seen is a push, and the quiet period starts over.</summary>
public sealed record PrSeen(PullRequest PullRequest, DateTimeOffset At) : PrEvent(PullRequest.Number, At);

/// <summary>CI's state on one of the PR's heads.</summary>
public sealed record CiSeen(int Number, string Head, CiSummary Summary, DateTimeOffset At) : PrEvent(Number, At);

/// <summary>The PR was closed or merged.</summary>
public sealed record PrClosed(int Number, DateTimeOffset At) : PrEvent(Number, At);

public enum Readiness
{
    /// <summary>A draft, a fork, or an author outside the allowlist.</summary>
    Skipped,

    /// <summary>Pushed less than <c>quiet_period</c> ago.</summary>
    Quiet,

    /// <summary>Quiet long enough; CI's state on this head isn't known yet.</summary>
    NeedsCi,

    /// <summary>CI is still running, and <c>ci_max_wait</c> hasn't passed.</summary>
    WaitingForCi,

    Due,
}

/// <param name="Until">When the wait ends, for <see cref="Readiness.Quiet"/> and <see cref="Readiness.WaitingForCi"/>.</param>
/// <param name="CiUnfinished">Due because <c>ci_max_wait</c> passed with CI still running: the summary says so.</param>
public sealed record PrReadiness(Readiness State, string? Reason = null, DateTimeOffset? Until = null, bool CiUnfinished = false);

/// <summary>One open PR as the detector knows it.</summary>
public sealed class TrackedPr(PullRequest pullRequest, DateTimeOffset headSeenAt)
{
    public PullRequest PullRequest { get; internal set; } = pullRequest;

    /// <summary>When this head was first seen: the quiet period and the CI wait run from here.</summary>
    public DateTimeOffset HeadSeenAt { get; internal set; } = headSeenAt;

    /// <summary>CI on this head, once asked; a new head forgets it.</summary>
    public CiSummary? Ci { get; internal set; }

    public int Number => PullRequest.Number;
    public string Head => PullRequest.HeadSha;
}

/// <summary>
/// The change detector (spec "Change detector"), for one repo. It consumes events and says when
/// each open PR's head is due for a review: after <c>quiet_period</c> with no new push, so five
/// quick pushes make one review, and once CI has settled, or <c>ci_max_wait</c> has passed, or
/// there are no checks to wait for. Only a push resets the quiet period, and a CI result on a head
/// that is already reviewed triggers nothing (the owner, 2026-10-15). Drafts, forks and authors
/// outside the allowlist are skipped here. Whether a head is already reviewed is the state store's
/// to say, not the detector's.
/// </summary>
public sealed class ChangeDetector(RepoConfig repo)
{
    private readonly Dictionary<int, TrackedPr> _prs = [];

    public IReadOnlyList<TrackedPr> Open => [.. _prs.Values.OrderBy(p => p.Number)];

    public TrackedPr? Find(int number) => _prs.GetValueOrDefault(number);

    /// <summary>Applies one event; returns what changed, for the log, or null.</summary>
    public string? Apply(PrEvent e)
    {
        switch (e)
        {
            case PrSeen seen:
                var pr = seen.PullRequest;
                if (!_prs.TryGetValue(pr.Number, out var tracked))
                {
                    _prs[pr.Number] = new TrackedPr(pr, seen.At);
                    return $"#{pr.Number} by {pr.Author} at {Short(pr.HeadSha)}: {pr.Title}";
                }

                var wasDraft = tracked.PullRequest.IsDraft;
                if (tracked.Head != pr.HeadSha)
                {
                    tracked.PullRequest = pr;
                    tracked.HeadSeenAt = seen.At;
                    tracked.Ci = null;
                    return $"#{pr.Number} has a new head, {Short(pr.HeadSha)}";
                }

                tracked.PullRequest = pr;
                return wasDraft && !pr.IsDraft ? $"#{pr.Number} is ready for review" : !wasDraft && pr.IsDraft ? $"#{pr.Number} is back to a draft" : null;

            case CiSeen ci when _prs.TryGetValue(ci.Number, out var withCi) && withCi.Head == ci.Head:
                var before = withCi.Ci;
                withCi.Ci = ci.Summary;
                return before is { } was && was != ci.Summary ? $"#{ci.Number}: CI is {Name(ci.Summary)} on {Short(ci.Head)}" : null;

            case PrClosed closed when _prs.Remove(closed.Number):
                return $"#{closed.Number} was closed or merged";

            default:
                return null;
        }
    }

    /// <summary>A poll's answer, as events: every open PR is seen, and one no longer open is closed.</summary>
    public IReadOnlyList<string> Poll(IReadOnlyList<PullRequest> open, DateTimeOffset now)
    {
        var events = open.Select(pr => (PrEvent)new PrSeen(pr, now))
            .Concat(_prs.Keys.Except(open.Select(pr => pr.Number)).Select(n => new PrClosed(n, now)))
            .ToList();
        return [.. events.Select(Apply).OfType<string>()];
    }

    public PrReadiness Readiness(int number, DateTimeOffset now)
    {
        var tracked = _prs[number];
        var pr = tracked.PullRequest;
        if (pr.IsDraft)
        {
            return new(Serve.Readiness.Skipped, "it's a draft");
        }

        if (ReviewRun.Refusal(repo, pr) is { } refusal)
        {
            return new(Serve.Readiness.Skipped, refusal);
        }

        var quietEnd = tracked.HeadSeenAt + repo.Settings.QuietPeriod;
        if (now < quietEnd)
        {
            return new(Serve.Readiness.Quiet, Until: quietEnd);
        }

        if (tracked.Ci is not { } ci)
        {
            return new(Serve.Readiness.NeedsCi);
        }

        if (ci != CiSummary.Pending)
        {
            return new(Serve.Readiness.Due);
        }

        var ciEnd = tracked.HeadSeenAt + repo.Settings.CiMaxWait;
        return now < ciEnd ? new(Serve.Readiness.WaitingForCi, Until: ciEnd) : new(Serve.Readiness.Due, CiUnfinished: true);
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static string Name(CiSummary summary) => summary switch
    {
        CiSummary.None => "not configured",
        _ => summary.ToString().ToLowerInvariant(),
    };
}
