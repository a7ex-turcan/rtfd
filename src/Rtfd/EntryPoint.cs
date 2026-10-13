using System.Reflection;
using Rtfd.Cli;

namespace Rtfd;

/// <summary>
/// Dispatches to a mode of the single <c>rtfd</c> executable, by hand, as the siblings do.
/// </summary>
/// <remarks>
/// Only results are written to <c>stdout</c>: in <c>rtfd git-tools</c> it will be the MCP
/// protocol. Diagnostics go to <c>stderr</c>.
/// </remarks>
public static class EntryPoint
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;

    public const string PlanUrl = "https://github.com/a7ex-turcan/rtfd/blob/main/docs/plan.md";

    /// <summary>Commands from the spec's CLI table that aren't built yet, and the plan phase that brings each.</summary>
    internal static readonly IReadOnlyDictionary<string, string> NotYetBuilt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["serve"] = "Phase 5",
        ["status"] = "Phase 5",
        ["pause"] = "Phase 5",
        ["resume"] = "Phase 5",
        ["install-service"] = "Phase 6",
    };

    /// <param name="args">The command line.</param>
    /// <param name="stdout">Results.</param>
    /// <param name="stderr">Diagnostics.</param>
    /// <param name="environment">Reads an environment variable; tests pass their own so they never change the real one.</param>
    /// <param name="stdin">The MCP protocol's input in <c>rtfd git-tools</c>.</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<string, string?>? environment = null, TextReader? stdin = null)
    {
        if (args is ["--version" or "version"])
        {
            stdout.WriteLine(Version);
            return Ok;
        }

        if (args is [] or ["--help" or "-h" or "help"])
        {
            stderr.WriteLine(UsageText);
            return Usage;
        }

        var ctx = new CommandContext(stdout, stderr, environment ?? Environment.GetEnvironmentVariable);
        var rest = args[1..];
        try
        {
            switch (args[0])
            {
                case "validate":
                    return ValidateCommand.Run(ctx, rest);
                case "run":
                    return RunCommand.Run(ctx, rest);
                case "retry":
                    return RetryCommand.Run(ctx, rest);
                case "git-tools":
                    return GitToolsCommand.Run(ctx, rest, stdin ?? Console.In);
                case var command when NotYetBuilt.TryGetValue(command, out var phase):
                    stderr.WriteLine($"rtfd {command}: not built yet. It arrives in {phase} of the plan ({PlanUrl}).");
                    return Failure;
                default:
                    stderr.WriteLine($"rtfd: unknown command '{args[0]}'.");
                    stderr.WriteLine(UsageText);
                    return Usage;
            }
        }
        catch (CommandLineException ex)
        {
            stderr.WriteLine($"rtfd {args[0]}: {ex.Message}");
            return Usage;
        }
    }

    private static string UsageText => $"""
        rtfd {Version}
        usage: rtfd <command> [args]

          validate [--config <path>]       check the config, the tokens and repos, and what each agent
              [--config-only]              loads; --config-only stops after the config
          run <repo> --pr <n>              one review now, posted and read back
              [--dry-run] [--inputs-only] [--keep] [--verbose] [--config <path>]
          retry <repo> --pr <n>            give a PR its attempts back, or lift a secret block
          serve                            watch every configured repo (Phase 5)
          status                           is it watching, and what has it done (Phase 5)
          pause <repo> | resume <repo>     stop or restart watching one repo (Phase 5)
          install-service                  start serve at logon, headless (Phase 6)
          git-tools --run <folder>         internal: the rtfd-git MCP server, started by the agent
          --version

        The config is rtfd.yaml in the home: RTFD_HOME, else ~/.rtfd. --config <path> picks another.
        """;

    public static string Version
    {
        get
        {
            var informational = typeof(EntryPoint).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";

            // The SDK appends "+<commit>" when built from a git checkout.
            return informational.Split('+')[0];
        }
    }
}
