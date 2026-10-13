using Rtfd.Agents;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Processes;
using Rtfd.Providers;
using Rtfd.Providers.GitHub;

namespace Rtfd.Cli;

/// <summary>What validate talks to beyond the config; tests pass fakes.</summary>
/// <param name="Providers">Makes the provider client for a repo and its token.</param>
/// <param name="RtfdExecutable">The binary the probe's <c>mcp.json</c> starts as <c>rtfd-git</c>.</param>
/// <param name="Adjust">Changes the probe's command line; only tests use it, to prove a leak is caught.</param>
internal sealed record ValidateServices(
    Func<RepoConfig, ProviderToken, IProvider> Providers,
    string RtfdExecutable,
    Func<IReadOnlyList<string>, IReadOnlyList<string>>? Adjust = null)
{
    public static ValidateServices Real => new((repo, token) => new GitHubProvider(repo.Slug, token), Environment.ProcessPath ?? "rtfd");
}

/// <summary>
/// <c>rtfd validate [--config &lt;path&gt;] [--config-only]</c>: the config, then each repo's
/// token and visibility from the provider, then each agent launched as a review launches it,
/// printing the full list of what it loaded (plan Phase 2). The provider and agent checks wait
/// until the config has no errors.
/// </summary>
internal static class ValidateCommand
{
    public static int Run(CommandContext ctx, string[] args, ValidateServices? services = null)
    {
        var commandLine = new CommandLine(args, "config-only");
        commandLine.Expect(0, "config", "config-only");
        services ??= ValidateServices.Real;

        var home = ctx.Home;
        var path = commandLine.Value("config") is { } given ? Path.GetFullPath(given) : home.ConfigPath;
        if (!File.Exists(path))
        {
            ctx.Stderr.WriteLine($"rtfd validate: no config at {path}.");
            ctx.Stderr.WriteLine($"Start from examples/ in the rtfd repo: copy rtfd.yaml and prompts/ into {home.Root}, or pass --config <path>.");
            return EntryPoint.Failure;
        }

        var loaded = ConfigLoader.LoadFile(path, home);
        var diagnostics = loaded.Diagnostics.ToList();
        if (loaded.Config is { } loadedConfig)
        {
            diagnostics.AddRange(ConfigChecks.Run(loadedConfig, ctx.Environment));
        }

        var output = ctx.Stdout;
        output.WriteLine($"Config: {path}");
        if (loaded.Config is not null)
        {
            output.WriteLine($"Data:   {loaded.Config.Data.Root}");
        }

        string? skipped = null;
        if (loaded.Config is not { } config || diagnostics.Any(d => d.IsError))
        {
            skipped = "Tokens, repos and agents are checked once the config has no errors.";
        }
        else if (commandLine.Flag("config-only"))
        {
            skipped = "Tokens, repos and agents not checked (--config-only).";
        }
        else
        {
            ctx.Stderr.WriteLine("Checking tokens and repos with the provider...");
            var (repos, providerDiagnostics) = ProviderChecks.RunAsync(config, ctx.Environment, services.Providers).GetAwaiter().GetResult();
            diagnostics.AddRange(providerDiagnostics);
            PrintRepos(output, repos);

            var agents = new List<AgentReport>();
            foreach (var agent in config.Agents.Values.Where(a => a.Kind == AgentKind.Claude))
            {
                ctx.Stderr.WriteLine($"Launching {agent.Name} the way a review would...");
                agents.Add(Probe(config, agent, ctx.Environment, services));
            }

            diagnostics.AddRange(agents.SelectMany(a => a.Diagnostics));
            PrintAgents(output, agents);
        }

        output.WriteLine();
        foreach (var diagnostic in diagnostics.OrderBy(d => d.Line == 0 ? int.MaxValue : d.Line))
        {
            output.WriteLine(diagnostic);
        }

        if (diagnostics.Count > 0)
        {
            output.WriteLine();
        }

        if (loaded.Config is not null)
        {
            output.WriteLine($"{Listed(loaded.Config.Repos.Select(r => r.Name), "repo")}, {Listed(loaded.Config.Agents.Keys, "agent")}.");
        }

        var errors = diagnostics.Count(d => d.IsError);
        var warnings = diagnostics.Count - errors;
        output.WriteLine(errors == 0
            ? $"No errors{(warnings > 0 ? $", {Counted(warnings, "warning")}" : "")}."
            : $"{Counted(errors, "error")}, {Counted(warnings, "warning")}.");
        if (skipped is not null)
        {
            output.WriteLine(skipped);
        }

        return errors == 0 ? EntryPoint.Ok : EntryPoint.Failure;
    }

