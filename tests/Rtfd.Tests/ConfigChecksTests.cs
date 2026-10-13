using Rtfd.Config;

namespace Rtfd.Tests;

public class ConfigChecksTests
{
    [Fact]
    public void A_minimal_config_passes_every_check()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        Assert.Empty(temp.Validate(Configs.Minimal()));
    }

    [Fact]
    public void Hooks_are_refused_until_they_ship()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        var diagnostics = temp.Validate(Configs.Minimal(repoExtra: "    hooks:\n      - name: build\n        command: dotnet build"));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("config.hooks_unsupported", diagnostic.Check);
        Assert.Equal("repos[0].hooks", diagnostic.Path);
        Assert.Equal(temp.LineOf("hooks:"), diagnostic.Line);
    }

    [Fact]
    public void The_untrusted_tier_is_refused_until_it_exists()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        var diagnostics = temp.Validate(Configs.Minimal().Replace("trust: trusted", "trust: untrusted"));

        Assert.Equal("config.trust_untrusted_unsupported", Assert.Single(diagnostics).Check);
    }

    [Fact]
    public void Bitbucket_is_refused_until_v1()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        var diagnostics = temp.Validate(Configs.Minimal().Replace("provider: github", "provider: bitbucket"));

        Assert.Equal("config.provider_unsupported", Assert.Single(diagnostics).Check);
    }

    [Theory]
    [InlineData("copilot -p")]
    [InlineData("codex exec")]
    [InlineData("pwsh -c claude -p")]
    [InlineData("bash -c 'claude -p'")]
    public void An_agent_rtfd_cannot_confine_is_refused(string command)
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        var diagnostics = temp.Validate(Configs.Minimal().Replace("claude -p --permission-mode dontAsk --output-format json", command));

        Assert.Equal("config.agent_unsupported", Assert.Single(diagnostics).Check);
    }

    [Theory]
    [InlineData("claude -p --allowedTools Bash")]
    [InlineData("claude -p --dangerously-skip-permissions")]
    [InlineData("claude -p --permission-mode bypassPermissions")]
    [InlineData("claude -p --permission-mode=acceptEdits")]
    [InlineData("claude -p --mcp-config ~/.claude.json")]
    [InlineData("claude -p --settings=mine.json")]
    [InlineData(@"claude -p --add-dir C:\Users")]
    public void A_claude_command_may_not_set_what_rtfd_decides(string command)
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        var diagnostics = temp.Validate(Configs.Minimal().Replace("claude -p --permission-mode dontAsk --output-format json", command));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("config.agent_command_owned_flag", diagnostic.Check);
        Assert.Equal("agents.claude.command", diagnostic.Path);
    }

    [Theory]
    [InlineData("mcp: all")]
    [InlineData("settings: user")]
    [InlineData("plugins: [rtfc]")]
    [InlineData("network: on")]
    public void Claude_isolation_cannot_be_loosened(string setting)
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        var diagnostics = temp.Validate(Configs.Minimal(agentExtra: $"    isolation:\n      {setting}"));

        Assert.Contains(Assert.Single(diagnostics).Check, new[] { "config.isolation_weakened", "config.isolation_unknown" });
    }

    [Fact]
    public void Full_isolation_written_out_is_accepted()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        var diagnostics = temp.Validate(Configs.Minimal(
            agentExtra: "    isolation: { mcp: strict, settings: none, plugins: none }\n    tools: { read: [worktree, run_folder], mcp: [rtfd-git] }"));

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("read: [worktree, home]")]
    [InlineData("mcp: [rtfd-git, rtfc]")]
    [InlineData("shell: [git]")]
    public void Claude_tools_cannot_go_beyond_reads_and_rtfd_git(string tools)
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        var diagnostics = temp.Validate(Configs.Minimal(agentExtra: $"    tools:\n      {tools}"));

        Assert.Equal("config.tool_not_allowed", Assert.Single(diagnostics).Check);
    }

    [Fact]
    public void Context_tools_are_refused_until_they_ship_and_reported_once_for_defaults()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");
        var yaml = Configs.Minimal(defaults: "  context_tools: [rtfm]") + Configs.Repo.Replace("name: nutrx", "name: other") + "\n";

        var diagnostics = temp.Validate(yaml);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("config.context_tools_unsupported", diagnostic.Check);
        Assert.Equal("defaults.context_tools", diagnostic.Path);
    }

    [Fact]
    public void A_token_in_token_env_is_refused_and_never_shown()
    {
        // Built at run time, so no token-shaped literal sits in the source for scanners to flag.
        string[] tokens =
        [
            "ghp_" + new string('a', 36),
            "github_pat_" + new string('B', 40),
            "glpat-" + new string('c', 20),
            "xoxb-" + new string('1', 30),
            "AbCdEfGhIjKlMnOpQrStUvWxYz" + "0123456789",
        ];

        foreach (var token in tokens)
        {
            using var temp = new TempHome();
            temp.WriteConfig(Configs.Minimal().Replace("token_env: GH_TOKEN", $"token_env: {token}"));

            var (code, stdout, stderr) = temp.Run("validate");

            Assert.Equal(EntryPoint.Failure, code);
            Assert.Contains("config.token_env_holds_a_token", stdout);
            Assert.DoesNotContain(token, stdout + stderr);
        }
    }

    [Fact]
    public void A_token_env_that_is_not_a_variable_name_is_refused()
    {
        using var temp = new TempHome();

        var diagnostics = temp.Validate(Configs.Minimal().Replace("token_env: GH_TOKEN", "token_env: my token"));

        Assert.Equal("config.token_env_not_a_name", Assert.Single(diagnostics).Check);
    }

    [Fact]
    public void An_unset_token_variable_is_only_a_warning()
    {
        using var temp = new TempHome();

        var diagnostics = temp.Validate(Configs.Minimal());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("config.token_env_unset", diagnostic.Check);
        Assert.False(diagnostic.IsError);
    }

    [Fact]
    public void A_missing_prompt_file_is_an_error()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");

        var diagnostics = temp.Validate(Configs.Minimal(), withPrompt: false);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("config.prompt_missing", diagnostic.Check);
        Assert.Equal(temp.LineOf("prompt:"), diagnostic.Line);
    }
}
