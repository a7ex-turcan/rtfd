using System.Text.Json;
using System.Text.Json.Nodes;
using Rtfd.Config;
using Rtfd.Git;

namespace Rtfd.Review;

public sealed record Finding(FindingSeverity Severity, string? Path, int? Line, DiffSide Side, string? Anchor, string Body);

public enum ThreadActionKind
{
    Resolve,
    Reply,
}

public sealed record ThreadAction(string Id, ThreadActionKind Action, string? Reply);

/// <summary>
/// The agent's only output (spec "Findings contract"), schema version 1. Read strictly: an
/// unknown field is an error whose message tells the model why, so a retry can fix it. Parsed
/// over the JSON node tree by hand rather than with a serializer, for those messages.
/// </summary>
public sealed record FindingsDocument(string Summary, IReadOnlyList<Finding> Findings, IReadOnlyList<ThreadAction> Threads)
{
    public const int SchemaVersion = 1;

    private static readonly string[] DocumentFields = ["schema_version", "summary", "findings", "threads"];
    private static readonly string[] FindingFields = ["severity", "path", "line", "side", "anchor", "body"];
    private static readonly string[] ThreadFields = ["id", "action", "reply"];

    /// <summary>Parses the document, or returns every problem found in it.</summary>
    public static (FindingsDocument? Document, IReadOnlyList<string> Errors) Parse(string json)
    {
        var errors = new List<string>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            return (null, [$"the findings document isn't valid JSON: {ex.Message}"]);
        }

        if (root is not JsonObject document)
        {
            return (null, ["the findings document must be one JSON object"]);
        }

        UnknownFields(document, DocumentFields, "", errors);

        if (document["schema_version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != SchemaVersion)
        {
            errors.Add($"schema_version must be {SchemaVersion}");
        }

        var summary = String(document, "summary", "", errors, required: true) ?? "";
        var findings = new List<Finding>();
        if (document["findings"] is JsonArray items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (ParseFinding(items[i], $"findings[{i}]", errors) is { } finding)
                {
                    findings.Add(finding);
                }
            }
        }
        else
        {
            errors.Add("findings must be an array; use [] when there is nothing to say");
        }

        var threads = new List<ThreadAction>();
        switch (document["threads"])
        {
            case null:
                break;
            case JsonArray actions:
                for (var i = 0; i < actions.Count; i++)
                {
                    if (ParseThread(actions[i], $"threads[{i}]", errors) is { } action)
                    {
                        threads.Add(action);
                    }
                }

                break;
            default:
                errors.Add("threads must be an array");
                break;
        }

        return errors.Count > 0 ? (null, errors) : (new FindingsDocument(summary, findings, threads), []);
    }

    private static Finding? ParseFinding(JsonNode? node, string at, List<string> errors)
    {
        if (node is not JsonObject item)
        {
            errors.Add($"{at} must be an object");
            return null;
        }

        var count = errors.Count;
        UnknownFields(item, FindingFields, at + ".", errors);
        var severityText = String(item, "severity", at, errors, required: true);
        var path = String(item, "path", at, errors, required: false);
        var anchor = String(item, "anchor", at, errors, required: false);
        var body = String(item, "body", at, errors, required: true);
        var sideText = String(item, "side", at, errors, required: false) ?? "new";

        int? line = null;
        if (item["line"] is { } lineNode)
        {
            if (lineNode is JsonValue value && value.TryGetValue<int>(out var number) && number >= 1)
            {
                line = number;
            }
            else
            {
                errors.Add($"{at}.line must be a line number from 1, or left out");
            }
        }

        FindingSeverity? severity = severityText?.ToLowerInvariant() switch
        {
            "should" => FindingSeverity.Should,
            "could" => FindingSeverity.Could,
            "minor" => FindingSeverity.Minor,
            null => null,
            _ => null,
        };
        if (severityText is not null && severity is null)
        {
            errors.Add($"{at}.severity must be should, could or minor, not '{severityText}'");
        }

        DiffSide? side = sideText.ToLowerInvariant() switch
        {
            "new" => DiffSide.New,
            "old" => DiffSide.Old,
            _ => null,
        };
        if (side is null)
        {
            errors.Add($"{at}.side must be new (the PR head) or old (the merge base), not '{sideText}'");
        }

        if (body is { Length: 0 })
        {
            errors.Add($"{at}.body is empty");
        }

        if (path is not null && string.IsNullOrWhiteSpace(anchor))
        {
            errors.Add($"{at} names a path, so it needs an anchor: a short piece of the code it refers to, copied exactly. rtfd uses it to find the right line.");
        }

        if (path is { Length: 0 })
        {
            errors.Add($"{at}.path is empty; use null for a comment on the whole PR");
        }

        return errors.Count > count || severity is null || side is null || body is null
            ? null
            : new Finding(severity.Value, path, path is null ? null : line, side.Value, path is null ? null : anchor, body.Trim());
    }

    private static ThreadAction? ParseThread(JsonNode? node, string at, List<string> errors)
    {
        if (node is not JsonObject item)
        {
            errors.Add($"{at} must be an object");
            return null;
        }

        var count = errors.Count;
        UnknownFields(item, ThreadFields, at + ".", errors);
        var id = String(item, "id", at, errors, required: true);
        var actionText = String(item, "action", at, errors, required: true);
        var reply = String(item, "reply", at, errors, required: false);
        ThreadActionKind? action = actionText switch
        {
            "resolve" => ThreadActionKind.Resolve,
            "reply" => ThreadActionKind.Reply,
            null => null,
            _ => null,
        };
        if (actionText is not null && action is null)
        {
            errors.Add($"{at}.action must be resolve or reply, not '{actionText}'");
        }

        if (action == ThreadActionKind.Reply && string.IsNullOrWhiteSpace(reply))
        {
            errors.Add($"{at} is a reply, so it needs the reply text");
        }

        return errors.Count > count || id is null || action is null ? null : new ThreadAction(id, action.Value, reply?.Trim());
    }

    private static void UnknownFields(JsonObject item, string[] known, string at, List<string> errors)
    {
        foreach (var (name, _) in item)
        {
            if (known.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            errors.Add(name == "verdict"
                ? $"unknown field '{at}verdict': rtfd decides the verdict from your findings, so leave it out"
                : $"unknown field '{at}{name}'; the fields are {string.Join(", ", known)}");
        }
    }

    private static string? String(JsonObject item, string name, string at, List<string> errors, bool required)
    {
        var where = at.Length > 0 ? $"{at}.{name}" : name;
        switch (item[name])
        {
            case null when required:
                errors.Add($"{where} is required");
                return null;
            case null:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;
            default:
                errors.Add($"{where} must be a string");
                return null;
        }
    }
}
