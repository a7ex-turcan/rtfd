using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rtfd.Config;

namespace Rtfd.Review.Secrets;

/// <param name="Rule">The gitleaks rule id, <c>extra[n]</c>, or <c>rtfd-token</c> for a token rtfd holds.</param>
/// <param name="Secret">The secret itself: the rule's secret group, or the whole match.</param>
public sealed record SecretMatch(string Rule, string Secret);

/// <summary>
/// The secret check on outgoing text (spec "Validator"): the gitleaks rule set, loaded as data
/// and compiled with <see cref="RegexOptions.NonBacktracking"/> (the rules are RE2, which fits
/// the linear-time engine, and the text is untrusted), the config's extra patterns, and an
/// exact match against every token value rtfd holds.
/// </summary>
/// <remarks>
/// A speed bump, not a wall: a secret base64-encoded or split across two findings gets past it.
/// Confinement is the real defence (spec decision 2).
/// </remarks>
public sealed class SecretScanner
{
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly Lazy<RuleSet> Bundled = new(LoadBundled);

    private readonly IReadOnlyList<Rule> _rules;
    private readonly Allowlist _global;
    private readonly IReadOnlyList<string> _tokens;

    private SecretScanner(IReadOnlyList<Rule> rules, Allowlist global, IReadOnlyList<string> tokens)
    {
        _rules = rules;
        _global = global;
        _tokens = tokens;
    }

    /// <summary>How many gitleaks rules are loaded; a test checks every one compiled.</summary>
    public static int BundledRuleCount => Bundled.Value.Rules.Count;

    /// <param name="extra">The config's <c>secrets.extra</c> patterns.</param>
    /// <param name="tokens">Every token value rtfd holds, not just this repo's.</param>
    public static SecretScanner Create(IReadOnlyList<ConfigPattern> extra, IReadOnlyCollection<string> tokens)
    {
        var rules = Bundled.Value.Rules.Concat(extra.Select((p, i) => new Rule($"extra[{i}]", new Regex(p.Pattern, Options), 0, 0, null, [], [])));
        return new SecretScanner([.. rules], Bundled.Value.Global, [.. tokens.Where(t => t.Length >= 8).Distinct()]);
    }

