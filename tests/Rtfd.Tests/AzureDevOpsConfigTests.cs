using Rtfd.Config;
using Rtfd.Providers;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>An Azure DevOps repo in the config: its three-part name, and its token from a variable or the Azure CLI.</summary>
public sealed class AzureDevOpsConfigTests
{
    private const string AzureRepo = """
          - name: web-app
            provider: azure_devops
            repo: contoso/Fabrikam Fiber/web-app
            trust: trusted
            agent: claude
            prompt: prompts/review.md
            authors: [author@contoso.com]
            verdicts: [comment, approve, request_changes]
            auth: azure_cli
            tenant: 0c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5
        """;

    private static IReadOnlyList<Diagnostic> Validate(string repo)
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");
        temp.Set("ADO_TOKEN", "set");
        return temp.Validate(Configs.Minimal(repoExtra: repo));
    }

    [Fact]
    public void An_Azure_DevOps_repo_with_its_token_from_the_Azure_CLI_passes()
    {
        Assert.Empty(Validate(AzureRepo));

        using var temp = new TempHome();
        var config = temp.Load(Configs.Minimal(repoExtra: AzureRepo)).Config!;
        var repo = config.Repos[1];
        Assert.Equal((ProviderKind.AzureDevOps, TokenAuth.AzureCli, (string?)null), (repo.Provider, repo.Auth, repo.TokenEnv));
        Assert.Equal("contoso/Fabrikam Fiber/web-app", repo.Slug);
        Assert.Equal("the Azure CLI (tenant 0c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5)", repo.TokenFrom);
    }

    [Fact]
    public void A_PAT_in_a_variable_works_too()
    {
        var repo = AzureRepo.Replace("    auth: azure_cli\n    tenant: 0c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5", "    token_env: ADO_TOKEN", StringComparison.Ordinal);

        Assert.Empty(Validate(repo));
    }

    [Theory]
    [InlineData("contoso/web-app")]
    [InlineData("contoso/Fabrikam Fiber/web-app/extra")]
    [InlineData("contoso/Fabrikam?Fiber/web-app")]
    [InlineData("-contoso/Fabrikam Fiber/web-app")]
    public void An_Azure_DevOps_repo_is_organization_project_repository(string slug)
    {
        var diagnostic = Assert.Single(Validate(AzureRepo.Replace("contoso/Fabrikam Fiber/web-app", slug, StringComparison.Ordinal)));

        Assert.Equal("config.repo_slug_invalid", diagnostic.Check);
        Assert.Contains("organization/project/repository", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_token_env_and_the_Azure_CLI_is_one_source_too_many()
    {
        var diagnostic = Assert.Single(Validate(AzureRepo + "\n    token_env: ADO_TOKEN"));

        Assert.Equal(("config.auth_conflict", "repos[1].token_env"), (diagnostic.Check, diagnostic.Path));
    }

    [Fact]
    public void A_repo_with_no_token_source_says_what_it_can_have()
    {
        var diagnostic = Assert.Single(Validate(AzureRepo.Replace("    auth: azure_cli\n    tenant: 0c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5", "", StringComparison.Ordinal)));

        Assert.Equal("config.required", diagnostic.Check);
        Assert.Contains("auth: azure_cli", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("contoso.onmicrosoft.com", null)]
    [InlineData("contoso.com", null)]
    [InlineData("not a tenant", "config.tenant_invalid")]
    [InlineData("--allow-no-subscriptions", "config.tenant_invalid")]
    public void A_tenant_is_a_GUID_or_a_domain(string tenant, string? check)
    {
        var diagnostics = Validate(AzureRepo.Replace("0c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5", $"'{tenant}'", StringComparison.Ordinal));

        Assert.Equal(check, diagnostics.SingleOrDefault()?.Check);
    }

    [Fact]
    public void The_Azure_CLI_is_for_Azure_DevOps_only()
    {
        var diagnostics = Validate("""
              - name: other
                provider: github
                repo: owner/other
                trust: trusted
                agent: claude
                prompt: prompts/review.md
                authors: [owner]
                auth: azure_cli
            """);

        Assert.Equal("config.auth_unsupported", Assert.Single(diagnostics).Check);
    }

    [Fact]
    public void A_tenant_without_the_Azure_CLI_means_nothing()
    {
        var diagnostics = Validate(AzureRepo.Replace("    auth: azure_cli\n", "    token_env: ADO_TOKEN\n", StringComparison.Ordinal));

        Assert.Equal("config.tenant_without_azure_cli", Assert.Single(diagnostics).Check);
    }
}

/// <summary>Where tokens come from: a variable read once, or the Azure CLI asked again before each token expires.</summary>
public sealed class TokenSourceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    private static string Jwt() => "eyJ" + Tokens.Random(20) + "." + Tokens.Random(30) + "." + Tokens.Random(20);

    private static Func<IReadOnlyList<string>, CancellationToken, Task<AzureCliAnswer>> Cli(List<IReadOnlyList<string>> calls, Func<AzureCliAnswer> answer) =>
        (arguments, _) =>
        {
            calls.Add(arguments);
            return Task.FromResult(answer());
        };

    [Fact]
    public async Task The_Azure_CLI_is_asked_once_and_again_five_minutes_before_the_token_expires()
    {
        var clock = new ManualClock(Start);
        var calls = new List<IReadOnlyList<string>>();
        var source = new AzureCliTokenSource("contoso.onmicrosoft.com", clock, Cli(calls,
            () => new AzureCliAnswer(0, $$"""{"accessToken": "{{Jwt()}}", "expiresOn": "2026-10-21 09:00:00.000000", "expires_on": {{(clock.Now + TimeSpan.FromHours(1)).ToUnixTimeSeconds()}}, "tokenType": "Bearer"}""", "")));

        var first = await source.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(54));
        var same = await source.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(2));
        var renewed = await source.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        var forced = await source.GetAsync(renew: true, TestContext.Current.CancellationToken);

        Assert.Same(first, same);
        Assert.NotSame(first, renewed);
        Assert.NotSame(renewed, forced);
        Assert.Equal(3, calls.Count);
        Assert.Same(forced, source.Current);
        Assert.False(source.ReadOnce);
        Assert.Equal(
            ["account", "get-access-token", "--resource", "499b84ac-1321-427f-aa17-267ca6975798", "--tenant", "contoso.onmicrosoft.com", "--output", "json"],
            calls[0]);
    }

    [Fact]
    public async Task Without_expires_on_the_local_expiresOn_is_used()
    {
        var clock = new ManualClock(Start);
        var local = TimeZoneInfo.ConvertTime(Start + TimeSpan.FromMinutes(70), TimeZoneInfo.Local);
        var source = new AzureCliTokenSource(null, clock, Cli([],
            () => new AzureCliAnswer(0, $$"""{"accessToken": "{{Jwt()}}", "expiresOn": "{{local:yyyy-MM-dd HH:mm:ss}}.000000"}""", "")));

        await source.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Start + TimeSpan.FromMinutes(70), source.Expires);
        Assert.Equal("the Azure CLI", source.Describe);
    }

    [Fact]
    public async Task A_CLI_that_isn_t_logged_in_says_how_to_log_in_and_never_shows_its_output()
    {
        var secret = Jwt();
        var source = new AzureCliTokenSource("contoso.onmicrosoft.com", TimeProvider.System, Cli([],
            () => new AzureCliAnswer(1, secret, "ERROR: AADSTS700082: The refresh token has expired due to inactivity.\nTrace ID: 0000")));

        var error = await Assert.ThrowsAsync<ProviderException>(() => source.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error.NoToken);
        Assert.True(error.IsAuth);
        Assert.Contains("AADSTS700082", error.Message, StringComparison.Ordinal);
        Assert.Contains("`az login --tenant contoso.onmicrosoft.com`", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Trace ID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_CLI_that_isn_t_there_says_so()
    {
        var source = new AzureCliTokenSource(null, TimeProvider.System, Cli([], () => new AzureCliAnswer(-1, "", "", "az isn't on PATH or in the usual install folder")));

        var error = await Assert.ThrowsAsync<ProviderException>(() => source.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("az isn't on PATH", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_variable_is_read_once_and_an_unset_one_has_no_token()
    {
        var value = Tokens.GitHubLike();
        var source = new EnvironmentTokenSource("GH_TOKEN", name => name == "GH_TOKEN" ? value : null);
        value = "changed";

        Assert.NotEqual("changed", (await source.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Value);
        Assert.True(source.ReadOnce);

        var unset = new EnvironmentTokenSource("ADO_TOKEN", _ => null);
        var error = await Assert.ThrowsAsync<ProviderException>(() => unset.GetAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(error.NoToken);
        Assert.Null(unset.Current);
    }

    [Fact]
    public async Task Repos_share_a_source_per_variable_and_per_tenant_and_every_token_handed_out_is_known()
    {
        using var temp = new TempHome();
        var config = temp.Load(Configs.Minimal(repoExtra: """
              - name: web-app
                provider: azure_devops
                repo: contoso/Fabrikam Fiber/web-app
                trust: trusted
                agent: claude
                prompt: prompts/review.md
                authors: [author@contoso.com]
                auth: azure_cli
                tenant: contoso.onmicrosoft.com
              - name: api
                provider: azure_devops
                repo: contoso/Fabrikam Fiber/api
                trust: trusted
                agent: claude
                prompt: prompts/review.md
                authors: [author@contoso.com]
                auth: azure_cli
                tenant: Contoso.onmicrosoft.com
            """)).Config!;
        var gh = Tokens.GitHubLike();
        var jwt = Jwt();
        var made = 0;
        var tokens = TestTokens.From(name => name == "GH_TOKEN" ? gh : null, tenant =>
        {
            made++;
            return new AzureCliTokenSource(tenant, TimeProvider.System, (_, _) => Task.FromResult(new AzureCliAnswer(0, $$"""{"accessToken": "{{jwt}}", "expires_on": 4102444800}""", "")));
        });

        Assert.Equal([gh], tokens.Known(config));
        Assert.Same(tokens.For(config.Repos[1]), tokens.For(config.Repos[2]));
        await tokens.For(config.Repos[1]).GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, made);
        Assert.Equal([gh, jwt], tokens.Known(config));
    }
}
