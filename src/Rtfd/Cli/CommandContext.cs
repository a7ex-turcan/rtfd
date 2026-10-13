namespace Rtfd.Cli;

/// <summary>What every command gets: where to write, and how to read the environment.</summary>
/// <remarks>The environment is a function so tests never have to change the real one.</remarks>
public sealed record CommandContext(TextWriter Stdout, TextWriter Stderr, Func<string, string?> Environment)
{
    public RtfdHome Home => RtfdHome.Resolve(Environment);
}
