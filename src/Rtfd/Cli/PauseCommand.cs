using System.Globalization;
using Rtfd.State;

namespace Rtfd.Cli;

/// <summary>
/// <c>rtfd pause &lt;repo&gt;</c> and <c>rtfd resume &lt;repo&gt;</c> (spec "CLI"): a flag in the store that
/// serve reads on every tick, so neither needs a restart. They replace the old watchers' <c>.stop</c>
/// file. Resume also lifts a stop after a fatal outcome. A paused repo can still be reviewed by
/// hand with <c>rtfd run</c>.
/// </summary>
internal static class PauseCommand
{
    public static int Run(CommandContext ctx, string[] args, bool pause, TimeProvider? clock = null)
    {
        var command = pause ? "pause" : "resume";
        var commandLine = new CommandLine(args);
        commandLine.Expect(1, "config");
        if (commandLine.Positionals.Count == 0)
        {
            throw new CommandLineException($"which repo? usage: rtfd {command} <repo>");
        }

        if (LoadedConfig.Load(ctx, commandLine, command, checks: false) is not { } config || LoadedConfig.Repo(ctx, config, commandLine, command) is not { } repo)
        {
            return EntryPoint.Failure;
        }

        using var store = StateStore.Open(config.Data.StatePath);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (pause)
        {
            var before = store.Flags(repo.Name);
            store.Pause(repo.Name, now);
            ctx.Stdout.WriteLine(before.PausedAt is { } since
                ? $"{repo.Name} has been paused since {Local(since)}."
                : $"{repo.Name}: paused. serve stops polling it at its next tick; a review already running finishes. `rtfd resume {repo.Name}` watches it again.");
            return EntryPoint.Ok;
        }

        var flags = store.Resume(repo.Name);
        var lifted = new List<string>();
        if (flags.PausedAt is not null)
        {
            lifted.Add("the pause");
        }

        if (flags.StoppedReason is { } reason)
        {
            lifted.Add($"the stop ({reason})");
        }

        ctx.Stdout.WriteLine(lifted.Count == 0
            ? $"{repo.Name} wasn't paused or stopped."
            : $"{repo.Name}: lifted {string.Join(" and ", lifted)}. serve watches it again from its next tick.");
        return EntryPoint.Ok;
    }

    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
