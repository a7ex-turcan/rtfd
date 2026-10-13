using Rtfd.Processes;

namespace Rtfd.Git;

/// <summary>
/// An HTTP header that lets git fetch from the provider. It reaches git through
/// <c>GIT_CONFIG_*</c> variables on the fetch command only: never an argument (process lists
/// show those) and never the mirror's config.
/// </summary>
public sealed class GitCredential(string header)
{
    internal string Header { get; } = header;

    public override string ToString() => "[credential]";
}

public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Truncated { get; init; }
    public bool TimedOut { get; init; }

    public bool Ok => ExitCode == 0 && !TimedOut;
}

public sealed class GitException(string message) : Exception(message);

/// <summary>
/// The one place git is started (plan Phase 1a.2). The environment is built from scratch: the
/// allowlist, plus variables that ignore the system and global config, so no filter, textconv,
/// external diff, pager, credential helper or fsmonitor from the user's setup can run. Every
/// call also turns off hooks, fsmonitor and symlinks on the command line, in case a mirror's own
/// config is ever tampered with.
/// </summary>
public sealed class GitRunner
{
    private readonly string _globalConfig;
    private readonly string _hooksFolder;
    private readonly Func<string, string?> _environment;
    private readonly IReadOnlyCollection<string> _secrets;

    /// <param name="stateFolder">Where the empty global config and the empty hooks folder live, <c>&lt;data&gt;/git</c>.</param>
    /// <param name="environment">Reads the variables the allowlist names.</param>
    /// <param name="secrets">Token values; a variable holding one never reaches git.</param>
    public GitRunner(string stateFolder, Func<string, string?> environment, IReadOnlyCollection<string> secrets)
    {
        _globalConfig = Path.Combine(stateFolder, "empty.gitconfig");
        _hooksFolder = Path.Combine(stateFolder, "no-hooks");
        _environment = environment;
        _secrets = secrets;

        Directory.CreateDirectory(_hooksFolder);
        if (!File.Exists(_globalConfig) || new FileInfo(_globalConfig).Length > 0)
        {
            File.WriteAllText(_globalConfig, "");
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(_hooksFolder))
        {
            throw new GitException($"{_hooksFolder} must be empty, and it holds {Path.GetFileName(entry)}");
        }
    }

    public string GlobalConfigPath => _globalConfig;
    public string HooksFolder => _hooksFolder;

    public async Task<GitResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, GitCredential? credential = null, string? stdin = null,
        TimeSpan? timeout = null, int maxOutputChars = 64 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(new ProcessRequest
        {
            FileName = "git",
            Arguments = [.. Prefix(), .. arguments],
            Environment = Environment(credential),
            WorkingDirectory = workingDirectory,
            Stdin = stdin,
            Timeout = timeout ?? TimeSpan.FromMinutes(2),
            MaxOutputChars = maxOutputChars,
        }, cancellationToken).ConfigureAwait(false);

        if (result.StartError is not null)
        {
            throw new GitException(result.StartError);
        }

        return new GitResult(result.ExitCode, result.Stdout, result.Stderr)
        {
            Truncated = result.StdoutTruncated,
            TimedOut = result.TimedOut,
        };
    }

    /// <summary>Runs git and returns stdout, or throws with git's own message.</summary>
    public async Task<string> RunCheckedAsync(
        string workingDirectory, IReadOnlyList<string> arguments, GitCredential? credential = null, string? stdin = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workingDirectory, arguments, credential, stdin, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new GitException($"git {arguments[0]} timed out");
        }

        if (result.ExitCode != 0)
        {
            var message = result.Stderr.Trim();
            throw new GitException($"git {arguments[0]} failed ({result.ExitCode}){(message.Length > 0 ? ": " + message : "")}");
        }

        return result.Stdout;
    }

    /// <summary>Options on every call. Public so the tests can check them.</summary>
    public IReadOnlyList<string> Prefix() =>
    [
        "--no-pager",
        "-c", $"core.hooksPath={_hooksFolder}",
        "-c", "core.fsmonitor=false",
        "-c", "core.longpaths=true",
        "-c", "core.symlinks=false",
        "-c", "core.quotePath=false",
        "-c", "credential.helper=",
    ];

    /// <summary>The whole environment git gets.</summary>
    public Dictionary<string, string> Environment(GitCredential? credential)
    {
        var environment = ChildEnvironment.Build(_environment, _secrets);
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = _globalConfig;
        environment["GIT_ATTR_NOSYSTEM"] = "1";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_LITERAL_PATHSPECS"] = "1";
        if (credential is not null)
        {
            environment["GIT_CONFIG_COUNT"] = "1";
            environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            environment["GIT_CONFIG_VALUE_0"] = credential.Header;
        }

        return environment;
    }
}
