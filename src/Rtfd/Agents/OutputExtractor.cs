using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rtfd.Agents;

/// <summary>
/// Gets the findings document out of what an agent printed (spec "Getting the document out of
/// the agent"): an optional JSON path into a wrapper (Claude's <c>.result</c>), then the last
/// fenced JSON block in that text, or the whole text if it parses on its own.
/// </summary>
public static partial class OutputExtractor
{
    /// <summary>The value at a path such as <c>.result</c>, as text, or null when the output isn't JSON or lacks it.</summary>
    public static string? Select(string output, string path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(output);
        }
        catch (JsonException)
        {
            return null;
        }

        foreach (var name in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node is JsonObject obj ? obj[name] : null;
        }

        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject or JsonArray => node.ToJsonString(),
            _ => null,
        };
    }

    /// <summary>The findings document's JSON text in the model's answer, or null when there's none.</summary>
    public static string? Document(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && Parses(trimmed))
        {
            return trimmed;
        }

        // The last fenced block that parses: models often show an example first.
        foreach (var block in FencedBlock().Matches(text).Reverse())
        {
            var body = block.Groups["body"].Value.Trim();
            if (body.StartsWith('{') && Parses(body))
            {
                return body;
            }
        }

        return null;
    }

    private static bool Parses(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // NonBacktracking: this runs over the model's output, which PR content may have shaped.
    [GeneratedRegex(@"```[a-zA-Z]*[ \t]*\r?\n(?<body>.*?)\r?\n[ \t]*```", RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex FencedBlock();
}
