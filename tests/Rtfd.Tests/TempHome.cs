using Rtfd.Config;

namespace Rtfd.Tests;

/// <summary>
/// A throwaway rtfd home under the temp directory, so no test touches the real <c>~/.rtfd</c>
/// (CLAUDE.md rule 5). Its <see cref="Environment"/> answers <c>RTFD_HOME</c> with this folder,
/// plus whatever a test sets, and nothing from the real environment.
/// </summary>
public sealed class TempHome : IDisposable
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

    public TempHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "rtfd-t", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        _variables[RtfdHome.EnvironmentVariable] = Root;
    }

    public string Root { get; }

    public RtfdHome Home => new(Root);

    public string ConfigPath => Path.Combine(Root, RtfdHome.ConfigFileName);

    public Func<string, string?> Environment => name => _variables.GetValueOrDefault(name);

    public TempHome Set(string name, string value)
    {
        _variables[name] = value;
        return this;
    }

    /// <summary>Writes <c>rtfd.yaml</c>, and the prompt it is likely to name.</summary>
    public string WriteConfig(string yaml, bool withPrompt = true)
    {
        File.WriteAllText(ConfigPath, yaml);
        if (withPrompt)
        {
            Directory.CreateDirectory(Path.Combine(Root, "prompts"));
            File.WriteAllText(Path.Combine(Root, "prompts", "review.md"), "Review the PR.");
        }

        return ConfigPath;
    }

    public LoadResult Load(string yaml, bool withPrompt = true)
    {
        WriteConfig(yaml, withPrompt);
        return ConfigLoader.LoadFile(ConfigPath, Home);
    }

    /// <summary>Everything <c>rtfd validate</c> would report: loading, then the checks.</summary>
    public IReadOnlyList<Diagnostic> Validate(string yaml, bool withPrompt = true)
    {
        var loaded = Load(yaml, withPrompt);
        return loaded.Config is null ? loaded.Diagnostics : [.. loaded.Diagnostics, .. ConfigChecks.Run(loaded.Config, Environment)];
    }

    /// <summary>The 1-based line of the config that first contains <paramref name="text"/>.</summary>
    public int LineOf(string text) =>
        File.ReadAllLines(ConfigPath).Select((line, i) => (line, i)).First(x => x.line.Contains(text, StringComparison.Ordinal)).i + 1;

    public (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EntryPoint.Run(args, stdout, stderr, Environment);
        return (code, stdout.ToString(), stderr.ToString());
    }

    public void Dispose()
    {
        try
        {
            // git writes its objects read-only.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the temp directory gets cleaned eventually.
        }
    }
}

/// <summary>Configs for tests to start from and change one thing in.</summary>
public static class Configs
{
    public const string Agent = """
        agents:
          claude:
            account: claude-personal
            command: claude -p --permission-mode dontAsk --output-format json
        """;

    public const string Repo = """
          - name: nutrx
            provider: github
            repo: owner/nutrx
            trust: trusted
            agent: claude
            prompt: prompts/review.md
            authors: [owner]
            token_env: GH_TOKEN
        """;

    /// <summary>One claude agent and one repo, valid as they stand.</summary>
    public static string Minimal(string repoExtra = "", string agentExtra = "", string defaults = "") =>
        (defaults.Length > 0 ? $"defaults:\n{defaults}\n" : "")
        + Agent + "\n" + agentExtra + (agentExtra.Length > 0 ? "\n" : "")
        + "repos:\n" + Repo + "\n" + repoExtra + "\n";

    /// <summary>The repository's own root, found from the test binary's folder.</summary>
    public static string RepositoryRoot
    {
        get
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "rtfd.slnx")))
            {
                folder = folder.Parent;
            }

            return folder?.FullName ?? throw new InvalidOperationException("rtfd.slnx not found above the test binary");
        }
    }
}
