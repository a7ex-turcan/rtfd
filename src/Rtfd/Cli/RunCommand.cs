using Rtfd.Agents;
using Rtfd.Config;
using Rtfd.Posting;
using Rtfd.Providers;
using Rtfd.Runs;
using Rtfd.Serve;
using Rtfd.State;

namespace Rtfd.Cli;

/// <summary>What <c>run</c> talks to beyond the config; tests pass fakes.</summary>
/// <param name="LockWait">How long to wait for the repo's lock; 15 seconds, enough for a poll of serve's to let go.</param>
/// <param name="Tokens">Makes each repo's token source from the environment; null for the real ones (the variables and the Azure CLI).</param>
internal sealed record RunServices(
    Func<RepoConfig, TokenSource, IProvider> Providers, string RtfdExecutable, TimeProvider Clock, Func<TimeSpan, Task>? Delay = null, TimeSpan? LockWait = null,
    Func<Func<string, string?>, TokenSources>? Tokens = null)
{
    public static RunServices Real => new(ProviderFactory.Create, Environment.ProcessPath ?? "rtfd", TimeProvider.System);

    public TokenSources TokensFor(Func<string, string?> environment) => Tokens?.Invoke(environment) ?? TokenSources.Real(environment, Clock);
}

/// <summary>
/// <c>rtfd run &lt;repo&gt; --pr &lt;n&gt; [--dry-run] [--inputs-only] [--keep] [--verbose] [--config &lt;path&gt;]</c>:
/// one review now. With <c>--dry-run</c> it prints what it would post and records nothing.
/// Without it, it posts the review once, through the write-ahead record, and reads it back. On a
/// PR rtfd reviewed before, either way it is a follow-up: the interdiff, and rtfd's own threads.
/// </summary>
internal static class RunCommand
{
    public static int Run(CommandContext ctx, string[] args, RunServices? services = null)
    {
        var commandLine = new CommandLine(args, "dry-run", "inputs-only", "keep", "verbose");
        commandLine.Expect(1, "config", "pr", "dry-run", "inputs-only", "keep", "verbose");
        if (commandLine.Positionals.Count == 0)
        {
            throw new CommandLineException("which repo? usage: rtfd run <repo> --pr <n> [--dry-run]");
        }

        var number = commandLine.IntValue("pr") ?? throw new CommandLineException("'--pr <n>' is required.");
        var dryRun = commandLine.Flag("dry-run") || commandLine.Flag("inputs-only");
        services ??= RunServices.Real;

        var home = ctx.Home;
        var path = commandLine.Value("config") is { } given ? Path.GetFullPath(given) : home.ConfigPath;
        if (!File.Exists(path))
        {
            ctx.Stderr.WriteLine($"rtfd run: no config at {path}. Run `rtfd validate` for help setting one up.");
            return EntryPoint.Failure;
        }

        var loaded = ConfigLoader.LoadFile(path, home);
        var diagnostics = loaded.Config is null ? loaded.Diagnostics : [.. loaded.Diagnostics, .. ConfigChecks.Run(loaded.Config, ctx.Environment)];
        var errors = diagnostics.Where(d => d.IsError).ToList();
        if (loaded.Config is not { } config || errors.Count > 0)
        {
            ctx.Stderr.WriteLine($"rtfd run: the config has {errors.Count} error{(errors.Count == 1 ? "" : "s")}; `rtfd validate` lists them.");
            foreach (var error in errors)
            {
                ctx.Stderr.WriteLine($"  {error}");
            }

            return EntryPoint.Failure;
        }

        var name = commandLine.Positionals[0];
        var repo = config.Repos.FirstOrDefault(r => r.Name == name);
        if (repo is null)
        {
            ctx.Stderr.WriteLine($"rtfd run: no repo named '{name}' in {path}. It has: {string.Join(", ", config.Repos.Select(r => r.Name))}.");
            return EntryPoint.Failure;
        }

        // serve and other runs work on the same mirror and PRs: one at a time per repo (plan Phase 5).
        using var repoLock = FileLock.AcquireAsync(config.Data.RepoLockPath(repo.Name), services.LockWait ?? TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        if (repoLock is null)
        {
            ctx.Stderr.WriteLine($"rtfd run: another rtfd is working on {repo.Name} right now: serve reviewing one of its PRs, or another `rtfd run`. `rtfd status` shows which; try again once it's done.");
            return EntryPoint.Failure;
        }

        var request = new RunRequest
        {
            Config = config,
            Repo = repo,
            Agent = config.Agents[repo.Agent],
            Number = number,
            InputsOnly = commandLine.Flag("inputs-only"),
            Keep = commandLine.Flag("keep"),
            Environment = ctx.Environment,
            Tokens = services.TokensFor(ctx.Environment),
            Providers = services.Providers,
            RtfdExecutable = services.RtfdExecutable,
            Log = ctx.Stderr,
            Clock = services.Clock,
            Delay = services.Delay ?? (t => Task.Delay(t)),
        };

        if (dryRun)
        {
            // A dry run reads what rtfd knows, so the follow-up it prints is the one that would run,
            // and writes nothing: a PR the store doesn't know is rebuilt in memory only.
            using var store = File.Exists(config.Data.StatePath) ? StateStore.Open(config.Data.StatePath) : null;
            var report = ReviewRun.RunAsync(request with { History = new ReviewHistory(store, persist: false, services.Clock, ctx.Stderr) }).GetAwaiter().GetResult();
            DryRunPrinter.Print(report, request, ctx.Stdout, commandLine.Flag("verbose"));
            return report.Refused is null && (report.Outcome is OutcomeKind.Ok || report.Unchanged is not null || (request.InputsOnly && report.Outcome is null))
                ? EntryPoint.Ok
                : EntryPoint.Failure;
        }

        return LiveAsync(ctx, request, services, commandLine.Flag("verbose")).GetAwaiter().GetResult();
    }

    private static async Task<int> LiveAsync(CommandContext ctx, RunRequest request, RunServices services, bool verbose)
    {
        var (repo, output) = (request.Repo, ctx.Stdout);
        var source = request.Tokens.For(repo);
        try
        {
            await source.GetAsync().ConfigureAwait(false);
        }
        catch (ProviderException ex) when (ex.NoToken)
        {
            ctx.Stderr.WriteLine(repo.TokenEnv is not null
                ? $"rtfd run: {repo.TokenEnv} isn't set. It should hold the token rtfd reads and posts to {repo.Slug} with."
                : $"rtfd run: {ex.Message}");
            return EntryPoint.Failure;
        }

        using var store = StateStore.Open(request.Config.Data.StatePath);
        var provider = services.Providers(repo, source);
        try
        {
            var outcome = await LiveRun.RunAsync(request, store, provider, report => DryRunPrinter.Print(report, request, output, verbose, posting: true)).ConfigureAwait(false);
            if (outcome.Recovered is { } recovered)
            {
                output.WriteLine($"PR #{outcome.PullRequest.Number}  {outcome.PullRequest.Title}");
                output.WriteLine();
                return PrintPosting(output, recovered, outcome.Event!, ProviderNames.Of(repo.Provider), provider.Posting == ReviewPosting.PerThread);
            }

            if (outcome.Posted is { } posted)
            {
                return PrintPosting(output, posted, outcome.Event!, ProviderNames.Of(repo.Provider), provider.Posting == ReviewPosting.PerThread);
            }

            return outcome.Report?.Unchanged is not null ? EntryPoint.Ok : EntryPoint.Failure;
        }
        catch (ProviderException ex)
        {
            ctx.Stderr.WriteLine($"rtfd run: {ex.Message}");
            return EntryPoint.Failure;
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    private static int PrintPosting(TextWriter output, PostOutcome outcome, string verdictEvent, string provider, bool perThread)
    {
        var link = outcome.Url is { } url ? $" {url}" : "";
        var comments = $"{outcome.Comments} inline comment{(outcome.Comments == 1 ? "" : "s")}";
        if (outcome.Replies > 0)
        {
            comments += $", and {outcome.Replies} repl{(outcome.Replies == 1 ? "y" : "ies")} on earlier threads ({outcome.Resolved} resolved)";
        }

        output.WriteLine(outcome.Status switch
        {
            PostStatus.Posted => $"Posted the review as {verdictEvent}, with {comments}.{link}",
            PostStatus.Reposted => $"An earlier run's review never landed, so what it recorded was posted now, as {verdictEvent}, with {comments}.{link}",
            PostStatus.Recovered => $"The review an earlier run posted is on the PR; nothing was posted again.{link}",
            PostStatus.Unknown => $"The post failed, and the review may still land: {outcome.Message}",
            PostStatus.Rejected => $"Not posted: {outcome.Message}. The run counts as invalid; this is a validator bug worth reporting.",
            _ => $"Not posted: {outcome.Message}",
        });

        if (outcome.Landed)
        {
            // Where the vote is apart from the review, a comment has none to read back.
            var verdict = !perThread ? ", the event" : verdictEvent == "COMMENT" ? "" : ", the vote";
            output.WriteLine(outcome.Verified
                ? $"Read back from {provider}: the summary{verdict} and every inline comment{(outcome.Replies > 0 ? " and reply" : "")} are there as sent."
                : $"Read back from {provider}, with problems: {string.Join("; ", outcome.Problems)}");
        }

        return outcome.Verified ? EntryPoint.Ok : EntryPoint.Failure;
    }
}
