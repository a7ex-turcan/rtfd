using System.Globalization;
using Rtfd.Agents;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Posting;
using Rtfd.Providers;
using Rtfd.Runs;
using Rtfd.State;

namespace Rtfd.Serve;

/// <summary>What serve's parts share: the config, the store, the outside world, the clock and the log.</summary>
public sealed record ServeContext
{
    public required RtfdConfig Config { get; init; }
    public required StateStore Store { get; init; }
    public required Func<string, string?> Environment { get; init; }
    public required Func<RepoConfig, ProviderToken, IProvider> Providers { get; init; }

    /// <summary>The rtfd binary the agent's <c>mcp.json</c> starts as <c>rtfd-git</c>.</summary>
    public required string RtfdExecutable { get; init; }

    public required ServeLog Log { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>The wait before a run retries an agent's transient failure; tests pass one that doesn't.</summary>
    public Func<TimeSpan, CancellationToken, Task> RunDelay { get; init; } = Task.Delay;
}

/// <summary>
/// One repo under <c>serve</c> (plan Phase 5): each tick reads the repo's flags, takes its lock,
/// polls the open PRs, lets the change detector say which head is due, checks the store for what
/// is reviewed, blocked or out of attempts, and reviews one due PR through <see cref="LiveRun"/>.
/// Ticks are serial, so one repo never runs two reviews at once; repos tick in parallel.
/// </summary>
public sealed class RepoWatcher : IDisposable
{
    /// <summary>How long a PR that keeps failing waits at most before the next try.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly ServeContext _context;
    private readonly ChangeDetector _detector;
    private readonly Dictionary<int, BackoffState> _backoff = [];
    private readonly Dictionary<int, (string Head, string Reason)> _done = [];
    private readonly Dictionary<int, string> _skipLogged = [];
    private readonly Lock _statusLock = new();
    private IProvider? _poller;
    private bool _swept;
    private int _pollFailures;
    private string? _stopped;
    private string? _lastPoll;
    private string? _lastPollResult;
    private ReviewingStatus? _reviewing;
    private RepoStatus _status;

    public RepoWatcher(ServeContext context, RepoConfig repo)
    {
        _context = context;
        Repo = repo;
        _detector = new ChangeDetector(repo);
        _status = Snapshot("watching", null, [], null);
    }

    public RepoConfig Repo { get; }

    public AgentConfig Agent => _context.Config.Agents[Repo.Agent];

    public ChangeDetector Detector => _detector;

    /// <summary>The repo as <c>status.json</c> shows it, as of the last tick.</summary>
    public RepoStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    private DateTimeOffset Now => _context.Clock.GetUtcNow();

    private ServeLog Log => _context.Log;

    /// <summary>
    /// Leaves the repo unwatched until serve restarts: its token is missing or refused, or its
    /// config contradicts what the provider says. Those are fixed outside rtfd and read at start.
    /// </summary>
    public void StopUntilRestart(string reason)
    {
        _stopped = reason;
        Publish("stopped", reason, [], null);
    }

