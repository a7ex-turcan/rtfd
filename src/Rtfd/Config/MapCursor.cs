using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Rtfd.Config;

/// <summary>
/// A cursor over one YAML mapping that remembers which keys were read, so whatever is left can
/// be reported as an unknown key with its line number, as rtfq's does. A misspelt key in a file
/// that decides what an agent may touch must be a loud error, never a silent default.
/// </summary>
/// <remarks>
/// A value of the wrong shape (a single value where a list belongs) is an error too, never an
/// empty list. Every value read is recorded in <c>lines</c> under its dotted path, so the checks
/// that run after loading can point at the line they are about.
/// </remarks>
internal sealed class MapCursor(YamlMappingNode node, string path, List<Diagnostic> diagnostics, Dictionary<string, int> lines)
{
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public string Path => path;

    public int Line => LineOf(node);

    public string? String(string key)
    {
        if (Get(key) is not { } value)
        {
            return null;
        }

        if (value is YamlScalarNode scalar)
        {
            return scalar.Value;
        }

        Error("config.not_a_value", $"'{key}' must be a single value", key, value);
        return null;
    }

    public MapCursor? Map(string key)
    {
        if (Get(key) is not { } value)
        {
            return null;
        }

        if (value is YamlMappingNode map)
        {
            return new MapCursor(map, Join(key), diagnostics, lines);
        }

        Error("config.not_a_mapping", $"'{key}' must be a mapping of keys to values", key, value);
        return null;
    }

    public IReadOnlyList<string>? StringList(string key)
    {
        if (Get(key) is not { } value)
        {
            return null;
        }

        if (value is not YamlSequenceNode sequence)
        {
            Error("config.not_a_list", $"'{key}' must be a list, such as [a, b]", key, value);
            return null;
        }

        var items = new List<string>();
        foreach (var item in sequence)
        {
            if (item is YamlScalarNode { Value: { } text } scalar && !IsNull(scalar))
            {
                items.Add(text);
            }
            else
            {
                Error("config.not_a_value", $"each item of '{key}' must be a single value", key, item);
            }
        }

        return items;
    }

    /// <summary>The value of <paramref name="key"/> as a list, where a single value counts as a list of one.</summary>
    public IReadOnlyList<string>? ValueOrList(string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var value))
        {
            _used.Add(key);
            return null;
        }

        return value is YamlSequenceNode ? StringList(key) : String(key) is { } single ? [single] : null;
    }

    /// <summary>A list of mappings, such as <c>repos</c>.</summary>
    public IReadOnlyList<MapCursor> MapList(string key)
    {
        if (Get(key) is not { } value)
        {
            return [];
        }

        if (value is not YamlSequenceNode sequence)
        {
            Error("config.not_a_list", $"'{key}' must be a list", key, value);
            return [];
        }

        var items = new List<MapCursor>();
        var index = 0;
        foreach (var item in sequence)
        {
            var itemPath = $"{Join(key)}[{index++}]";
            if (item is YamlMappingNode map)
            {
                lines[itemPath] = LineOf(item);
                items.Add(new MapCursor(map, itemPath, diagnostics, lines));
            }
            else
            {
                diagnostics.Add(new("config.not_a_mapping", DiagnosticLevel.Error,
                    $"each item of '{key}' must be a mapping of keys to values", itemPath, LineOf(item)));
            }
        }

        return items;
    }

    /// <summary>The keys of this mapping, for mappings whose keys are names the user chose, such as <c>agents</c>.</summary>
    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var (key, _) in node.Children)
        {
            if (key is YamlScalarNode { Value: { } name })
            {
                keys.Add(name);
            }
        }

        return keys;
    }

    public int? PositiveInt(string key)
    {
        if (String(key) is not { } raw)
        {
            return null;
        }

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            return value;
        }

        Report("config.not_a_positive_integer", $"'{raw}' is not a positive whole number", key);
        return null;
    }

    public TimeSpan? Duration(string key)
    {
        if (String(key) is not { } raw)
        {
            return null;
        }

        if (Config.Duration.TryParse(raw, out var value))
        {
            return value;
        }

        Report("config.not_a_duration", $"'{raw}' is not a duration; use forms like 30s, 5m, 1h", key);
        return null;
    }

    public bool? Bool(string key)
    {
        switch (String(key)?.ToLowerInvariant())
        {
            case null:
                return null;
            case "true" or "yes":
                return true;
            case "false" or "no":
                return false;
            default:
                Report("config.not_a_boolean", $"'{key}' must be true or false", key);
                return null;
        }
    }

    /// <summary>Adds an error about the value of <paramref name="key"/>, at its line.</summary>
    public void Report(string check, string message, string key) =>
        diagnostics.Add(new(check, DiagnosticLevel.Error, message, Join(key), LineOf(key)));

    public void ReportUnknownKeys()
    {
        foreach (var (key, _) in node.Children)
        {
            if (key is YamlScalarNode { Value: { } name } && !_used.Contains(name))
            {
                diagnostics.Add(new("config.unknown_key", DiagnosticLevel.Error, $"unknown key '{name}'", Join(name), LineOf(key)));
            }
        }
    }

    public string Join(string key) => path.Length == 0 ? key : $"{path}.{key}";

    /// <summary>The value at <paramref name="key"/>, or null when it is absent or explicitly empty (<c>key:</c>, <c>~</c>, <c>null</c>).</summary>
    private YamlNode? Get(string key)
    {
        _used.Add(key);
        if (!TryFind(key, out var keyNode, out var value))
        {
            return null;
        }

        lines[Join(key)] = LineOf(keyNode);
        return value is YamlScalarNode scalar && IsNull(scalar) ? null : value;
    }

    /// <summary>
    /// Finds the entry for <paramref name="key"/>. The key's own line is what a person looks for:
    /// a block list or mapping as the value starts on the line after it.
    /// </summary>
    private bool TryFind(string key, out YamlNode keyNode, out YamlNode value)
    {
        foreach (var (k, v) in node.Children)
        {
            if (k is YamlScalarNode { Value: { } name } && name == key)
            {
                keyNode = k;
                value = v;
                return true;
            }
        }

        keyNode = value = null!;
        return false;
    }

    private void Error(string check, string message, string key, YamlNode at) =>
        diagnostics.Add(new(check, DiagnosticLevel.Error, message, Join(key), LineOf(at)));

    private int LineOf(string key) => TryFind(key, out var keyNode, out _) ? LineOf(keyNode) : Line;

    private static int LineOf(YamlNode at) => (int)at.Start.Line;

    private static bool IsNull(YamlScalarNode scalar) =>
        scalar.Style == ScalarStyle.Plain && scalar.Value is null or "" or "~" or "null" or "Null" or "NULL";
}
