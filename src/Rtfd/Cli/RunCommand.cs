using Rtfd.Agents;
using Rtfd.Config;
using Rtfd.Posting;
using Rtfd.Providers;
using Rtfd.Providers.GitHub;
using Rtfd.Review;
using Rtfd.Runs;
using Rtfd.State;

namespace Rtfd.Cli;

/// <summary>What <c>run</c> talks to beyond the config; tests pass fakes.</summary>
internal sealed record RunServices(Func<RepoConfig, ProviderToken, IProvider> Providers, string RtfdExecutable, TimeProvider Clock, Func<TimeSpan, Task>? Delay = null)
{
    public static RunServices Real => new((repo, token) => new GitHubProvider(repo.Slug, token), Environment.ProcessPath ?? "rtfd", TimeProvider.System);
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

        var request = new RunRequest
        {
            Config = config,
            Repo = repo,
            Agent = config.Agents[repo.Agent],
            Number = number,
            InputsOnly = commandLine.Flag("inputs-only"),
            Keep = commandLine.Flag("keep"),
            Environment = ctx.Environment,
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
        var token = ctx.Environment(repo.TokenEnv);
        if (string.IsNullOrEmpty(token))
        {
            ctx.Stderr.WriteLine($"rtfd run: {repo.TokenEnv} isn't set. It should hold the token rtfd reads and posts to {repo.Slug} with.");
            return EntryPoint.Failure;
        }

        using var store = StateStore.Open(request.Config.Data.StatePath);
        var provider = services.Providers(repo, new ProviderToken(token));
        try
        {
            var poster = new Poster(store, provider, repo, services.Clock, ctx.Stderr);

            // What an earlier run left open settles first, so a crash never ends in a second review.
            var pr = await provider.GetPullRequestAsync(request.Number).ConfigureAwait(false);
            if (await poster.RecoverAsync(pr.Number, pr.HeadSha).ConfigureAwait(false) is { } recovered)
            {
                output.WriteLine($"PR #{pr.Number}  {pr.Title}");
                output.WriteLine();
                return PrintPosting(output, recovered, ReviewEventOf(store, repo, pr));
            }

            var hooks = new StoreHooks(store, repo, services.Clock);
            var history = new ReviewHistory(store, persist: true, services.Clock, ctx.Stderr);
            var report = await ReviewRun.RunAsync(request with { Hooks = hooks, History = history }).ConfigureAwait(false);
            DryRunPrinter.Print(report, request, output, verbose, posting: true);
            if (report.Unchanged is not null && report.Commits is { } same)
            {
                // Nothing of the author's changed: this head counts as reviewed, and the next follow-up starts here.
                store.SetLastReviewed(repo.Name, pr.Number, same.Head, same.MergeBase);
                hooks.Finish(report, "unchanged", report.Unchanged);
                return EntryPoint.Ok;
            }

            if (report.Refused is not null || report.Outcome != OutcomeKind.Ok || report.Plan is not { } plan || hooks.RunId is not { } runId)
            {
                hooks.Finish(report);
                return EntryPoint.Failure;
            }

            var posted = await poster.PostAsync(runId, report.PullRequest!, report.Commits!.Head, report.Commits.MergeBase, report.PostingIdentity!, plan).ConfigureAwait(false);
            if (posted.Status == PostStatus.Rejected)
            {
                // GitHub refusing a review means the validator let something through: count it invalid.
                hooks.Finish(report, "invalid", posted.Message);
            }
            else
            {
                hooks.Finish(report);
            }

            return PrintPosting(output, posted, ReviewBuilder.EventName(plan.Verdict.Event));
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

    private static string ReviewEventOf(StateStore store, RepoConfig repo, PullRequest pr) =>
        store.LandedPosting(repo.Name, pr.Number, pr.HeadSha)?.Event ?? "review";

    private static int PrintPosting(TextWriter output, PostOutcome outcome, string verdictEvent)
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
            output.WriteLine(outcome.Verified
                ? $"Read back from GitHub: the summary, the event and every inline comment{(outcome.Replies > 0 ? " and reply" : "")} are there as sent."
                : $"Read back from GitHub, with problems: {string.Join("; ", outcome.Problems)}");
        }

        return outcome.Verified ? EntryPoint.Ok : EntryPoint.Failure;
    }
}
