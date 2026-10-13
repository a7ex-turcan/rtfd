using System.Text;

namespace Rtfd.Config;

/// <summary>
/// Splits an agent's command template into arguments: whitespace separates, double or single
/// quotes group, and backslashes are literal so Windows paths need no escaping. rtfd starts the
/// result with an argument list, never through a shell.
/// </summary>
public static class CommandSplitter
{
    /// <returns>The arguments, or null when a quote is left open.</returns>
    public static IReadOnlyList<string>? Split(string command)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inArgument = false;
        char? quote = null;
        foreach (var c in command)
        {
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inArgument = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inArgument)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    inArgument = false;
                }
            }
            else
            {
                current.Append(c);
                inArgument = true;
            }
        }

        if (quote is not null)
        {
            return null;
        }

        if (inArgument)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    /// <summary>
    /// The agent CLI a command starts, from its executable's file name: <c>claude</c>,
    /// <c>C:\tools\claude.cmd</c> and <c>/usr/local/bin/claude</c> are all claude. Anything
    /// else, a shell or a wrapper in front of a known CLI included, is unknown.
    /// </summary>
    public static AgentKind KindOf(string executable)
    {
        var fileName = executable.Replace('\\', '/').Split('/')[^1];
        var dot = fileName.LastIndexOf('.');
        var name = (dot > 0 ? fileName[..dot] : fileName).ToLowerInvariant();
        return name switch
        {
            "claude" => AgentKind.Claude,
            "copilot" => AgentKind.Copilot,
            _ => AgentKind.Unknown,
        };
    }
}
