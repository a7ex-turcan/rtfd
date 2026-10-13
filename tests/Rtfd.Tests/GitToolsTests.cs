using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Rtfd.Git;
using Rtfd.GitTools;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>
/// <c>rtfd-git</c>: the agent's only way into git history, so its argument checks are a guard.
/// With <c>RTFD_BINARY</c> set to a published rtfd, every test here talks to that binary over
/// stdio instead of to the server in-process: CI runs them against the Native AOT build, because
/// a guard that passes under the JIT can fail open after trimming (rtfq ADR 0001).
/// </summary>
public sealed class GitToolsTests : IAsyncLifetime
{
    private readonly GitFixture _git = new();
    private string _runFolder = "";
    private string _outsideCommit = "";
    private string _mergeBase = "";
    private string _head = "";
    private string _firstPrCommit = "";
    private IToolHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        var cancel = TestContext.Current.CancellationToken;
        _mergeBase = _git.Commit("Base", new Dictionary<string, string?> { ["src/app.txt"] = "one\ntwo\nthree\n" });
        _git.Branch("feature");
        _firstPrCommit = _git.Commit("First", new Dictionary<string, string?> { ["src/app.txt"] = "one\ntwo\nthree\nfour\n" });
        _head = _git.Commit("Second", new Dictionary<string, string?> { ["src/new.txt"] = "new\n" });
        _git.Checkout("main");
        _outsideCommit = _git.Commit("Elsewhere on main", new Dictionary<string, string?> { ["src/other.txt"] = "other\n" });

        var mirror = Mirror.For(_git.Data, "repo", _git.Git);
        var commits = await mirror.FetchAsync(new FetchSource(_git.Origin, "refs/heads/feature", null), 1, "main", cancel);
        Assert.Equal(_mergeBase, commits.MergeBase);

        _runFolder = Path.Combine(_git.Root, "run");
        Directory.CreateDirectory(_runFolder);
        var binding = new GitToolsBinding(mirror.Path, Path.Combine(_git.Root, "worktree"), commits.Head, commits.MergeBase, null, _git.Data.GitDirectory);
        binding.Write(_runFolder);

