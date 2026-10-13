using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rtfd.Config;
using Rtfd.Processes;

namespace Rtfd.Agents;

/// <summary>How a run ended (spec "Agent adapter").</summary>
public enum OutcomeKind
{
    /// <summary>The agent answered; whether the document is valid is the validator's call.</summary>
    Ok,

    /// <summary>JSON missing, bad, or rejected by the validator. Retried with the errors, and counted.</summary>
    Invalid,

    /// <summary>A usage or rate limit. The whole account pauses until <see cref="AgentOutcome.Limit"/>, and the attempt isn't counted.</summary>
    Limit,

    /// <summary>Network, timeout, an unexplained failure. Retried with backoff.</summary>
    Transient,

    /// <summary>Auth, a missing binary, or a secret from outside the diff. Not retried.</summary>
    Fatal,
}

/// <param name="Text">The model's answer, extracted from the CLI's wrapper; set when <see cref="Kind"/> is Ok.</param>
/// <param name="Message">Why, for anything but Ok.</param>
public sealed record AgentOutcome(OutcomeKind Kind, string? Text, string? Message)
{
    public LimitHit? Limit { get; init; }
    public required ProcessResult Process { get; init; }
}

/// <summary>Where one run's files are, for rendering the agent's command line.</summary>
public sealed record AgentRunPaths(string RunFolder, string Worktree, string Prompt, string Settings, string McpConfig);

/// <summary>
/// Starts the agent for one attempt and sorts the result. The child gets the allowlisted
/// environment (no token, no <c>CLAUDE*</c>), the run folder as its working directory, the
/// prompt on stdin, and the command line the adapter for its CLI renders.
/// </summary>
public static partial class AgentRunner
{
    public static async Task<AgentOutcome> RunAsync(
        AgentConfig agent, AgentRunPaths paths, string prompt, IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, DateTimeOffset now, TimeZoneInfo zone, CancellationToken cancellationToken = default)
    {
        var arguments = agent.Kind switch
        {
            AgentKind.Claude => ClaudeAdapter.Arguments(agent, paths),
            _ => throw new NotSupportedException($"rtfd can't run a {agent.Kind} agent yet"),
        };

        var process = await ProcessRunner.RunAsync(new ProcessRequest
        {
            FileName = agent.Command[0],
            Arguments = arguments,
            Environment = environment,
            WorkingDirectory = paths.RunFolder,
            Stdin = prompt,
            Timeout = timeout,
        }, cancellationToken).ConfigureAwait(false);

        return Classify(agent, process, now, zone, timeout);
    }

    /// <summary>Sorts a finished process. Pure, so every case is a test.</summary>
    public static AgentOutcome Classify(AgentConfig agent, ProcessResult process, DateTimeOffset now, TimeZoneInfo zone, TimeSpan timeout)
    {
        if (process.StartError is { } startError)
        {
            return new(OutcomeKind.Fatal, null, startError) { Process = process };
        }

        if (process.TimedOut)
        {
            return new(OutcomeKind.Transient, null, $"the agent didn't finish within {timeout}") { Process = process };
        }

        var reply = agent.Output.Extract is { } path ? Wrapper(process.Stdout, path) : new Reply(process.Stdout, false, null, null);
        var failed = process.ExitCode != 0 || reply.IsError || reply.Text is null;
        if (!failed)
        {
            return new(OutcomeKind.Ok, reply.Text, null) { Process = process };
        }

        // Limit and auth patterns only apply to a failed run: a review of rate-limiting code
        // mentions "rate limit" too.
        var everything = string.Join('\n', reply.Text ?? "", reply.Error ?? "", process.Stdout, process.Stderr);
        if (LimitParser.Parse(everything, agent.LimitPatterns, now, zone) is { } limit)
        {
            return new(OutcomeKind.Limit, null, limit.Message) { Process = process, Limit = limit };
        }

        // Claude also says so in fields, which outlive a change of wording: a limit with no
        // reset time pauses for the default.
        if (reply.ApiError is "usage_limit_reached" or "rate_limit_error" or "rate_limit" || reply.ApiStatus == 429)
        {
            var message = FirstLine(reply.Text) ?? reply.ApiError ?? "HTTP 429";
            return new(OutcomeKind.Limit, null, message) { Process = process, Limit = new LimitHit(now + LimitParser.DefaultPause, message) };
        }

        if (AuthFailure().Match(everything) is { Success: true } auth)
        {
            return new(OutcomeKind.Fatal, null, $"the agent isn't logged in or its credentials were refused ({auth.Value.Trim()})") { Process = process };
        }

        var detail = FirstLine(reply.Error ?? reply.Text ?? process.Stderr) ?? $"exit code {process.ExitCode}";
        return new(OutcomeKind.Transient, null, $"the agent failed: {detail}") { Process = process };
    }

    private sealed record Reply(string? Text, bool IsError, string? Error, string? ApiError, int? ApiStatus = null);

    /// <summary>A CLI that wraps the answer in its own JSON, as <c>claude --output-format json</c> does.</summary>
    private static Reply Wrapper(string stdout, string path)
    {
        var text = OutputExtractor.Select(stdout, path);
        bool isError;
        string? error = null;
        string? apiError;
        int? apiStatus;
        try
        {
            var root = JsonNode.Parse(stdout) as JsonObject;
            isError = root?["is_error"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
            apiError = root?["api_error"] is JsonValue a && a.TryGetValue<string>(out var api) ? api : null;
            apiStatus = root?["api_error_status"] is JsonValue st && st.TryGetValue<int>(out var code) ? code : null;
            if (root?["subtype"] is JsonValue subtype && subtype.TryGetValue<string>(out var name) && name.StartsWith("error", StringComparison.Ordinal))
            {
                isError = true;
                error = name;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return new Reply(null, true, null, null);
        }

        return new Reply(text, isError, error ?? (isError ? text : null), apiError, apiStatus);
    }

    private static string? FirstLine(string? text)
    {
        var line = text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : line.Length <= 200 ? line : line[..200] + "...";
    }

    [GeneratedRegex(@"(?i)invalid api key|please run /login|authentication_error|oauth token (has )?expired|not logged in|credit balance is too low", RegexOptions.NonBacktracking)]
    private static partial Regex AuthFailure();
}
