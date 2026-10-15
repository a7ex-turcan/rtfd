namespace Rtfd.Git;

/// <summary>What a follow-up pass reviews (spec "Change detector").</summary>
/// <param name="LastHead">The head the last review was of.</param>
/// <param name="LastMergeBase">Its merge base then, once known.</param>
/// <param name="Interdiff">
/// What the author changed since the last review, without what came in from the base branch;
/// null when it can't be separated, and the whole PR is reviewed again.
/// </param>
/// <param name="FullReviewReason">Why the whole PR is reviewed again, when there is no interdiff.</param>
public sealed record FollowUpPass(string LastHead, string? LastMergeBase, DiffModel? Interdiff, string? FullReviewReason)
{
    /// <summary>The author changed nothing since the last review; at most the base branch moved.</summary>
    public bool Unchanged => Interdiff is { Files.Count: 0 };
}

/// <summary>
/// The interdiff, as the spec's three steps (decision 36): the PR diff as it was at the last
/// review (the old head against its merge base), applied onto the new merge base, then diffed
/// against the new head. A plain <c>git diff old new</c> would bring in every change from a base
/// branch the author merged. The apply is a three-way merge in the mirror (<c>git merge-tree
/// --merge-base</c>, git 2.40 or later), so no scratch worktree is needed. It doesn't need the old
/// head to be an ancestor of the new one, so a rebased PR gets an interdiff too, from the head rtfd
/// kept in its mirror.
/// </summary>
public static class Interdiff
{
    public static async Task<FollowUpPass> BuildAsync(
        GitRunner git, Mirror mirror, FetchSource source, int number, string lastHead, string? lastMergeBase, ReviewCommits commits,
        CancellationToken cancellationToken = default)
    {
        var last = await mirror.TryResolveAsync(lastHead, cancellationToken).ConfigureAwait(false);
        if (last is null && await mirror.FetchCommitAsync(source, number, lastHead, cancellationToken).ConfigureAwait(false))
        {
            last = await mirror.TryResolveAsync(lastHead, cancellationToken).ConfigureAwait(false);
        }

        if (last is null)
        {
            return new FollowUpPass(lastHead, lastMergeBase, null, $"the last reviewed head, {lastHead[..8]}, can't be fetched any more");
        }

        var oldBase = lastMergeBase is { } recorded ? await mirror.TryResolveAsync(recorded, cancellationToken).ConfigureAwait(false) : null;
        if (oldBase is null)
        {
            var mergeBase = await git.RunAsync(mirror.Path, ["merge-base", "--end-of-options", last, commits.BaseTip], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!mergeBase.Ok)
            {
                return new FollowUpPass(last, null, null, $"the last reviewed head, {last[..8]}, shares no history with the base branch");
            }

            oldBase = mergeBase.Stdout.Trim();
        }

        // Steps 1 and 2: the old PR diff onto the new merge base, as a merge of the old head into it.
        var merge = await git.RunAsync(mirror.Path,
            ["merge-tree", "--write-tree", "--no-messages", $"--merge-base={oldBase}", commits.MergeBase, last],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (merge.ExitCode == 1 && !merge.TimedOut)
        {
            return new FollowUpPass(last, oldBase, null, $"the changes reviewed at {last[..8]} don't apply cleanly onto the base branch as it is now");
        }

        if (!merge.Ok)
        {
            var message = merge.Stderr.Trim();
            throw new GitException($"git merge-tree failed ({merge.ExitCode}){(message.Length > 0 ? ": " + message : "")}. The interdiff needs git 2.40 or later.");
        }

        // Step 3: what the new head has beyond that.
        var tree = merge.Stdout.Split('\n')[0].Trim();
        var interdiff = await DiffModel.ReadAsync(git, mirror.Path, tree, commits.Head, cancellationToken).ConfigureAwait(false);
        return new FollowUpPass(last, oldBase, interdiff, null);
    }
}