        _host = Environment.GetEnvironmentVariable("RTFD_BINARY") is { Length: > 0 } binary
            ? ProcessHost.Start(binary, _runFolder)
            : new InProcessHost(await GitToolset.OpenAsync(binding, Environment.GetEnvironmentVariable, cancel));
    }

    public ValueTask DisposeAsync()
    {
        (_host as IDisposable)?.Dispose();
        _git.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Log_lists_the_commits_on_the_PR_only()
    {
        var (text, isError) = await _host.CallAsync("log", new JsonObject());

        Assert.False(isError, text);
        Assert.Contains(_head, text, StringComparison.Ordinal);
        Assert.Contains(_firstPrCommit, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_mergeBase, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_outsideCommit, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_blame_and_diff_work_on_the_PR_s_commits()
    {
        var show = await _host.CallAsync("show", new JsonObject { ["sha"] = _firstPrCommit[..10], ["path"] = "src/app.txt" });
        var blame = await _host.CallAsync("blame", new JsonObject { ["path"] = "src/app.txt", ["start"] = 3, ["end"] = 4 });
        var diff = await _host.CallAsync("diff", new JsonObject { ["from"] = _mergeBase, ["to"] = _head, ["path"] = "src/new.txt" });

        Assert.Equal(("one\ntwo\nthree\nfour\n", false), show);
        Assert.False(blame.IsError, blame.Text);
        Assert.Contains(_firstPrCommit[..8], blame.Text, StringComparison.Ordinal);
        Assert.False(diff.IsError, diff.Text);
        Assert.Contains("+new", diff.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--output=x")]
    [InlineData("-p")]
    [InlineData("HEAD")]
    [InlineData("main")]
    [InlineData("HEAD~1")]
    [InlineData("1234567^{tree}")]
    public async Task A_sha_that_isn_t_hex_is_refused(string sha)
    {
        var (text, isError) = await _host.CallAsync("show", new JsonObject { ["sha"] = sha });

        Assert.True(isError, text);
        AssertNothingWritten();
    }

    [Fact]
    public async Task A_commit_from_the_base_branch_outside_the_PR_is_refused()
    {
        var show = await _host.CallAsync("show", new JsonObject { ["sha"] = _outsideCommit });
        var diff = await _host.CallAsync("diff", new JsonObject { ["from"] = _mergeBase, ["to"] = _outsideCommit });

        Assert.True(show.IsError);
        Assert.Contains("isn't a commit of this PR", show.Text, StringComparison.Ordinal);
        Assert.True(diff.IsError);
    }

    [Theory]
    [InlineData("--output=x")]
    [InlineData("../x")]
    [InlineData("src/../../x")]
    [InlineData(@"C:\x")]
    [InlineData("C:x")]
    [InlineData(@"\\server\share\x")]
    [InlineData("/etc/passwd")]
    [InlineData(":(top)src/app.txt")]
    [InlineData(":/src/app.txt")]
    [InlineData("-p")]
    [InlineData("src/app.txt\nx")]
    public async Task A_path_that_leaves_the_repo_or_isn_t_plain_is_refused(string path)
    {
        var results = new[]
        {
            await _host.CallAsync("log", new JsonObject { ["path"] = path }),
            await _host.CallAsync("show", new JsonObject { ["sha"] = _head, ["path"] = path }),
            await _host.CallAsync("blame", new JsonObject { ["path"] = path, ["start"] = 1, ["end"] = 2 }),
            await _host.CallAsync("diff", new JsonObject { ["from"] = _mergeBase, ["to"] = _head, ["path"] = path }),
        };

        Assert.All(results, r => Assert.True(r.IsError, r.Text));
        AssertNothingWritten();
    }

    [Fact]
    public async Task Bad_arguments_are_refused_as_tool_errors()
    {
        Assert.True((await _host.CallAsync("blame", new JsonObject { ["path"] = "src/app.txt", ["start"] = 1, ["end"] = 1000 })).IsError);
        Assert.True((await _host.CallAsync("blame", new JsonObject { ["path"] = "src/app.txt", ["start"] = 0, ["end"] = 2 })).IsError);
        Assert.True((await _host.CallAsync("log", new JsonObject { ["max"] = 500 })).IsError);
        Assert.True((await _host.CallAsync("show", new JsonObject())).IsError);
        Assert.True((await _host.CallAsync("show", new JsonObject { ["sha"] = 42 })).IsError);
        Assert.True((await _host.CallAsync("rm", new JsonObject())).IsError);
    }

    [Fact]
    public async Task The_server_lists_four_read_only_tools()
    {
        var tools = await _host.ListAsync();

        Assert.Equal(["log", "show", "blame", "diff"], tools);
    }

    private void AssertNothingWritten()
    {
        foreach (var folder in new[] { _runFolder, _git.Root, Environment.CurrentDirectory })
        {
            Assert.False(File.Exists(Path.Combine(folder, "x")), $"something wrote {folder}/x");
        }
    }

    private interface IToolHost
    {
        Task<(string Text, bool IsError)> CallAsync(string tool, JsonObject arguments);
        Task<IReadOnlyList<string>> ListAsync();
    }

    private sealed class InProcessHost(GitToolset tools) : IToolHost
    {
        private readonly McpServer _server = new(tools, TextReader.Null, TextWriter.Null);
        private int _id;

        public async Task<(string Text, bool IsError)> CallAsync(string tool, JsonObject arguments) =>
            Parse((await _server.HandleAsync(Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }), TestContext.Current.CancellationToken))!);

        public async Task<IReadOnlyList<string>> ListAsync() =>
            Names((await _server.HandleAsync(Request("tools/list", new JsonObject()), TestContext.Current.CancellationToken))!);

        private JsonObject Request(string method, JsonObject parameters) =>
            new() { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = parameters };
    }

    /// <summary>A published <c>rtfd git-tools</c>, driven over stdio as Claude drives it.</summary>
    private sealed class ProcessHost : IToolHost, IDisposable
    {
        private readonly Process _process;
        private int _id;

        private ProcessHost(Process process) => _process = process;

        public static ProcessHost Start(string binary, string runFolder)
        {
            var start = new ProcessStartInfo(binary, ["git-tools", "--run", runFolder])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            var host = new ProcessHost(Process.Start(start)!);
            host.SendAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" }).GetAwaiter().GetResult();
            return host;
        }

        public async Task<(string Text, bool IsError)> CallAsync(string tool, JsonObject arguments) =>
            Parse(await SendAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }));

        public async Task<IReadOnlyList<string>> ListAsync() => Names(await SendAsync("tools/list", new JsonObject()));

        private async Task<JsonObject> SendAsync(string method, JsonObject parameters)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = parameters };
            await _process.StandardInput.WriteLineAsync(request.ToJsonString());
            await _process.StandardInput.FlushAsync();
            var line = await _process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("rtfd git-tools exited: " + await _process.StandardError.ReadToEndAsync());
            return JsonNode.Parse(line)!.AsObject();
        }

        public void Dispose()
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.Dispose();
        }
    }

    private static (string, bool) Parse(JsonObject response)
    {
        var result = response["result"]!.AsObject();
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    private static IReadOnlyList<string> Names(JsonObject response) =>
        [.. response["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];
}
