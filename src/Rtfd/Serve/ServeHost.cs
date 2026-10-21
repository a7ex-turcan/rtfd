using System.Security.Cryptography;
using Rtfd.Config;
using Rtfd.State;

namespace Rtfd.Serve;

/// <summary>
/// <c>rtfd serve</c> once it holds the data directory's lock (plan Phase 5): checks each repo's
/// token and visibility once, then runs one loop per repo, in parallel, and a heartbeat that
/// rewrites <c>status.json</c>. The config is read once; an edit is noticed and logged, and a
/// restart applies it (the owner, 2026-10-15). Prompt files are read fresh for every review.
/// </summary>
public sealed class ServeHost : IDisposable
{
    private readonly ServeContext _context;
    private readonly DateTimeOffset _startedAt;
    private readonly string? _configHash;
    private bool _configChangeLogged;

    public ServeHost(ServeContext context)
    {
        _context = context;
        _startedAt = context.Clock.GetUtcNow();
        _configHash = HashOf(context.Config.FilePath);
        Watchers = [.. context.Config.Repos.Select(r => new RepoWatcher(context, r))];
    }

    public IReadOnlyList<RepoWatcher> Watchers { get; }

    /// <summary>How often <c>status.json</c> is rewritten while nothing else happens.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Waits between ticks; tests pass one that returns at once.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Whether rtfd.yaml changed since serve started.</summary>
    public bool ConfigChanged => HashOf(_context.Config.FilePath) != _configHash;

    /// <summary>The checks that need the provider, once: a repo that fails one isn't watched until a restart.</summary>
    public async Task StartAsync(CancellationToken stop)
    {
        var log = _context.Log;
        log.Write($"rtfd {EntryPoint.Version} serve started (pid {Environment.ProcessId}), config {_context.Config.FilePath}, data {_context.Config.Data.Root}.");
        var (reports, diagnostics) = await ProviderChecks.RunAsync(_context.Config, _context.Tokens, _context.Providers, stop).ConfigureAwait(false);
        foreach (var watcher in Watchers)
        {
            var repo = watcher.Repo;
            var report = reports.FirstOrDefault(r => r.Repo == repo);
            var problems = diagnostics.Where(d => d.IsError && d.Path.StartsWith(repo.Path + ".", StringComparison.Ordinal)).Select(d => d.Message).ToList();
            if (report?.Skipped is { } skipped && problems.Count == 0)
            {
                problems.Add($"{repo.Name}: {skipped}");
            }

            if (problems.Count > 0)
            {
                var reason = string.Join(" ", problems.Select(p => p.StartsWith(repo.Name + ": ", StringComparison.Ordinal) ? p[(repo.Name.Length + 2)..] : p)).TrimEnd('.');

                // A token the Azure CLI renews comes back once you log in again: the repo keeps trying.
                if (!_context.Tokens.For(repo).ReadOnce && diagnostics.All(d => !d.IsError || d.Check == "provider.token_refused" || !d.Path.StartsWith(repo.Path + ".", StringComparison.Ordinal)))
                {
                    log.Write($"{repo.Name}: waiting for a token: {reason}. Trying again every {RepoWatcher.Span(repo.Settings.Interval)}.");
                    continue;
                }

                watcher.StopUntilRestart($"{reason}. Fix it and restart serve");
                log.Write($"{repo.Name}: not watched: {reason}.");
                continue;
            }

            var settings = repo.Settings;
            log.Write($"{repo.Name}: watching {repo.Slug} as {report?.Identity} every {RepoWatcher.Span(settings.Interval)} with {repo.Agent} " +
                $"(quiet {RepoWatcher.Span(settings.QuietPeriod)}, CI wait up to {RepoWatcher.Span(settings.CiMaxWait)}).");
        }

        WriteStatus(stoppedAt: null);
    }

    /// <summary>Runs until <paramref name="stop"/>: then the current reviews finish or are abandoned, and the last status says serve stopped.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        await StartAsync(stop).ConfigureAwait(false);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var heartbeat = HeartbeatAsync(heartbeatStop.Token);
        await Task.WhenAll(Watchers.Select(w => LoopAsync(w, stop))).ConfigureAwait(false);
        await heartbeatStop.CancelAsync().ConfigureAwait(false);
        await heartbeat.ConfigureAwait(false);
        WriteStatus(stoppedAt: _context.Clock.GetUtcNow());
        _context.Log.Write("serve stopped.");
    }

    private async Task LoopAsync(RepoWatcher watcher, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            DateTimeOffset next;
            try
            {
                next = await watcher.TickAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bug in one tick mustn't end the watching: say so, and try again next interval.
                _context.Log.Write($"{watcher.Repo.Name}: the tick failed unexpectedly: {ex}");
                next = _context.Clock.GetUtcNow() + watcher.Repo.Settings.Interval;
            }

            WriteStatus(stoppedAt: null);
            var wait = next - _context.Clock.GetUtcNow();
            try
            {
                await Delay(wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HeartbeatAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Delay(Heartbeat, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_configChangeLogged && ConfigChanged)
            {
                _configChangeLogged = true;
                _context.Log.Write($"{_context.Config.FilePath} changed; restart serve to apply it. Prompt files need no restart.");
            }

            WriteStatus(stoppedAt: null);
        }
    }

    private readonly Lock _writeLock = new();

    public void WriteStatus(DateTimeOffset? stoppedAt)
    {
        var now = _context.Clock.GetUtcNow();
        var status = new ServeStatus(
            ServeStatus.CurrentVersion, Environment.ProcessId, EntryPoint.Version, _context.Config.FilePath,
            StateStore.Time(_startedAt), StateStore.Time(now), stoppedAt is { } at ? StateStore.Time(at) : null, ConfigChanged,
            [.. Watchers.Select(w => w.Status)],
            [.. _context.Store.AccountPauses().Where(p => p.Until > now).Select(p => new AccountStatus(p.Account, StateStore.Time(p.Until), p.Message))]);
        lock (_writeLock)
        {
            try
            {
                StatusFile.Write(_context.Config.Data.StatusPath, status);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _context.Log.Write($"status.json couldn't be written: {ex.Message}");
            }
        }
    }

    private static string? HashOf(string path)
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var watcher in Watchers)
        {
            watcher.Dispose();
        }
    }
}
