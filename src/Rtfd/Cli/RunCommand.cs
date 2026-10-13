using Rtfd.Agents;
using Rtfd.Config;
using Rtfd.Providers;
using Rtfd.Providers.GitHub;
using Rtfd.Runs;

namespace Rtfd.Cli;

/// <summary>
/// <c>rtfd run &lt;repo&gt; --pr &lt;n&gt; --dry-run [--inputs-only] [--keep] [--verbose] [--config &lt;path&gt;]</c>:
/// one review now. Posting arrives in Phase 3, so for now <c>--dry-run</c> is required, and the
/// command prints what it would post.
/// </summary>
internal static class RunCommand
{
    public static int Run(CommandContext ctx, string[] args, Func<RepoConfig, ProviderToken, IProvider>? providers = null)
    {
        var commandLine = new CommandLine(args, "dry-run", "inputs-only", "keep", "verbose");
        commandLine.Expect(1, "config", "pr", "dry-run", "inputs-only", "keep", "verbose");
        if (commandLine.Positionals.Count == 0)
        {
            throw new CommandLineException("which repo? usage: rtfd run <repo> --pr <n> --dry-run");
        }

        var number = commandLine.IntValue("pr") ?? throw new CommandLineException("'--pr <n>' is required.");
        if (!commandLine.Flag("dry-run"))
        {
            ctx.Stderr.WriteLine("rtfd run: posting arrives in Phase 3 of the plan. Pass --dry-run to see what would post.");
            return EntryPoint.Usage;
        }

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
            Providers = providers ?? ((r, token) => new GitHubProvider(r.Slug, token)),
            RtfdExecutable = Environment.ProcessPath ?? "rtfd",
            Log = ctx.Stderr,
        };

        var report = ReviewRun.RunAsync(request).GetAwaiter().GetResult();
        DryRunPrinter.Print(report, request, ctx.Stdout, commandLine.Flag("verbose"));
        return report.Refused is null && (report.Outcome is OutcomeKind.Ok || (request.InputsOnly && report.Outcome is null))
            ? EntryPoint.Ok
            : EntryPoint.Failure;
    }
}
