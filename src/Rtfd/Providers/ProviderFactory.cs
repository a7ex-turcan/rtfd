using Rtfd.Config;
using Rtfd.Providers.AzureDevOps;
using Rtfd.Providers.GitHub;

namespace Rtfd.Providers;

/// <summary>The real provider client for a repo, by its <c>provider</c>.</summary>
public static class ProviderFactory
{
    public static IProvider Create(RepoConfig repo, TokenSource tokens) => repo.Provider switch
    {
        ProviderKind.AzureDevOps => new AzureDevOpsProvider(repo.Slug, tokens),
        ProviderKind.GitHub => new GitHubProvider(repo.Slug, tokens),
        _ => throw new NotSupportedException($"{ProviderNames.Of(repo.Provider)} isn't built yet"),
    };
}
