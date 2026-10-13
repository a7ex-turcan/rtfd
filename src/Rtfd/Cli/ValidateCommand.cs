using Rtfd.Config;

namespace Rtfd.Cli;

/// <summary>
/// <c>rtfd validate [--config &lt;path&gt;]</c>: loads the config and runs every check that needs
/// neither the provider nor an agent. The rest arrive in Phase 2.
/// </summary>
internal static class ValidateCommand
{
    public static int Run(CommandContext ctx, string[] args)
    {
        var commandLine = new CommandLine(args);
        commandLine.Expect(0, "config");

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
        if (loaded.Config is { } config)
        {
            diagnostics.AddRange(ConfigChecks.Run(config, ctx.Environment));
        }

        var output = ctx.Stdout;
        output.WriteLine($"Config: {path}");
        if (loaded.Config is not null)
        {
            output.WriteLine($"Data:   {loaded.Config.Data.Root}");
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

        output.WriteLine("Not checked until Phase 2:");
        foreach (var item in ConfigChecks.NotYetRun)
        {
            output.WriteLine($"  - {item}");
        }

        return errors == 0 ? EntryPoint.Ok : EntryPoint.Failure;
    }

    private static string Listed(IEnumerable<string> names, string noun)
    {
        var all = names.ToList();
        return all.Count == 0 ? Counted(0, noun) : $"{Counted(all.Count, noun)} ({string.Join(", ", all)})";
    }

    private static string Counted(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
