using Rtfd.Providers;

namespace Rtfd.Config;

/// <param name="Identity">Who the token posts as, when it worked.</param>
/// <param name="IsPublic">The repo's visibility, when the provider answered.</param>
/// <param name="Skipped">Why the repo wasn't checked, when it wasn't.</param>
public sealed record RepoReport(RepoConfig Repo, string? Identity, bool? IsPublic, string? Skipped);

/// <summary>
/// The checks that need the provider (plan Phase 2): each repo's token works and says who rtfd
/// posts as, the repo is reachable with it, and a public repo doesn't let everyone's PRs reach
/// the agent while the untrusted tier doesn't exist (spec "Policy"). A token from the Azure CLI
/// is asked for here, so a CLI that isn't logged in shows up before anything else.
/// </summary>
public static class ProviderChecks
{
    public static async Task<(IReadOnlyList<RepoReport> Repos, IReadOnlyList<Diagnostic> Diagnostics)> RunAsync(
        RtfdConfig config, TokenSources tokens, Func<RepoConfig, TokenSource, IProvider> providers,
        CancellationToken cancellationToken = default)
    {
        var reports = new List<RepoReport>();
        var diagnostics = new List<Diagnostic>();
        var identities = new Dictionary<TokenSource, string>();
        var refused = new Dictionary<TokenSource, string>();

        foreach (var repo in config.Repos)
        {
            var source = tokens.For(repo);
            var tokenKey = repo.Auth == TokenAuth.AzureCli ? "auth" : "token_env";
            var from = source.ReadOnce ? $"in {source.Describe}" : $"from {source.Describe}";

            void Error(string check, string message, string key) =>
                diagnostics.Add(new(check, DiagnosticLevel.Error, $"{repo.Name}: {message}", $"{repo.Path}.{key}", config.LineOf($"{repo.Path}.{key}")));

            if (refused.TryGetValue(source, out var earlier))
            {
                Error("provider.token_refused", earlier, tokenKey);
                reports.Add(new RepoReport(repo, null, null, "its token was refused"));
                continue;
            }

            try
            {
                await source.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderException ex) when (ex.NoToken)
            {
                // An unset variable is the config checks' warning already; an Azure CLI without a token is news.
                if (source.ReadOnce)
                {
                    reports.Add(new RepoReport(repo, null, null, $"{source.Describe} isn't set"));
                }
                else
                {
                    refused[source] = ex.Message;
                    Error("provider.token_refused", ex.Message, tokenKey);
                    reports.Add(new RepoReport(repo, null, null, "it has no token"));
                }

                continue;
            }

            var provider = providers(repo, source);
            try
            {
                if (!identities.TryGetValue(source, out var identity))
                {
                    try
                    {
                        identities[source] = identity = await provider.WhoAmIAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (ProviderException ex)
                    {
                        refused[source] = $"the token {from} doesn't work: {ex.Message}";
                        Error("provider.token_refused", refused[source], tokenKey);
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
                    Error("provider.repo_unreachable", $"{repo.Slug} can't be read with the token {from}: {ex.Message}", "repo");
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
