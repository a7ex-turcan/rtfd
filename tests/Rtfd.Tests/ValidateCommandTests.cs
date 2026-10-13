namespace Rtfd.Tests;

public class ValidateCommandTests
{
    [Fact]
    public void Validate_without_a_config_says_where_it_looked_and_points_at_the_example()
    {
        using var temp = new TempHome();

        var (code, _, stderr) = temp.Run("validate");

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains($"no config at {temp.ConfigPath}", stderr);
        Assert.Contains("examples/", stderr);
    }

    [Fact]
    public void Validate_accepts_a_good_config_and_says_what_it_found()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set").WriteConfig(Configs.Minimal());

        var (code, stdout, _) = temp.Run("validate", "--config-only");

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Contains($"Config: {temp.ConfigPath}", stdout);
        Assert.Contains("1 repo (nutrx), 1 agent (claude).", stdout);
        Assert.Contains("No errors.", stdout);
    }

    [Fact]
    public void Validate_fails_on_errors_and_names_each_check_with_its_line()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set").WriteConfig(Configs.Minimal(repoExtra: "    hooks:\n      - name: build\n        command: dotnet build"));

        var (code, stdout, _) = temp.Run("validate");

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains($"error [config.hooks_unsupported] line {temp.LineOf("hooks:")}, repos[0].hooks:", stdout);
        Assert.Contains("1 error, 0 warnings.", stdout);
    }

    [Fact]
    public void Validate_reads_the_config_the_option_names()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set");
        var elsewhere = Path.Combine(temp.Root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "prompts"));
        File.WriteAllText(Path.Combine(elsewhere, "prompts", "review.md"), "Review the PR.");
        var path = Path.Combine(elsewhere, "work.yaml");
        File.WriteAllText(path, Configs.Minimal());

        var (code, stdout, _) = temp.Run("validate", "--config", path, "--config-only");

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Contains($"Config: {path}", stdout);
    }

    [Fact]
    public void Validate_checks_tokens_and_agents_only_once_the_config_has_no_errors()
    {
        using var temp = new TempHome();
        temp.Set("GH_TOKEN", "set").WriteConfig(Configs.Minimal(repoExtra: "    promt: x"));

        var (code, stdout, _) = temp.Run("validate");

        Assert.Equal(EntryPoint.Failure, code);
        Assert.DoesNotContain("Repos:", stdout);
        Assert.Contains("checked once the config has no errors", stdout);
    }
}
