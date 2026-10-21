using System.Globalization;
using System.Text.Json;
using Rtfd.Config;
using Rtfd.Processes;

namespace Rtfd.Providers;

/// <summary>
/// Where a repo's token comes from (CLAUDE.md rule 1): the variable <c>token_env</c> names, read
/// once, or the Azure CLI, asked again before each token expires. Either way the token stays in
/// rtfd's memory: never logged, never on a command line, never on disk, never handed to a child.
/// </summary>
public abstract class TokenSource
{
    /// <summary>Where the token comes from, for messages: the variable's name, or the Azure CLI. Never the token.</summary>
    public abstract string Describe { get; }

    /// <summary>
    /// True when the token is read once, so a refused one stays refused until rtfd restarts. The
    /// Azure CLI is asked again, so logging in again fixes it without a restart.
    /// </summary>
    public abstract bool ReadOnce { get; }

    /// <summary>The token handed out last, for the secret check; null before the first.</summary>
    public abstract ProviderToken? Current { get; }

    /// <summary>
    /// A token good for a few minutes more. <paramref name="renew"/>, after a 401, asks for a new
    /// one where that can help. Throws a <see cref="ProviderException"/> with
    /// <see cref="ProviderException.NoToken"/> when there is none to give.
    /// </summary>
    public abstract Task<ProviderToken> GetAsync(bool renew = false, CancellationToken cancellationToken = default);
}

/// <summary>A token from the variable <c>token_env</c> names, read once when the source is made.</summary>
public sealed class EnvironmentTokenSource : TokenSource
{
    private readonly ProviderToken? _token;

    public EnvironmentTokenSource(string name, Func<string, string?> environment)
    {
        Describe = name;
        var value = environment(name);
        _token = string.IsNullOrEmpty(value) ? null : new ProviderToken(value);
    }

    public override string Describe { get; }

    public override bool ReadOnce => true;

    public override ProviderToken? Current => _token;

    public override Task<ProviderToken> GetAsync(bool renew = false, CancellationToken cancellationToken = default) =>
        _token is { } token
            ? Task.FromResult(token)
            : throw new ProviderException($"{Describe} isn't set. It should hold the token rtfd reads and posts with.") { NoToken = true };
}

/// <summary>What the Azure CLI printed for <c>az account get-access-token</c>.</summary>
public sealed record AzureCliAnswer(int ExitCode, string Stdout, string Stderr, string? StartError = null);

/// <summary>
/// An Entra ID token for Azure DevOps from the Azure CLI (<c>auth: azure_cli</c>): rtfd runs
/// <c>az account get-access-token</c> for the Azure DevOps resource and the configured tenant,
/// keeps the token in memory, and runs it again five minutes before the token expires (about an
/// hour). Organizations that only issue packaging-scoped PATs leave no other way for a
/// long-running <c>serve</c>. az is a Python program; on Windows rtfd starts its Python directly,
/// since <see cref="ProcessRunner"/> refuses batch files, and the arguments are rtfd's own.
/// </summary>
public sealed class AzureCliTokenSource : TokenSource
{
    /// <summary>Azure DevOps' resource id in Entra ID, the same for every organization.</summary>
    public const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";

    /// <summary>A token this close to expiring is renewed first: a fetch or a review must not outlive it.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly string? _tenant;
    private readonly TimeProvider _clock;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<AzureCliAnswer>> _run;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProviderToken? _token;
    private DateTimeOffset _expires;

    /// <param name="tenant">The tenant id or domain; null for the CLI's default.</param>
    /// <param name="run">Runs az with these arguments; tests pass a fake.</param>
    public AzureCliTokenSource(string? tenant, TimeProvider clock, Func<IReadOnlyList<string>, CancellationToken, Task<AzureCliAnswer>> run)
    {
        _tenant = tenant;
        _clock = clock;
        _run = run;
    }

    public override string Describe => $"the Azure CLI{(_tenant is { } tenant ? $" (tenant {tenant})" : "")}";

    public override bool ReadOnce => false;

    public override ProviderToken? Current => _token;

    /// <summary>When the token in hand expires, for the tests.</summary>
    public DateTimeOffset Expires => _expires;

    /// <summary>The arguments after <c>az</c>: fixed, the tenant checked by the config loader.</summary>
    public IReadOnlyList<string> Arguments =>
        ["account", "get-access-token", "--resource", AzureDevOpsResource, .. _tenant is { } tenant ? (string[])["--tenant", tenant] : [], "--output", "json"];

    public override async Task<ProviderToken> GetAsync(bool renew = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!renew && _token is { } held && _expires - _clock.GetUtcNow() > RenewBefore)
            {
                return held;
            }

            var answer = await _run(Arguments, cancellationToken).ConfigureAwait(false);
            if (answer.StartError is { } startError)
            {
                throw new ProviderException($"{Describe} can't run: {startError}. Install it, or check where rtfd looks for it.") { NoToken = true };
            }

            if (answer.ExitCode != 0)
            {
                // stderr says why (not logged in, consent, a wrong tenant); stdout is never shown, it may hold a token.
                var why = answer.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {answer.ExitCode}";
                var login = _tenant is { } tenant ? $"az login --tenant {tenant}" : "az login";
                throw new ProviderException($"{Describe} gave no token: {why} Log in with `{login}`; rtfd asks again on its next try.") { NoToken = true };
            }

