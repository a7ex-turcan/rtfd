namespace Rtfd.Git;

/// <summary>Where to fetch a PR from, as the provider says (spec "Provider adapter").</summary>
/// <param name="Url">The clone URL.</param>
/// <param name="HeadRef">The ref holding the PR head, <c>refs/pull/12/head</c> on GitHub.</param>
/// <param name="Credential">Null for a public repo or a local test remote.</param>
public sealed record FetchSource(string Url, string HeadRef, GitCredential? Credential);

/// <summary>The commits one run reviews.</summary>
public sealed record ReviewCommits(string Head, string BaseTip, string MergeBase);

/// <summary>
/// rtfd's own bare mirror of a repo, <c>&lt;data&gt;/mirrors/&lt;repo&gt;.git</c> (spec decision 33).
/// It never touches the user's clones, and its config is rtfd's alone. Each run fetches just the
/// base branch and the PR head into <c>refs/rtfd/</c>.
/// </summary>
public sealed class Mirror(GitRunner git, string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public static Mirror For(DataDirectory data, string repoName, GitRunner git) =>
        new(git, System.IO.Path.Combine(data.MirrorsDirectory, repoName + ".git"));

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(System.IO.Path.Combine(Path, "HEAD")))
        {
            return;
        }

        var parent = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(parent);
        await git.RunCheckedAsync(parent, ["init", "--bare", "--quiet", Path], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches the base branch and the PR head, and works out the merge base.</summary>
    public async Task<ReviewCommits> FetchAsync(FetchSource source, int number, string baseBranch, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);

        // The branch name comes from the provider; git decides whether it's a valid one.
        var check = await git.RunAsync(Path, ["check-ref-format", $"refs/heads/{baseBranch}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!check.Ok || baseBranch.StartsWith('-'))
        {
            throw new GitException($"'{baseBranch}' is not a branch name git accepts");
        }

        var baseLocal = $"refs/rtfd/base/{baseBranch}";
        var headLocal = $"refs/rtfd/pull/{number}";
        await git.RunCheckedAsync(Path,
        [
            "fetch", "--quiet", "--no-tags", "--no-recurse-submodules", "--force", "--no-write-fetch-head",
            source.Url,
            $"+refs/heads/{baseBranch}:{baseLocal}",
            $"+{source.HeadRef}:{headLocal}",
        ], source.Credential, timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken).ConfigureAwait(false);

        var head = await ResolveAsync(headLocal, cancellationToken).ConfigureAwait(false);
        var baseTip = await ResolveAsync(baseLocal, cancellationToken).ConfigureAwait(false);
        var mergeBase = (await git.RunCheckedAsync(Path, ["merge-base", baseTip, head], cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();
        return new ReviewCommits(head, baseTip, mergeBase);
    }

    /// <summary>A full SHA the mirror has as a commit, or null.</summary>
    public async Task<string?> TryResolveAsync(string sha, CancellationToken cancellationToken = default)
    {
        var result = await git.RunAsync(Path, ["rev-parse", "--verify", "--quiet", "--end-of-options", sha + "^{commit}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Ok ? result.Stdout.Trim() : null;
    }

    public async Task<string> MergeBaseAsync(string a, string b, CancellationToken cancellationToken = default) =>
        (await git.RunCheckedAsync(Path, ["merge-base", "--end-of-options", a, b], cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();

    public async Task<string> ResolveAsync(string revision, CancellationToken cancellationToken = default) =>
        (await git.RunCheckedAsync(Path, ["rev-parse", "--verify", "--end-of-options", revision + "^{commit}"], cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();

    /// <summary>A file's text at a commit, or null when the commit has no such file.</summary>
    public async Task<string?> ReadFileAsync(string commit, string path, CancellationToken cancellationToken = default)
    {
        var result = await git.RunAsync(Path, ["cat-file", "blob", $"{commit}:{path}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Ok ? result.Stdout : null;
    }

    /// <summary>Every file path in a commit's tree.</summary>
    public async Task<IReadOnlyList<string>> ListFilesAsync(string commit, CancellationToken cancellationToken = default)
    {
        var output = await git.RunCheckedAsync(Path, ["ls-tree", "-r", "-z", "--name-only", "--end-of-options", commit], cancellationToken: cancellationToken).ConfigureAwait(false);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
