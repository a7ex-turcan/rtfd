using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Rtfd.Config;

public sealed record LoadResult(RtfdConfig? Config, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.IsError);
}

/// <summary>
/// Loads <c>rtfd.yaml</c> by hand, walking YamlDotNet's node model as rtfq's loader does rather
/// than using its reflection-based deserializer. That one doesn't survive Native AOT, and mapping
/// by hand is what turns a mistake into "line 14: unknown key 'promt'".
/// </summary>
/// <remarks>
/// Loading reports what is malformed: shapes, types, unknown keys and values, missing required
/// keys, and references between sections. Whether this build supports what a well-formed config
/// asks for is <see cref="ConfigChecks"/>' job. The config comes back even with errors, so the
/// checks can report on everything at once.
/// </remarks>
public static partial class ConfigLoader
{
    public static LoadResult LoadFile(string path, RtfdHome home)
    {
        path = Path.GetFullPath(path);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, [new("config.unreadable", DiagnosticLevel.Error, ex.Message)]);
        }

        return LoadText(text, path, home);
    }

    public static LoadResult LoadText(string text, string path, RtfdHome home)
    {
        var diagnostics = new List<Diagnostic>();
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);

        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0)
            {
                return new(null, [new("config.empty", DiagnosticLevel.Error, "the config file is empty")]);
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode map)
            {
                return new(null, [new("config.malformed", DiagnosticLevel.Error, "the top level of the config must be a mapping")]);
            }

            root = map;
        }
        catch (YamlException ex)
        {
            return new(null, [new("config.malformed", DiagnosticLevel.Error, ex.Message, "", (int)ex.Start.Line)]);
        }

        var folder = Path.GetDirectoryName(path)!;
        var cursor = new MapCursor(root, "", diagnostics, lines);

        var defaults = cursor.Map("defaults");
        var dataDir = defaults?.String("data_dir");
        var secrets = ReadSecrets(defaults?.Map("secrets"));
        var settings = ReadSettings(defaults, new RepoSettings());
        defaults?.ReportUnknownKeys();

        var agents = ReadAgents(cursor.Map("agents"));
        var repos = ReadRepos(cursor, settings, agents, folder, diagnostics, lines);
        cursor.ReportUnknownKeys();

        var config = new RtfdConfig
        {
            FilePath = path,
            Data = new DataDirectory(dataDir is null ? home.Root : RtfdHome.ResolvePath(dataDir, folder)),
            Secrets = secrets,
            Agents = agents,
            Repos = repos,
            Lines = lines,
        };
        return new(config, diagnostics);
    }

    // --- defaults ---------------------------------------------------------

    private static SecretsSettings ReadSecrets(MapCursor? c)
    {
        if (c is null)
        {
            return new SecretsSettings();
        }

        var rules = c.String("rules") ?? "gitleaks";
        if (rules != "gitleaks")
        {
            c.Report("config.value_unknown", $"'{rules}' is not a rule set rtfd knows; the bundled one is gitleaks", "rules");
        }

        var extra = ReadPatterns(c, "extra") ?? [];
        c.ReportUnknownKeys();
        return new SecretsSettings { Rules = rules, Extra = extra };
    }

    /// <summary>Reads the keys a repo can take from <c>defaults</c> or set itself, over <paramref name="inherited"/>.</summary>
    private static RepoSettings ReadSettings(MapCursor? c, RepoSettings inherited)
    {
        if (c is null)
        {
            return inherited;
        }

        var interval = c.Duration("interval");
        if (interval is { } i && i <= TimeSpan.Zero)
        {
            c.Report("config.interval_zero", "'interval' must be more than zero", "interval");
        }

        return inherited with
        {
            Interval = interval ?? inherited.Interval,
            QuietPeriod = c.Duration("quiet_period") ?? inherited.QuietPeriod,
            CiMaxWait = c.Duration("ci_max_wait") ?? inherited.CiMaxWait,
            MaxAttempts = c.PositiveInt("max_attempts") ?? inherited.MaxAttempts,
            Timeout = c.Duration("timeout") ?? inherited.Timeout,
            MaxDiffLines = c.PositiveInt("max_diff_lines") ?? inherited.MaxDiffLines,
            Exclude = c.StringList("exclude") ?? inherited.Exclude,
            NotReadyIfOpen = ReadVerdictRule(c.Map("verdict_rule")) ?? inherited.NotReadyIfOpen,
            HumanResolveCloses = c.Bool("human_resolve_closes") ?? inherited.HumanResolveCloses,
            ContextTools = c.StringList("context_tools") ?? inherited.ContextTools,
        };
    }

    private static IReadOnlyList<FindingSeverity>? ReadVerdictRule(MapCursor? c)
    {
        if (c is null)
        {
            return null;
        }

        var names = c.StringList("not_ready_if_open");
        c.ReportUnknownKeys();
        return names is null ? null : ParseAll(c, "not_ready_if_open", names, Severities);
    }

    // --- agents -----------------------------------------------------------

    private static Dictionary<string, AgentConfig> ReadAgents(MapCursor? section)
    {
        var agents = new Dictionary<string, AgentConfig>(StringComparer.Ordinal);
        if (section is null)
        {
            return agents;
        }

        foreach (var name in section.Keys())
        {
            if (section.Map(name) is not { } a)
            {
                continue;
            }

            var account = Required(a, "account");
            var commandText = Required(a, "command");
            var model = a.String("model");
            var effort = a.String("effort");
            var outputSection = a.Map("output");
            var isolation = ReadNamedLists(a.Map("isolation"));
            var tools = ReadNamedLists(a.Map("tools"));
            var projectConfigSection = a.Map("project_config");
            var limitPatterns = ReadPatterns(a, "limit_patterns");

            var command = commandText is null ? null : CommandSplitter.Split(commandText);
            if (commandText is not null && command is null or [])
            {
                a.Report("config.command_unparseable", "the command is empty or leaves a quote open", "command");
                command = null;
            }

            var kind = command is null ? AgentKind.Unknown : CommandSplitter.KindOf(command[0]);
            var output = ReadOutput(outputSection, kind);
            var projectConfig = ReadProjectConfig(projectConfigSection, kind);
            a.ReportUnknownKeys();

            if (account is null || command is null)
            {
                continue;
            }

            agents[name] = new AgentConfig
            {
                Name = name,
                Path = a.Path,
                Kind = kind,
                Account = account,
                Command = command,
                Model = model,
                Effort = effort,
                Output = output,
                Isolation = isolation,
                Tools = tools,
                ProjectConfig = projectConfig,
                LimitPatterns = limitPatterns ?? BuiltIn.LimitPatterns(kind, a.Join("limit_patterns")),
            };
        }

        return agents;
    }

    private static AgentOutput ReadOutput(MapCursor? c, AgentKind kind)
    {
        if (c is null)
        {
            return BuiltIn.Output(kind);
        }

        var fromText = Required(c, "from");
        var extract = c.String("extract");
        c.ReportUnknownKeys();

        var from = fromText is null ? null : ParseOne(c, "from", fromText, OutputSources);
        if (extract is not null && from == OutputSource.File)
        {
            c.Report("config.extract_needs_stdout", "'extract' only applies to output from stdout", "extract");
        }

        if (extract is not null && !JsonPath().IsMatch(extract))
        {
            c.Report("config.extract_invalid", "'extract' must be a JSON path such as .result", "extract");
        }

        return new AgentOutput(from ?? OutputSource.Stdout, extract);
    }

    /// <summary>The built-in paths for the agent's CLI plus any the config adds. A config can't remove one.</summary>
    private static ProjectConfigPaths ReadProjectConfig(MapCursor? c, AgentKind kind)
    {
        var builtIn = BuiltIn.ProjectConfig(kind);
        if (c is null)
        {
            return builtIn;
        }

        var executable = c.StringList("executable") ?? [];
        var instructions = c.StringList("instructions") ?? [];
        c.ReportUnknownKeys();
        return new([.. builtIn.Executable.Union(executable)], [.. builtIn.Instructions.Union(instructions)]);
    }

    /// <summary>A mapping of setting names to a value or a list, kept as written for the checks to judge.</summary>
    private static Dictionary<string, IReadOnlyList<string>> ReadNamedLists(MapCursor? c)
    {
        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (c is null)
        {
            return values;
        }

        foreach (var key in c.Keys())
        {
            values[key] = c.ValueOrList(key) ?? [];
        }

        return values;
    }

    // --- repos ------------------------------------------------------------

    private static List<RepoConfig> ReadRepos(
        MapCursor root, RepoSettings defaults, Dictionary<string, AgentConfig> agents, string folder,
        List<Diagnostic> diagnostics, Dictionary<string, int> lines)
    {
        var repos = new List<RepoConfig>();
        var sections = root.MapList("repos");
        foreach (var r in sections)
        {
            var name = Required(r, "name");
            var providerText = Required(r, "provider");
            var slug = Required(r, "repo");
            var trustText = Required(r, "trust");
            var agent = Required(r, "agent");
            var prompt = Required(r, "prompt");
            var authors = r.StringList("authors");
            var verdictNames = r.StringList("verdicts");
            var tokenEnv = Required(r, "token_env");
            var hooks = r.MapList("hooks").Select(ReadHook).ToList();
            var settings = ReadSettings(r, defaults);
            r.ReportUnknownKeys();

            var provider = providerText is null ? null : ParseOne(r, "provider", providerText, Providers);
            var trust = trustText is null ? null : ParseOne(r, "trust", trustText, Trusts);
            var verdicts = verdictNames is null ? [Verdict.Comment] : ParseAll(r, "verdicts", verdictNames, Verdicts);

            if (name is not null && !RepoName().IsMatch(name))
            {
                r.Report("config.repo_name_invalid", "a repo name is lowercase letters, digits, '.', '-' and '_', because it names folders", "name");
            }

            if (slug is not null && !Slug().IsMatch(slug))
            {
                r.Report("config.repo_slug_invalid", "'repo' must be owner/name", "repo");
            }

            if (authors is null or [])
            {
                r.Report("config.authors_missing", "'authors' must list whose PRs to review; [\"*\"] means everyone", "authors");
            }

            if (verdictNames is [])
            {
                r.Report("config.verdicts_empty", "'verdicts' must allow at least comment", "verdicts");
            }

            if (agent is not null && !agents.ContainsKey(agent))
            {
                r.Report("config.agent_unknown", $"no agent named '{agent}' under 'agents'", "agent");
            }

            if (name is null || provider is null || slug is null || trust is null || agent is null || prompt is null || tokenEnv is null)
            {
                continue;
            }

            repos.Add(new RepoConfig
            {
                Name = name,
                Path = r.Path,
                Provider = provider.Value,
                Slug = slug,
                Trust = trust.Value,
                Agent = agent,
                PromptPath = RtfdHome.ResolvePath(prompt, folder),
                Authors = authors ?? [],
                Verdicts = verdicts,
                TokenEnv = tokenEnv,
                Hooks = hooks,
                Settings = settings,
            });
        }

        foreach (var repeat in repos.GroupBy(r => r.Name, StringComparer.Ordinal).SelectMany(g => g.Skip(1)))
        {
            var path = $"{repeat.Path}.name";
            diagnostics.Add(new("config.repo_name_duplicate", DiagnosticLevel.Error,
                $"another repo is already called '{repeat.Name}'", path, lines.GetValueOrDefault(path)));
        }

        if (sections.Count == 0)
        {
            diagnostics.Add(new("config.no_repos", DiagnosticLevel.Error, "the config has no repos to watch; add them under 'repos'"));
        }

        return repos;
    }

    private static HookConfig ReadHook(MapCursor h)
    {
        var hook = new HookConfig(h.String("name"), h.String("command"));
        h.ReportUnknownKeys();
        return hook;
    }

    // --- values -----------------------------------------------------------

    private static string? Required(MapCursor c, string key)
    {
        var value = c.String(key);
        if (value is null)
        {
            c.Report("config.required", $"'{key}' is required", key);
        }

        return value;
    }

    /// <summary>Regular expressions, compiled now so a bad one fails at load rather than mid-review.</summary>
    private static IReadOnlyList<ConfigPattern>? ReadPatterns(MapCursor c, string key)
    {
        if (c.StringList(key) is not { } texts)
        {
            return null;
        }

        var patterns = new List<ConfigPattern>();
        foreach (var (text, index) in texts.Select((t, i) => (t, i)))
        {
            var path = $"{c.Join(key)}[{index}]";
            try
            {
                // NonBacktracking: these run over agent output, which is untrusted (CLAUDE.md).
                _ = new Regex(text, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
                patterns.Add(new ConfigPattern(text, path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                c.Report("config.pattern_invalid", $"pattern {index + 1} doesn't compile: {ex.Message}", key);
            }
        }

        return patterns;
    }

    private static T? ParseOne<T>(MapCursor c, string key, string text, IReadOnlyDictionary<string, T> names)
        where T : struct
    {
        if (names.TryGetValue(text, out var value))
        {
            return value;
        }

        c.Report("config.value_unknown", $"'{text}' is not one of: {string.Join(", ", names.Keys)}", key);
        return null;
    }

    private static List<T> ParseAll<T>(MapCursor c, string key, IReadOnlyList<string> texts, IReadOnlyDictionary<string, T> names)
        where T : struct =>
        [.. texts.Select(t => ParseOne(c, key, t, names)).OfType<T>()];

    private static readonly Dictionary<string, ProviderKind> Providers = new(StringComparer.Ordinal)
    {
        ["github"] = ProviderKind.GitHub,
        ["bitbucket"] = ProviderKind.Bitbucket,
    };

    private static readonly Dictionary<string, TrustTier> Trusts = new(StringComparer.Ordinal)
    {
        ["trusted"] = TrustTier.Trusted,
        ["untrusted"] = TrustTier.Untrusted,
    };

    private static readonly Dictionary<string, Verdict> Verdicts = new(StringComparer.Ordinal)
    {
        ["comment"] = Verdict.Comment,
        ["approve"] = Verdict.Approve,
        ["request_changes"] = Verdict.RequestChanges,
    };

    private static readonly Dictionary<string, FindingSeverity> Severities = new(StringComparer.Ordinal)
    {
        ["should"] = FindingSeverity.Should,
        ["could"] = FindingSeverity.Could,
        ["minor"] = FindingSeverity.Minor,
    };

    private static readonly Dictionary<string, OutputSource> OutputSources = new(StringComparer.Ordinal)
    {
        ["stdout"] = OutputSource.Stdout,
        ["file"] = OutputSource.File,
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex RepoName();

    [GeneratedRegex("^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"^(\.[A-Za-z_][A-Za-z0-9_]*)+$")]
    private static partial Regex JsonPath();
}
