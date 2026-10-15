using System.Runtime.InteropServices;
using Rtfd.Config;
using Rtfd.Processes;
using Rtfd.Providers;
using Rtfd.Providers.GitHub;
using Rtfd.Serve;
using Rtfd.State;

namespace Rtfd.Cli;

/// <summary>What <c>serve</c> talks to beyond the config; tests pass fakes and stop it with <see cref="Stop"/>.</summary>
internal sealed record ServeServices(Func<RepoConfig, ProviderToken, IProvider> Providers, string RtfdExecutable, TimeProvider Clock)
{
    public CancellationToken Stop { get; init; }
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The real process: stop on Ctrl+C and termination signals, and the job object that takes the agent down with serve.</summary>
    public bool OwnsProcess { get; init; }

    public static ServeServices Real => new((repo, token) => new GitHubProvider(repo.Slug, token), Environment.ProcessPath ?? "rtfd", TimeProvider.System)
    {
        OwnsProcess = true,
    };
}

/// <summary>
/// <c>rtfd serve [--config &lt;path&gt;]</c>: one process watching every configured repo (plan
/// Phase 5). One per data directory; Ctrl+C or a termination signal stops it, letting a post in
/// flight finish and abandoning a review in progress.
/// </summary>
internal static class ServeCommand
{
    public static int Run(CommandContext ctx, string[] args, ServeServices? services = null)
    {
        var commandLine = new CommandLine(args);
        commandLine.Expect(0, "config");
        services ??= ServeServices.Real;
        if (LoadedConfig.Load(ctx, commandLine, "serve") is not { } config)
        {
            return EntryPoint.Failure;
        }

        using var instance = FileLock.TryAcquire(config.Data.ServeLockPath);
        if (instance is null)
        {
            var pid = StatusFile.Read(config.Data.StatusPath)?.Pid;
            ctx.Stderr.WriteLine($"rtfd serve: already running for {config.Data.Root}{(pid is { } p ? $" (pid {p})" : "")}. `rtfd status` shows what it's doing.");
            return EntryPoint.Failure;
        }

        var log = new ServeLog(config.Data.LogsDirectory, services.Clock, ctx.Stderr);
        if (services.OwnsProcess && ChildProcessJob.Enable() is { } problem)
        {
            log.Write($"warning: {problem}. If serve is killed, an agent it started may live on until it finishes.");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(services.Stop);
        var signals = services.OwnsProcess ? Register(stop, log) : [];
        try
        {
            using var store = StateStore.Open(config.Data.StatePath);
            using var host = new ServeHost(new ServeContext
            {
                Config = config,
                Store = store,
                Environment = ctx.Environment,
                Providers = services.Providers,
                RtfdExecutable = services.RtfdExecutable,
                Log = log,
                Clock = services.Clock,
            })
            {
                Heartbeat = services.Heartbeat,
                Delay = services.Delay ?? Task.Delay,
            };
            host.RunAsync(stop.Token).GetAwaiter().GetResult();
            return EntryPoint.Ok;
        }
        finally
        {
            foreach (var signal in signals)
            {
                signal.Dispose();
            }
        }
    }

    /// <summary>
    /// Ctrl+C, Ctrl+Break, closing the console and termination all stop serve the same way. On
    /// Windows closing the console gives a few seconds only; the write-ahead record covers a post
    /// it cuts short.
    /// </summary>
    private static List<PosixSignalRegistration> Register(CancellationTokenSource stop, ServeLog log)
    {
        void Handle(PosixSignalContext context)
        {
            context.Cancel = true;
            if (!stop.IsCancellationRequested)
            {
                log.Write($"stopping ({context.Signal}): a post in flight finishes, a review in progress is abandoned.");
                stop.Cancel();
            }
        }

        return
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Handle),
        ];
    }
}
