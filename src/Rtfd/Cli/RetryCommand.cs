using Rtfd.Config;
using Rtfd.State;

namespace Rtfd.Cli;

/// <summary>
/// <c>rtfd retry &lt;repo&gt; --pr &lt;n&gt; [--config &lt;path&gt;]</c>: gives the PR's heads their
/// attempts back, and lifts a secret block once someone has looked at it (spec "CLI"). It only
/// touches the state store.
/// </summary>
internal static class RetryCommand
{
    public static int Run(CommandContext ctx, string[] args)
    {
        var commandLine = new CommandLine(args);
        commandLine.Expect(1, "config", "pr");
        if (commandLine.Positionals.Count == 0)
        {
            throw new CommandLineException("which repo? usage: rtfd retry <repo> --pr <n>");
        }

        var number = commandLine.IntValue("pr") ?? throw new CommandLineException("'--pr <n>' is required.");
        var home = ctx.Home;
        var path = commandLine.Value("config") is { } given ? Path.GetFullPath(given) : home.ConfigPath;
        var loaded = File.Exists(path) ? ConfigLoader.LoadFile(path, home) : null;
        if (loaded?.Config is not { } config)
        {
            ctx.Stderr.WriteLine($"rtfd retry: no usable config at {path}; `rtfd validate` says why.");
            return EntryPoint.Failure;
        }

        var name = commandLine.Positionals[0];
        if (config.Repos.All(r => r.Name != name))
        {
            ctx.Stderr.WriteLine($"rtfd retry: no repo named '{name}' in {path}.");
            return EntryPoint.Failure;
        }

        using var store = StateStore.Open(config.Data.StatePath);
        var cleared = store.Retry(name, number);
        if (cleared.Count == 0)
        {
            ctx.Stdout.WriteLine($"{name} #{number}: nothing to clear. No attempts are used and nothing is blocked.");
            return EntryPoint.Ok;
        }

        foreach (var entry in cleared)
        {
            var what = entry.Blocked is { } reason
                ? $"lifted the block ({reason}){(entry.Used > 0 ? $" and gave back {entry.Used} attempt{(entry.Used == 1 ? "" : "s")}" : "")}"
                : $"gave back {entry.Used} attempt{(entry.Used == 1 ? "" : "s")}";
            ctx.Stdout.WriteLine($"{name} #{number} at {entry.Head[..8]}: {what}.");
        }

        return EntryPoint.Ok;
    }
}
