// Converts gitleaks' default config (config/gitleaks.toml at a pinned tag) into the JSON rtfd
// embeds, src/Rtfd/Resources/gitleaks-rules.json. Run from the repo root:
//
//   gh api -H "Accept: application/vnd.github.raw" "repos/gitleaks/gitleaks/contents/config/gitleaks.toml?ref=v8.30.1" > gitleaks.toml
//   dotnet run scripts/gitleaks-rules.cs -- gitleaks.toml v8.30.1
//
// It reads only the TOML gitleaks' generator writes (tables, arrays of tables, strings, numbers,
// arrays of strings), turns RE2 syntax .NET lacks into its equivalent, and refuses to write the
// file if any pattern doesn't compile with RegexOptions.NonBacktracking, the engine rtfd uses.
// The licence notice goes next to the JSON as gitleaks-LICENSE.txt.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run scripts/gitleaks-rules.cs -- <gitleaks.toml> <tag>");
    return 2;
}

var (global, rules) = Parse(File.ReadAllLines(args[0]));
var failures = new List<string>();
var output = Path.Combine("src", "Rtfd", "Resources", "gitleaks-rules.json");

using (var stream = new MemoryStream())
{
    using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
    {
        json.WriteStartObject();
        json.WriteString("source", $"gitleaks {args[1]}, config/gitleaks.toml, converted by scripts/gitleaks-rules.cs");
        json.WritePropertyName("allowlist");
        WriteAllowlist(json, global, "allowlist");
        json.WriteStartArray("rules");
        foreach (var rule in rules)
        {
            if (!rule.TryGetValue("regex", out var regex))
            {
                // Path-only rules (a .p12 file, say) look at file names, not text.
                continue;
            }

            var id = (string)rule["id"];
            json.WriteStartObject();
            json.WriteString("id", id);
            json.WriteString("regex", Convert(id, (string)regex, failures));
            if (rule.TryGetValue("secretGroup", out var group))
            {
                json.WriteNumber("secret_group", (double)group);
            }

            if (rule.TryGetValue("entropy", out var entropy))
            {
                json.WriteNumber("entropy", (double)entropy);
            }

            if (rule.TryGetValue("path", out var path))
            {
                json.WriteString("path", Convert(id, (string)path, failures));
            }

            json.WriteStartArray("keywords");
            foreach (var keyword in Strings(rule, "keywords"))
            {
                json.WriteStringValue(keyword.ToLowerInvariant());
            }

            json.WriteEndArray();
            json.WriteStartArray("allowlists");
            foreach (var allowlist in (List<Dictionary<string, object>>?)rule.GetValueOrDefault("allowlists") ?? [])
            {
                WriteAllowlist(json, allowlist, id);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    if (failures.Count > 0)
    {
        Console.Error.WriteLine($"{failures.Count} patterns don't compile with NonBacktracking; nothing written:");
        failures.ForEach(f => Console.Error.WriteLine("  " + f));
        return 1;
    }

    File.WriteAllBytes(output, [.. stream.ToArray(), (byte)'\n']);
}

Console.WriteLine($"wrote {rules.Count(r => r.ContainsKey("regex"))} rules to {output}");
return 0;

void WriteAllowlist(Utf8JsonWriter json, Dictionary<string, object> allowlist, string owner)
{
    json.WriteStartObject();
    json.WriteString("condition", ((string?)allowlist.GetValueOrDefault("condition") ?? "OR").ToUpperInvariant());
    json.WriteString("regex_target", (string?)allowlist.GetValueOrDefault("regexTarget") ?? "secret");
    foreach (var (key, name) in new[] { ("regexes", "regexes"), ("paths", "paths") })
    {
        json.WriteStartArray(name);
        foreach (var pattern in Strings(allowlist, key))
        {
            json.WriteStringValue(Convert(owner, pattern, failures));
        }

        json.WriteEndArray();
    }

    json.WriteStartArray("stopwords");
    foreach (var word in Strings(allowlist, "stopwords"))
    {
        json.WriteStringValue(word.ToLowerInvariant());
    }

    json.WriteEndArray();
    json.WriteEndObject();
}

static IEnumerable<string> Strings(Dictionary<string, object> table, string key) =>
    table.TryGetValue(key, out var value) ? ((List<object>)value).Cast<string>() : [];

// RE2 to .NET: POSIX classes, Python-style named groups and \x{...} escapes. The rest of RE2's
// syntax means the same in .NET, and RE2 has nothing NonBacktracking refuses (no lookaround,
// no backreferences).
static string Convert(string owner, string pattern, List<string> failures)
{
    var converted = pattern
        .Replace("[:alnum:]", "a-zA-Z0-9")
        .Replace("[:alpha:]", "a-zA-Z")
        .Replace("[:digit:]", "0-9")
        .Replace("[:xdigit:]", "0-9A-Fa-f")
        .Replace("[:upper:]", "A-Z")
        .Replace("[:lower:]", "a-z")
        .Replace("[:space:]", @"\s")
        .Replace("[:word:]", @"\w")
        .Replace("(?P<", "(?<");
    converted = Regex.Replace(converted, @"\\x\{([0-9A-Fa-f]{1,4})\}", m => @"\u" + m.Groups[1].Value.PadLeft(4, '0'));
    try
    {
        _ = new Regex(converted, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
    {
        failures.Add($"{owner}: {ex.Message}");
    }

    return converted;
}

// The TOML subset gitleaks' generator writes.
static (Dictionary<string, object> Global, List<Dictionary<string, object>> Rules) Parse(string[] lines)
{
    var global = new Dictionary<string, object>();
    var rules = new List<Dictionary<string, object>>();
    var root = new Dictionary<string, object>();
    var current = root;
    for (var i = 0; i < lines.Length; i++)
    {
        var line = lines[i].Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        switch (line)
        {
            case "[allowlist]":
                current = global;
                continue;
            case "[[rules]]":
                current = [];
                rules.Add(current);
                continue;
            case "[[rules.allowlists]]":
                var allowlists = (List<Dictionary<string, object>>)(rules[^1].TryGetValue("allowlists", out var existing)
                    ? existing
                    : rules[^1]["allowlists"] = new List<Dictionary<string, object>>());
                current = [];
                allowlists.Add(current);
                continue;
        }

        if (line.StartsWith('['))
        {
            throw new InvalidDataException($"line {i + 1}: unexpected table {line}");
        }

        var equals = line.IndexOf('=');
        var key = line[..equals].Trim();
        var rest = line[(equals + 1)..].Trim();
        if (rest.StartsWith('['))
        {
            // An array of strings, one item per line until the closing bracket.
            var items = new List<object>();
            var text = new StringBuilder(rest[1..]);
            while (!text.ToString().TrimEnd().EndsWith(']'))
            {
                text.Append('\n').Append(lines[++i]);
            }

            var body = text.ToString().TrimEnd();
            var position = 0;
            body = body[..^1];
            while (true)
            {
                while (position < body.Length && (char.IsWhiteSpace(body[position]) || body[position] == ','))
                {
                    position++;
                }

                if (position >= body.Length)
                {
                    break;
                }

                items.Add(ReadString(body, ref position));
            }

            current[key] = items;
        }
        else if (rest.StartsWith('\'') || rest.StartsWith('"'))
        {
            var position = 0;
            current[key] = ReadString(rest, ref position);
        }
        else
        {
            current[key] = double.Parse(rest, CultureInfo.InvariantCulture);
        }
    }

    return (global, rules);
}

static string ReadString(string text, ref int position)
{
    if (text.AsSpan(position).StartsWith("'''"))
    {
        var end = text.IndexOf("'''", position + 3, StringComparison.Ordinal);
        var value = text[(position + 3)..end];
        position = end + 3;
        return value;
    }

    if (text[position] == '\'')
    {
        var end = text.IndexOf('\'', position + 1);
        var value = text[(position + 1)..end];
        position = end + 1;
        return value;
    }

    var result = new StringBuilder();
    position++;
    while (text[position] != '"')
    {
        if (text[position] == '\\')
        {
            position++;
            switch (text[position])
            {
                case 'n': result.Append('\n'); break;
                case 't': result.Append('\t'); break;
                case 'r': result.Append('\r'); break;
                case 'u':
                    result.Append((char)int.Parse(text.AsSpan(position + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    position += 4;
                    break;
                default: result.Append(text[position]); break;
            }
        }
        else
        {
            result.Append(text[position]);
        }

        position++;
    }

    position++;
    return result.ToString();
}
