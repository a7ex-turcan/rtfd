using Rtfd.Config;
using Rtfd.Providers;
using Rtfd.Runs;

namespace Rtfd.Tests.Support;

/// <summary>
/// Everything an end-to-end run needs, all local: a git remote, a temp home and data
/// directory, the fake agent posing as claude, and a provider serving the PR from the remote.
/// </summary>
public sealed class RunHarness : IDisposable
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

    public RunHarness()
    {
        Git = new GitFixture();
        Home = new TempHome();
        Agent = new FakeAgent(Path.Combine(Home.Root, "agent"));
        Token = Tokens.GitHubLike();
        _variables["GH_TOKEN"] = Token;
    }

    public GitFixture Git { get; }
    public TempHome Home { get; }
    public FakeAgent Agent { get; }

    /// <summary>The fake GitHub token in GH_TOKEN, shaped like a real one.</summary>
    public string Token { get; }

    public FakeProvider? Provider { get; private set; }
    public StringWriter Log { get; } = new();

    public string DataRoot => Path.Combine(Home.Root, "data");

    /// <summary>The test's variables, then the real ones (PATH, SystemRoot: what the allowlist lets through).</summary>
    public Func<string, string?> Environment => name => _variables.TryGetValue(name, out var value) ? value : System.Environment.GetEnvironmentVariable(name);

    public RunHarness Set(string name, string value)
    {
        _variables[name] = value;
        return this;
    }

    public string Yaml(string repoExtra = "", string agentExtra = "") => $"""
        defaults:
          data_dir: '{DataRoot}'
        agents:
          claude:
            account: test-account
            command: '{Agent.Command.Replace("'", "''")}'
        {agentExtra}
        repos:
          - name: repo
            provider: github
            repo: owner/repo
            trust: trusted
            agent: claude
            prompt: prompts/review.md
            authors: [owner]
            token_env: GH_TOKEN
        {repoExtra}
        """;

    public RunRequest Request(PullRequest pr, bool keep = false, bool inputsOnly = false, string repoExtra = "")
    {
        var loaded = Home.Load(Yaml(repoExtra));
        Assert.False(loaded.HasErrors, string.Join("\n", loaded.Diagnostics));
        var config = loaded.Config!;
        Provider = new FakeProvider(Git, pr);
        return new RunRequest
        {
            Config = config,
            Repo = config.Repos[0],
            Agent = config.Agents["claude"],
            Number = pr.Number,
            Keep = keep,
            InputsOnly = inputsOnly,
            Environment = Environment,
            Providers = (_, token) =>
            {
                Provider.Token = token;
                return Provider;
            },
            RtfdExecutable = "rtfd-test-binary",
            Log = Log,
            Delay = _ => Task.CompletedTask,
        };
    }

    public Task<RunReport> RunAsync(RunRequest request) => ReviewRun.RunAsync(request, TestContext.Current.CancellationToken);

    public void Dispose()
    {
        Home.Dispose();
        Git.Dispose();
    }
}

/// <summary>Token-shaped strings built at run time, so no token-shaped literal sits in the repo.</summary>
public static class Tokens
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Like a classic GitHub token: ghp_ and 36 random letters and digits.</summary>
    public static string GitHubLike() => "ghp" + "_" + Random(36);

    public static string Random(int length) =>
        new([.. Enumerable.Range(0, length).Select(_ => Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)])]);
}
