using System.Globalization;
using Rtfd.Config;
using Rtfd.Serve;
using Rtfd.State;

namespace Rtfd.Cli;

/// <summary>
/// <c>rtfd status [--config &lt;path&gt;]</c>: is serve watching, and what has it done (spec "CLI",
/// lesson 5). It reads <c>status.json</c> with the store. Running means serve holds the data
/// directory's lock and its heartbeat is younger than two intervals; holding the lock with an
/// older heartbeat is a hung serve. Exits 0 only when serve is running.
/// </summary>
internal static class StatusCommand
{
    private enum Liveness
    {
        Running,
        Starting,
        NotResponding,
        NotRunning,
    }

    public static int Run(CommandContext ctx, string[] args, TimeProvider? clock = null)
    {
        var commandLine = new CommandLine(args);
        commandLine.Expect(0, "config");
        if (LoadedConfig.Load(ctx, commandLine, "status", checks: false) is not { } config)
        {
            return EntryPoint.Failure;
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var output = ctx.Stdout;
        var held = FileLock.IsHeld(config.Data.ServeLockPath);
        var status = StatusFile.Read(config.Data.StatusPath);
        var heartbeat = status is null ? (DateTimeOffset?)null : StateStore.ParseTime(status.Heartbeat);
        var limit = 2 * (config.Repos.Count > 0 ? config.Repos.Min(r => r.Settings.Interval) : TimeSpan.FromMinutes(5));
        var liveness = (held, status, heartbeat) switch
        {
            (false, _, _) => Liveness.NotRunning,
            (true, null, _) or (true, { StoppedAt: not null }, _) => Liveness.Starting,
            (true, _, { } beat) when now - beat <= limit => Liveness.Running,
            _ => Liveness.NotResponding,
        };

        output.WriteLine(liveness switch
        {
            Liveness.Running => $"rtfd serve is running: pid {status!.Pid}, since {Local(StateStore.ParseTime(status.StartedAt))}, last heartbeat {Ago(now - heartbeat!.Value)}.",
            Liveness.Starting => "rtfd serve is starting: it holds the lock but hasn't written its status yet.",
            Liveness.NotResponding => $"rtfd serve is not responding: pid {status!.Pid} holds the lock, but its last heartbeat was {Ago(now - heartbeat!.Value)}, more than two intervals. It may be hung; stop it and start it again.",
            _ when status is { StoppedAt: { } stopped } => $"rtfd serve is not running. It stopped at {Local(StateStore.ParseTime(stopped))}.",
            _ when heartbeat is { } beat => $"rtfd serve is not running. It last wrote its status {Ago(now - beat)} and didn't stop cleanly: it was killed, or crashed.",
            _ => "rtfd serve is not running.",
        });

        if (status is { ConfigChanged: true } && liveness is Liveness.Running or Liveness.NotResponding)
        {
            output.WriteLine($"{config.FilePath} changed since serve started; a restart applies it.");
        }

        using var store = File.Exists(config.Data.StatePath) ? StateStore.Open(config.Data.StatePath) : null;
        var live = liveness is Liveness.Running or Liveness.NotResponding ? status : null;
        foreach (var repo in config.Repos)
        {
            output.WriteLine();
            var agent = config.Agents.GetValueOrDefault(repo.Agent);
            output.WriteLine($"{repo.Name}  {repo.Slug} · {repo.Agent}{(agent is null ? "" : $" ({agent.Account})")} · every {RepoWatcher.Span(repo.Settings.Interval)}");
            if (live?.Repos.FirstOrDefault(r => r.Name == repo.Name) is { } shown)
            {
                PrintLive(output, shown, now);
            }
            else
            {
                PrintStored(output, repo, store);
            }
        }

        var pauses = store?.AccountPauses().Where(p => p.Until > now).ToList() ?? [];
        if (pauses.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Accounts");
            foreach (var pause in pauses)
            {
                output.WriteLine($"  {pause.Account}: paused until {Local(pause.Until)} ({pause.Message}). It resumes on its own.");
            }
        }

        return liveness == Liveness.Running ? EntryPoint.Ok : EntryPoint.Failure;
    }

    private static void PrintLive(TextWriter output, RepoStatus repo, DateTimeOffset now)
    {
        var poll = repo.LastPoll is { } at ? $"; last poll {Ago(now - StateStore.ParseTime(at))}: {repo.LastPollResult}" : "; no poll yet";
        output.WriteLine(repo.State switch
        {
            "stopped" => $"  stopped: {repo.Reason}.",
            "paused" => $"  paused; `rtfd resume {repo.Name}` watches it again.",
            _ => $"  watching{poll}",
        });

        if (repo.Reviewing is { } reviewing)
        {
            var since = StateStore.ParseTime(reviewing.Since);
            output.WriteLine($"  reviewing #{reviewing.Number} at {Short(reviewing.Head)} since {Local(since)} ({Ago(now - since, "")})");
        }

        foreach (var pr in repo.Prs)
        {
            var detail = pr.State switch
            {
                "reviewed" when pr.LastReview is { } last => $"reviewed {Short(last.Head)} as {last.Event}, {Local(StateStore.ParseTime(last.At))}{(last.Url is { } url ? $"  {url}" : "")}",
                "reviewed" => $"reviewed {Short(pr.Head)}",
                "quiet" or "waiting_for_ci" or "backing_off" or "account_paused" when pr.Until is { } until =>
                    $"{Label(pr.State)} until {Local(StateStore.ParseTime(until))}{(pr.Detail is { } d ? $": {d}" : "")}",
                _ => $"{Label(pr.State)}{(pr.Detail is { } d ? $": {d}" : "")}",
            };
            var attempts = pr.AttemptsUsed > 0 && pr.State != "reviewed" ? $" · {pr.AttemptsUsed} of {pr.AttemptsUsed + pr.AttemptsLeft} attempts used" : "";
            output.WriteLine($"  #{pr.Number}  {Trim(pr.Title)} · {detail}{attempts}");
        }
    }

    /// <summary>What the store knows when serve isn't there to say: flags, the last reviews, blocks and attempts.</summary>
    private static void PrintStored(TextWriter output, RepoConfig repo, StateStore? store)
    {
        if (store is null)
        {
            output.WriteLine("  nothing reviewed yet");
            return;
        }

        var flags = store.Flags(repo.Name);
        if (flags.StoppedReason is { } reason)
        {
            output.WriteLine($"  stopped: {reason}. Fix it, then `rtfd resume {repo.Name}`.");
        }
        else if (flags.PausedAt is { } paused)
        {
            output.WriteLine($"  paused since {Local(paused)}; `rtfd resume {repo.Name}` watches it again.");
        }

        var reviews = store.LandedReviews(repo.Name);
        var attempts = store.AllAttempts(repo.Name);
        var runs = store.LastRuns(repo.Name);
        var numbers = reviews.Select(r => r.Pr).Concat(attempts.Select(a => a.Pr)).Concat(runs.Select(r => r.Pr)).Distinct().OrderDescending().Take(10).ToList();
        if (numbers.Count == 0)
        {
            output.WriteLine("  nothing reviewed yet");
        }

        foreach (var number in numbers)
        {
            var parts = new List<string>();
            var review = reviews.FirstOrDefault(r => r.Pr == number);
            if (review is not null)
            {
                parts.Add($"reviewed {Short(review.Head)} as {review.Event}, {Local(review.At)}{(review.Url is { } url ? $"  {url}" : "")}");
            }

            var run = runs.FirstOrDefault(r => r.Pr == number);
            if (run is { Outcome: not ("ok" or "unchanged") } && run.Head != review?.Head)
            {
                parts.Add($"last run at {Short(run.Head)}, {Local(run.StartedAt)}: {run.Outcome ?? "didn't finish"}{(run.Message is { } m ? $" ({m})" : "")}");
            }

            foreach (var head in attempts.Where(a => a.Pr == number))
            {
                parts.Add(head.Blocked is { } blocked
                    ? $"{Short(head.Head)} blocked: {blocked}; `rtfd retry {repo.Name} --pr {number}` lifts it"
                    : $"{head.Used} of {repo.Settings.MaxAttempts} attempts used on {Short(head.Head)}");
            }

            output.WriteLine($"  #{number}  {string.Join(" · ", parts)}");
        }
    }

    private static string Label(string state) => state switch
    {
        "waiting_for_ci" => "waiting for CI",
        "backing_off" => "backing off",
        "account_paused" => "due, account paused",
        "attempts_used_up" => "out of attempts",
        "nothing_to_review" => "not reviewed",
        _ => state,
    };

    private static string Trim(string title) => title.Length <= 50 ? title : title[..47] + "...";

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static string Ago(TimeSpan span, string suffix = " ago") => $"{RepoWatcher.Span(span)}{suffix}";

    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
