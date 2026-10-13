using System.Text.RegularExpressions;
using Rtfd.Config;

namespace Rtfd.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void The_example_config_loads_and_passes_every_check()
    {
        using var temp = new TempHome();
        var path = Path.Combine(Configs.RepositoryRoot, "examples", "rtfd.yaml");

        var loaded = ConfigLoader.LoadFile(path, temp.Home);
        var checks = ConfigChecks.Run(loaded.Config!, temp.Set("GH_TOKEN", "set").Environment);

        Assert.Empty(loaded.Diagnostics.Concat(checks));
        Assert.Equal(["nutrx", "sandbox"], loaded.Config!.Repos.Select(r => r.Name));
    }

    [Fact]
    public void A_misspelt_key_is_an_error_with_its_line_number()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal(repoExtra: "    promt: other.md"));

        var diagnostic = Assert.Single(loaded.Diagnostics);
        Assert.Equal("config.unknown_key", diagnostic.Check);
        Assert.Equal("repos[0].promt", diagnostic.Path);
        Assert.Equal(temp.LineOf("promt:"), diagnostic.Line);
    }

    [Fact]
    public void Malformed_yaml_is_an_error_at_its_line()
    {
        using var temp = new TempHome();

        var loaded = temp.Load("agents:\n  claude:\n    account: [unclosed\n");

        var diagnostic = Assert.Single(loaded.Diagnostics);
        Assert.Equal("config.malformed", diagnostic.Check);
        Assert.True(diagnostic.Line > 0);
        Assert.Null(loaded.Config);
    }

    [Fact]
    public void An_empty_file_is_an_error()
    {
        using var temp = new TempHome();

        var loaded = temp.Load("");

        Assert.Equal("config.empty", Assert.Single(loaded.Diagnostics).Check);
    }

    [Fact]
    public void A_config_without_repos_is_an_error()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Agent);

        Assert.Equal("config.no_repos", Assert.Single(loaded.Diagnostics).Check);
    }

    [Fact]
    public void Defaults_apply_to_every_repo_and_a_repo_can_override_them()
    {
        using var temp = new TempHome();
        var yaml = Configs.Minimal(
            defaults: "  interval: 10m\n  max_attempts: 5\n  exclude: [\"**/*.gen.cs\"]",
            repoExtra: "    max_attempts: 2");

        var loaded = temp.Load(yaml);

        Assert.Empty(loaded.Diagnostics);
        var settings = Assert.Single(loaded.Config!.Repos).Settings;
        Assert.Equal(TimeSpan.FromMinutes(10), settings.Interval);
        Assert.Equal(2, settings.MaxAttempts);
        Assert.Equal(["**/*.gen.cs"], settings.Exclude);
        Assert.Equal(TimeSpan.FromMinutes(3), settings.QuietPeriod);
        Assert.Equal([FindingSeverity.Should], settings.NotReadyIfOpen);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("1.5h", 5400)]
    public void Durations_take_seconds_minutes_and_hours(string text, int seconds)
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal(defaults: $"  timeout: {text}"));

        Assert.Equal(TimeSpan.FromSeconds(seconds), loaded.Config!.Repos[0].Settings.Timeout);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("five minutes")]
    [InlineData("5d")]
    [InlineData("-5m")]
    public void A_duration_without_a_known_unit_is_an_error(string text)
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal(defaults: $"  timeout: {text}"));

        var diagnostic = Assert.Single(loaded.Diagnostics);
        Assert.Equal("config.not_a_duration", diagnostic.Check);
        Assert.Equal("defaults.timeout", diagnostic.Path);
    }

    [Fact]
    public void A_missing_required_key_is_an_error()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal().Replace("    token_env: GH_TOKEN\n", ""));

        var diagnostic = Assert.Single(loaded.Diagnostics);
        Assert.Equal("config.required", diagnostic.Check);
        Assert.Equal("repos[0].token_env", diagnostic.Path);
    }

    [Fact]
    public void A_single_value_where_a_list_belongs_is_an_error()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal().Replace("authors: [owner]", "authors: owner"));

        Assert.Contains(loaded.Diagnostics, d => d.Check == "config.not_a_list" && d.Path == "repos[0].authors");
    }

    [Fact]
    public void An_unknown_value_lists_the_ones_that_exist()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal().Replace("provider: github", "provider: gitlab"));

        var diagnostic = Assert.Single(loaded.Diagnostics);
        Assert.Equal("config.value_unknown", diagnostic.Check);
        Assert.Contains("github, bitbucket", diagnostic.Message);
    }

    [Fact]
    public void A_repo_naming_an_unknown_agent_is_an_error()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal().Replace("agent: claude", "agent: codex"));

        var diagnostic = Assert.Single(loaded.Diagnostics);
        Assert.Equal("config.agent_unknown", diagnostic.Check);
        Assert.Equal(temp.LineOf("agent: codex"), diagnostic.Line);
    }

    [Fact]
    public void Repo_names_must_be_unique()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal() + Configs.Repo + "\n");

        var diagnostic = Assert.Single(loaded.Diagnostics);
        Assert.Equal("config.repo_name_duplicate", diagnostic.Check);
        Assert.Equal("repos[1].name", diagnostic.Path);
    }

    [Fact]
    public void The_prompt_path_resolves_against_the_config_folder()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal());

        Assert.Equal(Path.Combine(temp.Root, "prompts", "review.md"), loaded.Config!.Repos[0].PromptPath);
    }

    [Fact]
    public void A_claude_agent_is_recognised_from_its_command_and_reads_its_answer_from_result()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal());

        var agent = loaded.Config!.Agents["claude"];
        Assert.Equal(AgentKind.Claude, agent.Kind);
        Assert.Equal(["claude", "-p", "--permission-mode", "dontAsk", "--output-format", "json"], agent.Command);
        Assert.Equal(new AgentOutput(OutputSource.Stdout, ".result"), agent.Output);
    }

    [Fact]
    public void A_config_can_add_project_files_to_reset_but_never_remove_the_built_in_ones()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal(agentExtra: "    project_config:\n      executable: [tools/agent.json]\n      instructions: []"));

        Assert.Empty(loaded.Diagnostics);
        var paths = loaded.Config!.Agents["claude"].ProjectConfig;
        Assert.Contains(".claude/settings.json", paths.Executable);
        Assert.Contains(".mcp.json", paths.Executable);
        Assert.Contains("tools/agent.json", paths.Executable);
        Assert.Contains("CLAUDE.md", paths.Instructions);
    }

    [Fact]
    public void A_pattern_that_does_not_compile_is_an_error()
    {
        using var temp = new TempHome();

        var loaded = temp.Load(Configs.Minimal(agentExtra: "    limit_patterns: [\"(unclosed\"]"));

        var diagnostic = Assert.Single(loaded.Diagnostics);
        Assert.Equal("config.pattern_invalid", diagnostic.Check);
        Assert.Equal("agents.claude.limit_patterns", diagnostic.Path);
    }

    [Fact]
    public void The_built_in_limit_pattern_finds_the_reset_time_and_zone_with_the_engine_that_runs_it()
    {
        var pattern = BuiltIn.LimitPatterns(AgentKind.Claude, "test")[0].Pattern;

        var match = new Regex(pattern, RegexOptions.NonBacktracking)
            .Match("You've hit your session limit · resets 12:10am (Europe/Chisinau)");

        Assert.True(match.Success);
        Assert.Equal("12:10am", match.Groups["reset"].Value);
        Assert.Equal("Europe/Chisinau", match.Groups["zone"].Value);
    }
}
