using Rtfd.Git;

namespace Rtfd.Tests.Support;

/// <summary>
/// A real git repository in a temp folder, standing in for the provider's remote: git is never
/// mocked (CLAUDE.md "Testing"). Commits go through <see cref="GitRunner"/>, so the user's own
/// git config (signing, hooks) never applies.
/// </summary>
public sealed class GitFixture : IDisposable
{
    public GitFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "rtfd-t", Guid.NewGuid().ToString("N")[..8]);
        Origin = Path.Combine(Root, "origin");
        Directory.CreateDirectory(Origin);
        Data = new DataDirectory(Path.Combine(Root, "data"));
        Git = new GitRunner(Data.GitDirectory, Environment.GetEnvironmentVariable, []);
        Run("init", "--quiet", "--initial-branch=main");
    }

    public string Root { get; }

    /// <summary>The working repository the mirror fetches from.</summary>
    public string Origin { get; }

    public DataDirectory Data { get; }
    public GitRunner Git { get; }

    public string Run(params string[] arguments) =>
        Git.RunCheckedAsync(Origin, ["-c", "user.name=rtfd test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", .. arguments])
            .GetAwaiter().GetResult();

    /// <summary>Writes the files (null deletes one), commits, and returns the commit's SHA.</summary>
    public string Commit(string message, IReadOnlyDictionary<string, string?> files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(Origin, path.Replace('/', Path.DirectorySeparatorChar));
            if (content is null)
            {
                Run("rm", "--quiet", "--", path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            Run("add", "--", path);
        }

        Run("commit", "--quiet", "--allow-empty", "-m", message);
        return Head();
    }

    /// <summary>Commits a symlink without needing the OS to make one: a blob of the target, mode 120000.</summary>
    public string CommitSymlink(string message, string path, string target)
    {
        var blob = Git.RunCheckedAsync(Origin, ["hash-object", "-w", "--stdin"], stdin: target).GetAwaiter().GetResult().Trim();
        Run("update-index", "--add", "--cacheinfo", $"120000,{blob},{path}");
        Run("commit", "--quiet", "-m", message);
        Run("checkout", "--quiet", "--", ".");
        return Head();
    }

    public void Branch(string name, string? from = null)
    {
        if (from is null)
        {
            Run("checkout", "--quiet", "-b", name);
        }
        else
        {
            Run("checkout", "--quiet", "-b", name, from);
        }
    }

    public void Checkout(string name) => Run("checkout", "--quiet", name);

    public string Head() => Run("rev-parse", "HEAD").Trim();

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