    /// <summary>One poll and at most one review. Returns when this repo wants its next tick.</summary>
    public async Task<DateTimeOffset> TickAsync(CancellationToken stop)
    {
        var now = Now;
        var interval = Repo.Settings.Interval;
        if (_stopped is { } stoppedReason)
        {
            Log.Write($"{Repo.Name}: not watched until serve restarts: {stoppedReason}.");
            Publish("stopped", stoppedReason, [], now + interval);
            return now + interval;
        }

        var flags = _context.Store.Flags(Repo.Name);
        if (flags.StoppedReason is { } reason)
        {
            var why = $"{reason}. Fix it, then `rtfd resume {Repo.Name}`";
            Log.Write($"{Repo.Name}: stopped: {why}.");
            Publish("stopped", why, [], now + interval);
            return now + interval;
        }

        if (flags.PausedAt is not null)
        {
            Log.Write($"{Repo.Name}: paused; `rtfd resume {Repo.Name}` watches it again.");
            Publish("paused", null, [], now + interval);
            return now + interval;
        }

        using var held = FileLock.TryAcquire(_context.Config.Data.RepoLockPath(Repo.Name));
        if (held is null)
        {
            var retry = interval < TimeSpan.FromMinutes(1) ? interval : TimeSpan.FromMinutes(1);
            Log.Write($"{Repo.Name}: an `rtfd run` is working on it; polling again in {Span(retry)}.");
            return now + retry;
        }

        if (!_swept)
        {
            await SweepAsync().ConfigureAwait(false);
            _swept = true;
        }

        IReadOnlyList<PullRequest> open;
        try
        {
            _poller ??= _context.Providers(Repo, new ProviderToken(_context.Environment(Repo.TokenEnv) ?? ""));
            open = await _poller.ListOpenPullRequestsAsync(stop).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (ex.IsAuth)
        {
            StopUntilRestart($"GitHub refused the token in {Repo.TokenEnv}: {ex.Message} Put a working one there and restart serve");
            Log.Write($"{Repo.Name}: stopped: {_stopped}.");
            return now + interval;
        }
        catch (ProviderException ex)
        {
            _pollFailures++;
            var wait = Backoff(interval, _pollFailures);
            _lastPoll = StateStore.Time(now);
            _lastPollResult = $"the poll failed: {ex.Message}";
            Log.Write($"{Repo.Name}: the poll failed: {ex.Message} Trying again in {Span(wait)}.");
            Publish("watching", null, [], now + wait);
            return now + wait;
        }

        _pollFailures = 0;
        now = Now;
        foreach (var note in _detector.Poll(open, now))
        {
            Log.Write($"{Repo.Name}: {note}");
        }

        var numbers = open.Select(p => p.Number).ToHashSet();
        foreach (var gone in _backoff.Keys.Concat(_done.Keys).Concat(_skipLogged.Keys).Where(n => !numbers.Contains(n)).ToList())
        {
            _backoff.Remove(gone);
            _done.Remove(gone);
            _skipLogged.Remove(gone);
        }

        var paused = _context.Store.AccountPaused(Agent.Account, now);
        var states = new List<(TrackedPr Pr, PrState State)>();
        foreach (var pr in _detector.Open)
        {
            states.Add((pr, await StateOfAsync(pr, now, paused, stop).ConfigureAwait(false)));
        }

        var due = states.FirstOrDefault(s => s.State.Due).Pr;
        var wake = now + interval;
        foreach (var until in states.Select(s => s.State.Until).OfType<DateTimeOffset>().Where(u => u > now && u < wake))
        {
            wake = until;
        }

        _lastPoll = StateStore.Time(now);
        _lastPollResult = PollLine(open.Count, states.Select(s => s.State).ToList(), due is not null);
        Log.Write($"{Repo.Name}: {_lastPollResult}");
        Publish("watching", null, states, due is null ? wake : now);

        if (due is null || stop.IsCancellationRequested)
        {
            return wake;
        }

        await ReviewAsync(due, stop).ConfigureAwait(false);

        // Straight on to the next tick: the PRs may have moved during the review.
        return Now;
    }

    private sealed record PrState(string State, string Short, string? Detail = null, DateTimeOffset? Until = null, bool Due = false);

    private sealed record BackoffState(string Head, int Failures, DateTimeOffset Until, string Reason);

    private async Task<PrState> StateOfAsync(TrackedPr pr, DateTimeOffset now, AccountPause? paused, CancellationToken stop)
    {
        var (n, head, store) = (pr.Number, pr.Head, _context.Store);
        var readiness = _detector.Readiness(n, now);
        if (readiness.State == Readiness.Skipped)
        {
            if (_skipLogged.GetValueOrDefault(n) != readiness.Reason)
            {
                _skipLogged[n] = readiness.Reason!;
                Log.Write($"{Repo.Name} #{n}: skipped: {readiness.Reason}.");
            }

            return new("skipped", $"#{n} skipped", readiness.Reason);
        }

        _skipLogged.Remove(n);
        if (store.LandedPosting(Repo.Name, n, head) is not null || store.LastReviewed(Repo.Name, n)?.Head == head)
        {
            return new("reviewed", $"#{n} reviewed");
        }

        if (_done.TryGetValue(n, out var done) && done.Head == head)
        {
            return new("nothing_to_review", $"#{n} not reviewed", done.Reason);
        }

        var attempts = store.Attempts(Repo.Name, n, head);
        if (attempts.Blocked is { } blocked)
        {
            return new("blocked", $"#{n} blocked", $"{blocked}. Look at it, then `rtfd retry {Repo.Name} --pr {n}`");
        }

        if (attempts.Used >= Repo.Settings.MaxAttempts)
        {
            return new("attempts_used_up", $"#{n} out of attempts",
                $"all {Repo.Settings.MaxAttempts} attempts on {Short(head)} are used up; `rtfd retry {Repo.Name} --pr {n}` gives them back");
        }

        if (_backoff.TryGetValue(n, out var backoff) && backoff.Head == head && backoff.Until > now)
        {
            return new("backing_off", $"#{n} trying again in {Span(backoff.Until - now)}", backoff.Reason, backoff.Until);
        }

        // A posting an earlier run left open settles first, whatever the quiet period says.
        var recovering = store.OpenPostings(Repo.Name, n).Count > 0;
        // CI is asked again on every tick until it settles; a conditional request makes that cheap.
        if (!recovering && readiness.State is Readiness.NeedsCi or Readiness.WaitingForCi)
        {
            try
            {
                var ci = await _poller!.GetCiStateAsync(head, stop).ConfigureAwait(false);
                if (_detector.Apply(new CiSeen(n, head, ci.Summary, now)) is { } note)
                {
                    Log.Write($"{Repo.Name}: {note}");
                }

                readiness = _detector.Readiness(n, now);
            }
            catch (ProviderException ex)
            {
                return new("waiting_for_ci", $"#{n} waiting for CI", $"CI couldn't be read: {ex.Message}", now + Repo.Settings.Interval);
            }
        }

        if (!recovering && readiness is { State: Readiness.Quiet, Until: { } quietEnd })
        {
            return new("quiet", $"#{n} quiet for {Span(quietEnd - now)} more",
                $"head seen {Span(now - pr.HeadSeenAt)} ago; reviewed once nothing new is pushed for {Span(Repo.Settings.QuietPeriod)}", quietEnd);
        }

        if (!recovering && readiness is { State: Readiness.WaitingForCi, Until: { } ciEnd })
        {
            return new("waiting_for_ci", $"#{n} waiting for CI, up to {Span(ciEnd - now)} more", "CI is still running", ciEnd);
        }

        if (paused is not null)
        {
            return new("account_paused", $"#{n} due, but {paused.Account} is paused until {Local(paused.Until)}", paused.Message, paused.Until);
        }

        var why = recovering ? " (an earlier post to settle)" : readiness.CiUnfinished ? " (CI still running after ci_max_wait)" : "";
        return new("due", $"#{n} due{why}", Due: true);
    }

    private async Task ReviewAsync(TrackedPr pr, CancellationToken stop)
    {
        var name = $"{Repo.Name} #{pr.Number}";
        lock (_statusLock)
        {
            _reviewing = new ReviewingStatus(pr.Number, pr.Head, StateStore.Time(Now));
            _status = _status with { Reviewing = _reviewing };
        }

        Log.Write($"{name}: reviewing {Short(pr.Head)}.");
        IProvider? provider = null;
        try
        {
            provider = _context.Providers(Repo, new ProviderToken(_context.Environment(Repo.TokenEnv) ?? ""));
            var request = new RunRequest
            {
                Config = _context.Config,
                Repo = Repo,
                Agent = Agent,
                Number = pr.Number,
                Environment = _context.Environment,
                Providers = _context.Providers,
                RtfdExecutable = _context.RtfdExecutable,
                Log = Log.For($"{name}: "),
                Clock = _context.Clock,
                Zone = _context.Zone,
                Delay = t => _context.RunDelay(t, stop),
            };
            var outcome = await LiveRun.RunAsync(request, _context.Store, provider, cancellationToken: stop).ConfigureAwait(false);
            Settle(pr, outcome);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            Log.Write($"{name}: review abandoned: serve is stopping. The next start reviews it again.");
        }
        catch (ProviderException ex) when (ex.IsAuth)
        {
            StopUntilRestart($"GitHub refused the token in {Repo.TokenEnv}: {ex.Message} Put a working one there and restart serve");
            Log.Write($"{Repo.Name}: stopped: {_stopped}.");
        }
        catch (ProviderException ex)
        {
            BackOff(pr, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A bug or a surprise from git or the disk: the PR waits, serve goes on.
            BackOff(pr, $"the review failed unexpectedly: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            lock (_statusLock)
            {
                _reviewing = null;
                _status = _status with { Reviewing = null };
            }
        }
    }

    /// <summary>What a live run's outcome means for this PR, and its line in the log.</summary>
    private void Settle(TrackedPr pr, LiveOutcome outcome)
    {
        var name = $"{Repo.Name} #{pr.Number}";
        if (outcome.Recovered is { } recovered)
        {
            Log.Write($"{name}: {Describe(recovered, outcome.Event)}");
            if (recovered.Status is PostStatus.Unknown or PostStatus.Failed)
            {
                BackOff(pr, "the post may not have landed");
            }

            return;
        }

        var report = outcome.Report!;
        if (outcome.Paused is { } paused)
        {
            Log.Write($"{Repo.Name}: {paused.Account} hit a usage limit ({paused.Message}). Every repo on it waits until {Local(paused.Until)}; the attempt doesn't count.");
            return;
        }

        if (report.Unchanged is { } unchanged)
        {
            Log.Write($"{name}: nothing to review: {unchanged}. The head counts as reviewed.");
            return;
        }

        if (report.Refused is { } refused)
        {
            if (report.RefusedForHead)
            {
                _done[pr.Number] = (report.Commits?.Head ?? pr.Head, refused);
                Log.Write($"{name}: not reviewed: {refused}.");
            }
            else
            {
                BackOff(pr, refused);
            }

            return;
        }

        if (outcome.Posted is { } posted)
        {
            Log.Write($"{name}: {Describe(posted, outcome.Event)}");
            if (posted.Status is PostStatus.Unknown or PostStatus.Failed)
            {
                BackOff(pr, "the post didn't go through");
            }
            else
            {
                _backoff.Remove(pr.Number);
            }

            return;
        }

        switch (report.Outcome)
        {
            case OutcomeKind.Fatal when report.Plan?.Blocked == true:
                Log.Write($"{name}: blocked: {report.Message}. Look at it, then `rtfd retry {Repo.Name} --pr {pr.Number}`.");
                break;
            case OutcomeKind.Fatal:
                _context.Store.Stop(Repo.Name, report.Message ?? "the agent failed in a way a retry won't fix", Now);
                Log.Write($"{Repo.Name}: stopped: {report.Message}. Fix it, then `rtfd resume {Repo.Name}`.");
                break;
            case OutcomeKind.Transient:
                BackOff(pr, report.Message ?? "the review failed");
                break;
            default:
                Log.Write($"{name}: {report.Message ?? report.Outcome?.ToString().ToLowerInvariant() ?? "no outcome"}.");
                break;
        }
    }

    private void BackOff(TrackedPr pr, string? reason)
    {
        var failures = _backoff.TryGetValue(pr.Number, out var earlier) && earlier.Head == pr.Head ? earlier.Failures + 1 : 1;
        var wait = Backoff(Repo.Settings.Interval, failures);
        reason = reason?.TrimEnd('.') ?? "it failed";
        _backoff[pr.Number] = new BackoffState(pr.Head, failures, Now + wait, reason);
        Log.Write($"{Repo.Name} #{pr.Number}: {reason}. Trying again in {Span(wait)}.");
    }

    /// <summary>Doubles from the interval with each failure in a row, up to <see cref="MaxBackoff"/>.</summary>
    private static TimeSpan Backoff(TimeSpan interval, int failures)
    {
        var wait = interval * Math.Pow(2, Math.Min(failures - 1, 10));
        return wait > MaxBackoff ? (interval > MaxBackoff ? interval : MaxBackoff) : wait;
    }

    /// <summary>
    /// Deletes the run folders and worktrees a killed run of this repo left (plan Phase 5), except
    /// ones <c>--keep</c> kept on purpose, drops the mirror's record of them, and marks the runs
    /// that never finished as abandoned. Runs under the repo's lock, so no live run of this repo
    /// is using any of them.
    /// </summary>
    private async Task SweepAsync()
    {
        var data = _context.Config.Data;
        var swept = 0;
        foreach (var folder in new[] { data.RunsDirectory, data.WorktreesDirectory }.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateDirectories(folder).ToList())
            {
                var name = Path.GetFileName(path);
                if (RunFolder.RepoOf(name) != Repo.Name || File.Exists(Path.Combine(data.RunsDirectory, name, RunFolder.KeptMarker)))
                {
                    continue;
                }

                try
                {
                    await Folders.DeleteAsync(path).ConfigureAwait(false);
                    swept++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Write($"{Repo.Name}: couldn't delete {path}, which a stopped run left: {ex.Message} The next start tries again.");
                }
            }
        }

        var tokens = _context.Config.Repos.Select(r => _context.Environment(r.TokenEnv)).OfType<string>().Where(t => t.Length > 0).Distinct().ToList();
        var git = new GitRunner(data.GitDirectory, _context.Environment, tokens);
        var mirror = Mirror.For(data, Repo.Name, git);
        if (File.Exists(Path.Combine(mirror.Path, "HEAD")))
        {
            await git.RunAsync(mirror.Path, ["worktree", "prune"]).ConfigureAwait(false);
        }

        if (swept > 0)
        {
            Log.Write($"{Repo.Name}: deleted {swept} folder{(swept == 1 ? "" : "s")} a stopped run left behind.");
        }

        if (_context.Store.AbandonUnfinishedRuns(Repo.Name, Now) is > 0 and var abandoned)
        {
            Log.Write($"{Repo.Name}: marked {abandoned} run{(abandoned == 1 ? "" : "s")} a stopped serve or `rtfd run` never finished as abandoned.");
        }
    }

    /// <summary>The poll's line in the log: so a quiet log means a dead loop (spec lesson 5).</summary>
    private static string PollLine(int count, IReadOnlyList<PrState> states, bool reviewing)
    {
        var prs = count == 0 ? "no open PRs" : $"{count} open PR{(count == 1 ? "" : "s")}";
        if (count == 0)
        {
            return prs;
        }

        var waiting = states.Any(s => s.State is not ("reviewed" or "skipped" or "nothing_to_review"));
        var parts = string.Join("; ", states.Select(s => s.Short));
        return reviewing ? $"{prs}: {parts}; reviewing now"
            : waiting ? $"{prs}: {parts}"
            : $"{prs}, nothing to do ({parts})";
    }

    private void Publish(string state, string? reason, IReadOnlyList<(TrackedPr Pr, PrState State)> states, DateTimeOffset? next)
    {
        var store = _context.Store;
        var landed = store.LandedReviews(Repo.Name).ToDictionary(r => r.Pr);
        var prs = states.Select(s =>
        {
            var attempts = store.Attempts(Repo.Name, s.Pr.Number, s.Pr.Head);
            var last = landed.GetValueOrDefault(s.Pr.Number);
            return new PrStatus(
                s.Pr.Number, s.Pr.PullRequest.Title, s.Pr.PullRequest.Author, s.Pr.Head, s.State.State, s.State.Detail,
                s.State.Until is { } until ? StateStore.Time(until) : null,
                attempts.Used, Math.Max(0, Repo.Settings.MaxAttempts - attempts.Used),
                last is null ? null : new LastReviewStatus(last.Head, last.Event, last.Url, StateStore.Time(last.At)));
        }).ToList();

        lock (_statusLock)
        {
            // A paused or stopped tick doesn't poll, so it keeps the PRs the last poll saw.
            _status = Snapshot(state, reason, states.Count > 0 || state == "watching" ? prs : _status.Prs, next);
        }
    }

    private RepoStatus Snapshot(string state, string? reason, List<PrStatus> prs, DateTimeOffset? next) => new(
        Repo.Name, Repo.Slug, Repo.Agent, Agent.Account, (int)Repo.Settings.Interval.TotalSeconds, state, reason,
        _lastPoll, _lastPollResult, next is { } n ? StateStore.Time(n) : null, _reviewing, prs);

    private static string Describe(PostOutcome outcome, string? verdictEvent)
    {
        var link = outcome.Url is { } url ? $": {url}" : "";
        var comments = $"{outcome.Comments} inline comment{(outcome.Comments == 1 ? "" : "s")}";
        if (outcome.Replies > 0)
        {
            comments += $" and {outcome.Replies} repl{(outcome.Replies == 1 ? "y" : "ies")} on earlier threads ({outcome.Resolved} resolved)";
        }

        var problems = outcome.Landed && !outcome.Verified ? $" Read back with problems: {string.Join("; ", outcome.Problems)}." : "";
        return outcome.Status switch
        {
            PostStatus.Posted => $"posted the review as {verdictEvent}, with {comments}{link}{problems}",
            PostStatus.Reposted => $"an earlier run's review never landed, so what it recorded went out now, as {verdictEvent}, with {comments}{link}{problems}",
            PostStatus.Recovered => $"the review an earlier run posted is on the PR; nothing posted again{link}{problems}",
            PostStatus.Unknown => $"the post failed and may still land: {outcome.Message}",
            PostStatus.Rejected => $"GitHub refused the review: {outcome.Message} It counts as invalid; this is a validator bug worth reporting.",
            _ => $"the post failed: {outcome.Message}",
        };
    }

    private string Local(DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, _context.Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>"45s", "3m", "1h 20m".</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds < 60 ? $"{Math.Ceiling(span.TotalSeconds):0}s"
            : span.TotalMinutes < 90 ? $"{Math.Round(span.TotalMinutes):0}m"
            : span.TotalHours < 48 ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalDays}d {span.Hours}h";
    }

    public void Dispose() => (_poller as IDisposable)?.Dispose();
}