    /// <summary>Every secret in <paramref name="text"/>. <paramref name="path"/> is the file it came from, when it came from one.</summary>
    public IReadOnlyList<SecretMatch> Scan(string text, string? path = null)
    {
        var found = new List<SecretMatch>();
        foreach (var token in _tokens)
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                found.Add(new SecretMatch("rtfd-token", token));
            }
        }

        var lower = text.ToLowerInvariant();
        foreach (var rule in _rules)
        {
            if (rule.Keywords.Count > 0 && !rule.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal)))
            {
                continue;
            }

            if (rule.Path is not null && (path is null || !rule.Path.IsMatch(path)))
            {
                continue;
            }

            foreach (Match match in rule.Regex.Matches(text))
            {
                var secret = SecretOf(match, rule.SecretGroup);
                if (secret.Length == 0 || found.Any(f => f.Secret == secret))
                {
                    continue;
                }

                if (rule.Entropy > 0 && Entropy(secret) < rule.Entropy)
                {
                    continue;
                }

                // gitleaks' own extra check: generic rules need a digit.
                if (rule.Id.StartsWith("generic", StringComparison.Ordinal) && !secret.Any(char.IsAsciiDigit))
                {
                    continue;
                }

                var line = LineAround(text, match.Index, match.Length);
                if (_global.Allows(secret, match.Value, line, path) || rule.Allowlists.Any(a => a.Allows(secret, match.Value, line, path)))
                {
                    continue;
                }

                found.Add(new SecretMatch(rule.Id, secret));
            }
        }

        return found;
    }

    /// <summary>The first characters, then a marker: <c>ghp_…REDACTED</c>.</summary>
    public static string Redact(string secret) => (secret.Length > 12 ? secret[..4] : "") + "…REDACTED";

    /// <summary>gitleaks' rule: the secret group when set, else the first group that matched, else the whole match.</summary>
    private static string SecretOf(Match match, int secretGroup)
    {
        if (match.Groups.Count < 2)
        {
            return match.Value;
        }

        if (secretGroup > 0)
        {
            return secretGroup < match.Groups.Count ? match.Groups[secretGroup].Value : "";
        }

        for (var i = 1; i < match.Groups.Count; i++)
        {
            if (match.Groups[i].Length > 0)
            {
                return match.Groups[i].Value;
            }
        }

        return match.Value;
    }

    /// <summary>Shannon entropy in bits per character, as gitleaks computes it.</summary>
    public static double Entropy(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        return text.GroupBy(c => c)
            .Select(g => (double)g.Count() / text.Length)
            .Sum(p => -p * Math.Log2(p));
    }

    private static string LineAround(string text, int index, int length)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index + length);
        return text[start..(end < 0 ? text.Length : end)];
    }

    private sealed record Rule(string Id, Regex Regex, int SecretGroup, double Entropy, Regex? Path, IReadOnlyList<string> Keywords, IReadOnlyList<Allowlist> Allowlists);

    private sealed record Allowlist(bool All, string Target, IReadOnlyList<Regex> Regexes, IReadOnlyList<Regex> Paths, IReadOnlyList<string> Stopwords)
    {
        /// <summary>gitleaks' allowlist: any check passing allows the match, or all of them under <c>condition = "AND"</c>.</summary>
        public bool Allows(string secret, string match, string line, string? path)
        {
            var target = Target switch
            {
                "match" => match,
                "line" => line,
                _ => secret,
            };

            var checks = new List<bool>();
            if (Regexes.Count > 0)
            {
                checks.Add(Regexes.Any(r => r.IsMatch(target)));
            }

            if (Paths.Count > 0)
            {
                checks.Add(path is not null && Paths.Any(r => r.IsMatch(path)));
            }

            if (Stopwords.Count > 0)
            {
                var lower = secret.ToLowerInvariant();
                checks.Add(Stopwords.Any(w => lower.Contains(w, StringComparison.Ordinal)));
            }

            return checks.Count > 0 && (All ? checks.All(c => c) : checks.Any(c => c));
        }
    }

    private sealed record RuleSet(IReadOnlyList<Rule> Rules, Allowlist Global);

    private static RuleSet LoadBundled()
    {
        using var stream = typeof(SecretScanner).Assembly.GetManifestResourceStream("Rtfd.Resources.gitleaks-rules.json")
            ?? throw new InvalidOperationException("the bundled gitleaks rules are missing from the binary");
        var file = JsonSerializer.Deserialize(stream, SecretsJson.Default.RulesFile)
            ?? throw new InvalidOperationException("the bundled gitleaks rules are empty");

        static Allowlist Map(AllowlistData a) => new(
            a.Condition == "AND", a.RegexTarget,
            [.. a.Regexes.Select(r => new Regex(r, Options))], [.. a.Paths.Select(r => new Regex(r, Options))], a.Stopwords);

        // Specific rules first, so a GitHub token is reported as github-pat rather than generic-api-key.
        var rules = file.Rules
            .OrderBy(r => r.Id.StartsWith("generic", StringComparison.Ordinal))
            .Select(r => new Rule(
                r.Id, new Regex(r.Regex, Options), r.SecretGroup ?? 0, r.Entropy ?? 0,
                r.Path is null ? null : new Regex(r.Path, Options), r.Keywords, [.. r.Allowlists.Select(Map)]))
            .ToList();
        return new RuleSet(rules, Map(file.Allowlist));
    }
}

internal sealed record AllowlistData(string Condition, string RegexTarget, List<string> Regexes, List<string> Paths, List<string> Stopwords);

internal sealed record RuleData(string Id, string Regex, int? SecretGroup, double? Entropy, string? Path, List<string> Keywords, List<AllowlistData> Allowlists);

internal sealed record RulesFile(string Source, AllowlistData Allowlist, List<RuleData> Rules);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RulesFile))]
internal sealed partial class SecretsJson : JsonSerializerContext;
