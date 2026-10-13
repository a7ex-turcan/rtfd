using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rtfd.GitTools;

/// <summary>
/// <c>rtfd-git</c>'s MCP surface: JSON-RPC 2.0 over stdio, hand-rolled as rtfq's
/// <c>McpServer.cs</c> is (no reflection, so it stays AOT-clean), mapping four tools onto
/// <see cref="GitToolset"/>. A refusal is a tool result with <c>isError</c>, not a protocol
/// error, so the agent can read why and adapt.
/// </summary>
/// <remarks><b>stdout is the protocol channel.</b> Nothing else may write to it.</remarks>
public sealed class McpServer(GitToolset tools, TextReader input, TextWriter output)
{
    private const string DefaultProtocolVersion = "2025-06-18";

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject request;
            try
            {
                request = JsonNode.Parse(line)?.AsObject() ?? throw new JsonException("not a JSON object");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                await WriteAsync(Error(null, -32700, "Parse error: " + ex.Message)).ConfigureAwait(false);
                continue;
            }

            var response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await WriteAsync(response).ConfigureAwait(false);
            }
        }
    }

    internal async Task<JsonObject?> HandleAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        switch (method)
        {
            case "initialize":
                var requested = request["params"]?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var version) ? version : DefaultProtocolVersion;
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = requested,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "rtfd-git", ["version"] = EntryPoint.Version },
                    ["instructions"] = "Read-only git history for the PR under review. Commits must be this PR's: the head, the merge base, or a commit between them.",
                });

            case "notifications/initialized" or "notifications/cancelled":
                return null;

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = Describe() });

            case "tools/call":
                return Result(id, await CallAsync(request["params"] as JsonObject, cancellationToken).ConfigureAwait(false));

            case null:
                return Error(id, -32600, "Invalid request: no method");

            default:
                return id is null ? null : Error(id, -32601, $"Method not found: {method}");
        }
    }

    private async Task<JsonObject> CallAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            var text = (parameters?["name"] as JsonValue)?.GetValue<string>() switch
            {
                "log" => await tools.LogAsync(OptionalString(arguments, "path"), OptionalInt(arguments, "max"), cancellationToken).ConfigureAwait(false),
                "show" => await tools.ShowAsync(RequiredString(arguments, "sha"), OptionalString(arguments, "path"), cancellationToken).ConfigureAwait(false),
                "blame" => await tools.BlameAsync(RequiredString(arguments, "path"), RequiredInt(arguments, "start"), RequiredInt(arguments, "end"), cancellationToken).ConfigureAwait(false),
                "diff" => await tools.DiffAsync(RequiredString(arguments, "from"), RequiredString(arguments, "to"), OptionalString(arguments, "path"), cancellationToken).ConfigureAwait(false),
                var other => throw new ToolRefusal($"no tool '{other}'; the tools are log, show, blame and diff"),
            };
            return Content(text.Length > 0 ? text : "(no output)", isError: false);
        }
        catch (ToolRefusal ex)
        {
            return Content(ex.Message, isError: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            return Content($"bad arguments: {ex.Message}", isError: true);
        }
    }

    private static string RequiredString(JsonObject arguments, string name) =>
        OptionalString(arguments, name) ?? throw new ToolRefusal($"'{name}' is required");

    private static string? OptionalString(JsonObject arguments, string name) => arguments[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new ToolRefusal($"'{name}' must be a string"),
    };

    private static int RequiredInt(JsonObject arguments, string name) =>
        OptionalInt(arguments, name) ?? throw new ToolRefusal($"'{name}' is required");

    private static int? OptionalInt(JsonObject arguments, string name) => arguments[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<int>(out var number) => number,
        JsonValue value when value.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
        _ => throw new ToolRefusal($"'{name}' must be a whole number"),
    };

    // The params constructor, not a collection expression: that compiles to JsonArray.Add<T>,
    // which needs reflection and fails the Native AOT publish.
    private static JsonArray Describe() => new(
    [
        Tool("log", "Commits on this PR, newest first, with the files each touched. With a path: that path's history from the PR head.",
            new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "A path relative to the repo root." },
                ["max"] = new JsonObject { ["type"] = "integer", ["description"] = $"How many commits, 1 to {GitToolset.MaxLogCount}. Default 20." },
            }, []),
        Tool("show", "One commit of this PR with its patch, or with a path, that file's content at that commit.",
            new JsonObject
            {
                ["sha"] = new JsonObject { ["type"] = "string", ["description"] = "The PR head, the merge base, or a commit between them." },
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "A path relative to the repo root." },
            }, ["sha"]),
        Tool("blame", $"Who last changed each line of a range, at the PR head. At most {GitToolset.MaxBlameLines} lines.",
            new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "A path relative to the repo root." },
                ["start"] = new JsonObject { ["type"] = "integer", ["description"] = "First line, from 1." },
                ["end"] = new JsonObject { ["type"] = "integer", ["description"] = "Last line." },
            }, ["path", "start", "end"]),
        Tool("diff", "The diff between two commits of this PR or its merge base, optionally for one path.",
            new JsonObject
            {
                ["from"] = new JsonObject { ["type"] = "string", ["description"] = "A commit of this PR or the merge base." },
                ["to"] = new JsonObject { ["type"] = "string", ["description"] = "A commit of this PR or the merge base." },
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "A path relative to the repo root." },
            }, ["from", "to"]),
    ]);

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
            ["additionalProperties"] = false,
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
    };

    private static JsonObject Content(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private async Task WriteAsync(JsonObject message)
    {
        await output.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}
