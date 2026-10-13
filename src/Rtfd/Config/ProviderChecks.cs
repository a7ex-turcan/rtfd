using Rtfd.Providers;

namespace Rtfd.Config;

/// <param name="Identity">Who the token posts as, when it worked.</param>
/// <param name="IsPublic">The repo's visibility, when the provider answered.</param>
/// <param name="Skipped">Why the repo wasn't checked, when it wasn't.</param>
public sealed record RepoReport(RepoConfig Repo, string? Identity, bool? IsPublic, string? Skipped);

/// <summary>
/// The checks that need the provider (plan Phase 2): each repo's token works and says who rtfd
/// posts as, the repo is reachable with it, and a public repo doesn't let everyone's PRs reach
/// the agent while the untrusted tier doesn't exist (spec "Policy").
/// </summary>
public static class ProviderChecks
{
    public static async Task<(IReadOnlyList<RepoReport> Repos, IReadOnlyList<Diagnostic> Diagnostics)> RunAsync(
        RtfdConfig config, Func<string, string?> environment, Func<RepoConfig, ProviderToken, IProvider> providers,
        CancellationToken cancellationToken = default)
    {
        var reports = new List<RepoReport>();
        var diagnostics = new List<Diagnostic>();
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var repo in config.Repos)
        {
            var token = environment(repo.TokenEnv);
            if (string.IsNullOrEmpty(token))
            {
                reports.Add(new RepoReport(repo, null, null, $"{repo.TokenEnv} isn't set"));
                continue;
            }

            void Error(string check, string message, string key) =>
                diagnostics.Add(new(check, DiagnosticLevel.Error, $"{repo.Name}: {message}", $"{repo.Path}.{key}", config.LineOf($"{repo.Path}.{key}")));

            if (refused.TryGetValue(token, out var earlier))
            {
                Error("provider.token_refused", earlier, "token_env");
                reports.Add(new RepoReport(repo, null, null, "its token was refused"));
                continue;
            }

            var provider = providers(repo, new ProviderToken(token));
            try
            {
                if (!identities.TryGetValue(token, out var identity))
                {
                    try
                    {
                        identities[token] = identity = await provider.WhoAmIAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (ProviderException ex)
                    {
                        refused[token] = $"the token in {repo.TokenEnv} doesn't work: {ex.Message}";
                        Error("provider.token_refused", refused[token], "token_env");
                        reports.Add(new RepoReport(repo, null, null, "its token was refused"));
                        continue;
                    }
                }

                RepositoryInfo info;
                try
                {
                    info = await provider.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ProviderException ex)
                {
                    Error("provider.repo_unreachable", $"{repo.Slug} can't be read with the token in {repo.TokenEnv}: {ex.Message}", "repo");
                    reports.Add(new RepoReport(repo, identity, null, "the repo can't be read"));
                    continue;
                }

                if (info.IsPublic && repo.Authors.Contains("*"))
                {
                    Error("config.authors_everyone_public",
                        $"authors: [\"*\"] on a public repo lets anyone's PR text and code reach the agent. List the authors you trust; the untrusted tier that would make everyone safe doesn't exist yet.",
                        "authors");
                }

                reports.Add(new RepoReport(repo, identity, info.IsPublic, null));
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }
        }

        return (reports, diagnostics);
    }
}
