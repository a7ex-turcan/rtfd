namespace Rtfd.Tests;

public class EntryPointTests
{
    [Fact]
    public void Version_prints_to_stdout_and_succeeds()
    {
        using var temp = new TempHome();

        var (code, stdout, stderr) = temp.Run("--version");

        Assert.Equal(EntryPoint.Ok, code);
        Assert.Matches(@"^\d+\.\d+\.\d+", stdout);
        Assert.Empty(stderr);
    }

    [Theory]
    [InlineData]
    [InlineData("not-a-command")]
    [InlineData("--help")]
    public void Unknown_or_missing_command_prints_usage_to_stderr(params string[] args)
    {
        using var temp = new TempHome();

        var (code, stdout, stderr) = temp.Run(args);

        Assert.Equal(EntryPoint.Usage, code);
        Assert.Contains("usage: rtfd", stderr);
        Assert.Empty(stdout);
    }

    [Theory]
    [InlineData("run", "Phase 1")]
    [InlineData("git-tools", "Phase 1")]
    [InlineData("retry", "Phase 3")]
    [InlineData("serve", "Phase 5")]
    [InlineData("status", "Phase 5")]
    [InlineData("install-service", "Phase 6")]
    public void Commands_not_built_yet_name_the_phase_that_brings_them(string command, string phase)
    {
        using var temp = new TempHome();

        var (code, stdout, stderr) = temp.Run(command);

        Assert.Equal(EntryPoint.Failure, code);
        Assert.Contains($"not built yet. It arrives in {phase}", stderr);
        Assert.Empty(stdout);
    }

    [Fact]
    public void Every_command_in_the_spec_is_built_or_scheduled()
    {
        // The spec's CLI table.
        string[] commands = ["serve", "install-service", "status", "run", "retry", "pause", "resume", "validate", "git-tools"];

        Assert.All(commands, command => Assert.True(command == "validate" || EntryPoint.NotYetBuilt.ContainsKey(command), command));
    }

    [Fact]
    public void An_unknown_option_is_a_usage_error()
    {
        using var temp = new TempHome();

        var (code, _, stderr) = temp.Run("validate", "--confg", "rtfd.yaml");

        Assert.Equal(EntryPoint.Usage, code);
        Assert.Contains("unknown option '--confg'", stderr);
    }
}
