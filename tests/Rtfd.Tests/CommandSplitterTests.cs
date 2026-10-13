using Rtfd.Config;

namespace Rtfd.Tests;

public class CommandSplitterTests
{
    [Fact]
    public void Arguments_are_split_on_whitespace()
    {
        Assert.Equal(["claude", "-p", "--output-format", "json"], CommandSplitter.Split("claude  -p --output-format\tjson"));
    }

    [Fact]
    public void Quotes_group_and_backslashes_stay_literal()
    {
        var args = CommandSplitter.Split(@"""C:\Program Files\claude\claude.exe"" -p --append-system-prompt 'be brief'");

        Assert.Equal([@"C:\Program Files\claude\claude.exe", "-p", "--append-system-prompt", "be brief"], args);
    }

    [Fact]
    public void An_open_quote_is_refused()
    {
        Assert.Null(CommandSplitter.Split("claude -p 'unclosed"));
    }

    [Theory]
    [InlineData("claude", AgentKind.Claude)]
    [InlineData("claude.exe", AgentKind.Claude)]
    [InlineData(@"C:\tools\claude.cmd", AgentKind.Claude)]
    [InlineData("/usr/local/bin/claude", AgentKind.Claude)]
    [InlineData("copilot", AgentKind.Copilot)]
    [InlineData("pwsh", AgentKind.Unknown)]
    [InlineData("bash", AgentKind.Unknown)]
    [InlineData("codex", AgentKind.Unknown)]
    public void The_agent_kind_comes_from_the_executable_name(string executable, AgentKind kind)
    {
        Assert.Equal(kind, CommandSplitter.KindOf(executable));
    }
}