    private static AgentReport Probe(RtfdConfig config, AgentConfig agent, Func<string, string?> environment, ValidateServices services)
    {
        // The environment a review gives the agent: the allowlist, minus every token rtfd holds.
        var tokens = config.Repos.Select(r => environment(r.TokenEnv)).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();
        var childEnvironment = ChildEnvironment.Build(environment, tokens);
        var work = Path.Combine(config.Data.Root, "validate", $"{agent.Name}-{Guid.NewGuid().ToString("N")[..8]}");
        try
        {
            return AgentProbe.RunAsync(agent, childEnvironment, work, services.RtfdExecutable, config.LineOf($"{agent.Path}.command"), services.Adjust)
                .GetAwaiter().GetResult();
        }
        catch (GitException ex)
        {
            return new AgentReport
            {
                Agent = agent,
                Diagnostics = [new("agent.probe_failed", DiagnosticLevel.Error, $"rtfd couldn't set up the probe's run folder: {ex.Message}", $"{agent.Path}.command", config.LineOf($"{agent.Path}.command"))],
            };
        }
        finally
        {
            Folders.DeleteAsync(work).GetAwaiter().GetResult();
        }
    }

    private static void PrintRepos(TextWriter output, IReadOnlyList<RepoReport> repos)
    {
        output.WriteLine();
        output.WriteLine("Repos:");
        foreach (var report in repos)
        {
            var detail = report.Skipped is { } why
                ? $"not checked: {why}"
                : $"{(report.IsPublic == true ? "public" : "private")}, rtfd reads and posts as {report.Identity}";
            output.WriteLine($"  {report.Repo.Name}: {report.Repo.Slug}, {detail}");
        }
    }

    private static void PrintAgents(TextWriter output, IReadOnlyList<AgentReport> agents)
    {
        output.WriteLine();
        output.WriteLine("Agents:");
        foreach (var report in agents)
        {
            output.WriteLine($"  {report.Agent.Name}: {report.Version ?? "version unknown"}{(report.Executable is { } exe ? $" ({exe})" : "")}");
            if (!report.Probed)
            {
                output.WriteLine("    what it loads: unknown (see below)");
                continue;
            }

            const string prefix = "mcp__rtfd-git__";
            var builtIn = report.Tools.Where(t => !t.StartsWith("mcp__", StringComparison.Ordinal)).ToList();
            var gitTools = report.Tools.Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).Select(t => t[prefix.Length..]).ToList();
            var others = report.Tools.Where(t => t.StartsWith("mcp__", StringComparison.Ordinal) && !t.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            output.WriteLine($"    tools: {Joined(builtIn)}{(gitTools.Count > 0 ? $"; rtfd-git: {Joined(gitTools)}" : "")}{(others.Count > 0 ? $"; other MCP tools: {Joined(others)}" : "")}");
            output.WriteLine($"    MCP servers: {Joined(report.McpServers.Select(s => $"{s.Name} ({s.Status})"))}");
            var plugins = report.Plugins.Where(p => !p.EndsWith("@builtin", StringComparison.Ordinal)).ToList();
            var bundled = report.Plugins.Where(p => p.EndsWith("@builtin", StringComparison.Ordinal)).Select(p => p[..^"@builtin".Length]).ToList();
            output.WriteLine($"    plugins: {Joined(plugins)}{(bundled.Count > 0 ? $"; built into the CLI: {Joined(bundled)}" : "")}");
            output.WriteLine($"    skills: {Joined(report.Skills)}; commands: {Joined(report.SlashCommands)}");
        }
    }

    private static string Joined(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    private static string Listed(IEnumerable<string> names, string noun)
    {
        var all = names.ToList();
        return all.Count == 0 ? Counted(0, noun) : $"{Counted(all.Count, noun)} ({string.Join(", ", all)})";
    }

    private static string Counted(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