            var (value, expires) = Parse(answer.Stdout, _clock.GetUtcNow());
            _token = new ProviderToken(value);
            _expires = expires;
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The token and when it expires: <c>expires_on</c> (Unix seconds) where the CLI gives it, else <c>expiresOn</c> (local time).</summary>
    private (string Token, DateTimeOffset Expires) Parse(string stdout, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var token = root.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrEmpty(token))
            {
                throw new ProviderException($"{Describe} answered without a token.") { NoToken = true };
            }

            if (root.TryGetProperty("expires_on", out var epoch)
                && (epoch.ValueKind == JsonValueKind.Number ? epoch.TryGetInt64(out var seconds)
                    : long.TryParse(epoch.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)))
            {
                return (token, DateTimeOffset.FromUnixTimeSeconds(seconds));
            }

            if (root.TryGetProperty("expiresOn", out var local) && local.ValueKind == JsonValueKind.String
                && DateTime.TryParse(local.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
            {
                return (token, new DateTimeOffset(at));
            }

            // No expiry given: assume a short life rather than a long one.
            return (token, now + TimeSpan.FromMinutes(30));
        }
        catch (JsonException)
        {
            throw new ProviderException($"{Describe} answered with something rtfd can't read.") { NoToken = true };
        }
    }

    /// <summary>
    /// Runs the real az with an allowlisted environment, plus <c>AZURE_CONFIG_DIR</c> where it is
    /// set so a moved login is found. On Windows the MSI's <c>az.cmd</c> only runs
    /// <c>..\python.exe -IBm azure.cli</c>, so rtfd runs that itself.
    /// </summary>
    public static Func<IReadOnlyList<string>, CancellationToken, Task<AzureCliAnswer>> RealRunner(Func<string, string?> environment) =>
        async (arguments, cancellationToken) =>
        {
            var childEnvironment = ChildEnvironment.Build(environment, [], ["AZURE_CONFIG_DIR"]);
            var (file, prefix) = Locate(childEnvironment, environment);
            if (file is null)
            {
                return new AzureCliAnswer(-1, "", "", "az isn't on PATH or in the usual install folder");
            }

            var result = await ProcessRunner.RunAsync(new ProcessRequest
            {
                FileName = file,
                Arguments = [.. prefix, .. arguments],
                Environment = childEnvironment,
                WorkingDirectory = Path.GetTempPath(),
                Timeout = TimeSpan.FromMinutes(1),
            }, cancellationToken).ConfigureAwait(false);
            return result.TimedOut
                ? new AzureCliAnswer(-1, "", "az didn't answer within a minute")
                : new AzureCliAnswer(result.ExitCode, result.Stdout, result.Stderr, result.StartError);
        };

    /// <summary>What to start for az: its Python with <c>-IBm azure.cli</c> on Windows, the <c>az</c> script elsewhere.</summary>
    internal static (string? File, IReadOnlyList<string> Prefix) Locate(IReadOnlyDictionary<string, string> childEnvironment, Func<string, string?> environment)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (ProcessRunner.Resolve("az", childEnvironment), []);
        }

        var folders = (childEnvironment.GetValueOrDefault("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(new[] { environment("ProgramFiles"), environment("ProgramFiles(x86)") }.OfType<string>()
                .Select(p => Path.Combine(p, "Microsoft SDKs", "Azure", "CLI2", "wbin")));
        foreach (var folder in folders.Where(Path.IsPathFullyQualified))
        {
            var python = Path.GetFullPath(Path.Combine(folder, "..", "python.exe"));
            if (File.Exists(Path.Combine(folder, "az.cmd")) && File.Exists(python))
            {
                return (python, ["-IBm", "azure.cli"]);
            }
        }

        return (null, []);
    }
}

/// <summary>
/// Every repo's token source, one per variable and one per Azure CLI tenant, shared by the
/// repos that use it: so serve asks az once an hour per tenant, and the secret check knows
/// every token rtfd holds.
/// </summary>
public sealed class TokenSources(Func<string, string?> environment, Func<string?, TokenSource> azureCli)
{
    private readonly Dictionary<string, TokenSource> _sources = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>The real sources: variables from <paramref name="environment"/>, and the real Azure CLI.</summary>
    public static TokenSources Real(Func<string, string?> environment, TimeProvider clock) =>
        new(environment, tenant => new AzureCliTokenSource(tenant, clock, AzureCliTokenSource.RealRunner(environment)));

    public TokenSource For(RepoConfig repo)
    {
        var key = repo.Auth == TokenAuth.AzureCli ? $"az:{repo.Tenant?.ToLowerInvariant()}" : $"env:{repo.TokenEnv}";
        lock (_lock)
        {
            if (!_sources.TryGetValue(key, out var source))
            {
                _sources[key] = source = repo.Auth == TokenAuth.AzureCli ? azureCli(repo.Tenant) : new EnvironmentTokenSource(repo.TokenEnv!, environment);
            }

            return source;
        }
    }

    /// <summary>
    /// Every token value rtfd holds now, for the secret check and the environment filter: each
    /// repo's variable, and each token the Azure CLI has handed over so far.
    /// </summary>
    public IReadOnlyList<string> Known(RtfdConfig config) =>
        [.. config.Repos.Select(For).Select(s => s.Current?.Value).OfType<string>().Where(t => t.Length > 0).Distinct()];
}
