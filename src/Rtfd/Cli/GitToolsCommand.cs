using Rtfd.GitTools;

namespace Rtfd.Cli;

/// <summary>
/// <c>rtfd git-tools --run &lt;folder&gt;</c>: the per-review <c>rtfd-git</c> MCP server. Claude
/// starts it from the run folder's <c>mcp.json</c>; nobody runs it by hand. stdout carries the
/// protocol and nothing else, so every message here goes to stderr.
/// </summary>
internal static class GitToolsCommand
{
    public static int Run(CommandContext ctx, string[] args, TextReader stdin)
    {
        var commandLine = new CommandLine(args);
        commandLine.Expect(0, "run");
        var folder = commandLine.Value("run") ?? throw new CommandLineException("'--run <folder>' is required.");

        GitToolset tools;
        try
        {
            var binding = GitToolsBinding.Read(Path.GetFullPath(folder));
            tools = GitToolset.OpenAsync(binding, ctx.Environment).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or Git.GitException)
        {
            ctx.Stderr.WriteLine($"rtfd git-tools: can't open the run folder {folder}: {ex.Message}");
            return EntryPoint.Failure;
        }

        new McpServer(tools, stdin, ctx.Stdout).RunAsync().GetAwaiter().GetResult();
        return EntryPoint.Ok;
    }
}
