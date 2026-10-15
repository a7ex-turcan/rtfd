using Rtfd.Agents;
using Rtfd.Config;
using Rtfd.Git;
using Rtfd.GitTools;
using Rtfd.Posting;
using Rtfd.Processes;
using Rtfd.Providers;
using Rtfd.Review;
using Rtfd.Review.Secrets;

namespace Rtfd.Runs;

public sealed record RunRequest
{
    public required RtfdConfig Config { get; init; }
    public required RepoConfig Repo { get; init; }
    public required AgentConfig Agent { get; init; }
    public required int Number { get; init; }

    /// <summary>Stop after the inputs: no agent.</summary>
    public bool InputsOnly { get; init; }

    /// <summary>Leave the run folder and the worktree for a look afterwards.</summary>
    public bool Keep { get; init; }

    public required Func<string, string?> Environment { get; init; }
    public required Func<RepoConfig, ProviderToken, IProvider> Providers { get; init; }

    /// <summary>The rtfd binary <c>mcp.json</c> starts as <c>rtfd-git</c>, by absolute path.</summary>
    public required string RtfdExecutable { get; init; }

    /// <summary>Progress, to stderr.</summary>
    public required TextWriter Log { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;
    public Func<TimeSpan, Task> Delay { get; init; } = t => Task.Delay(t);

    /// <summary>The state store's say in a live run: whether this head may be reviewed, and each attempt counted.</summary>
    public IRunHooks? Hooks { get; init; }

    /// <summary>What rtfd reviewed on the PR before, for a follow-up (plan Phase 4). Without it every review is a first one.</summary>
    public IReviewHistory? History { get; init; }
}

/// <param name="Refusal">Why the head isn't reviewed (already reviewed, attempts used up, blocked), or null.</param>
/// <param name="Attempts">How many agent attempts this run may use.</param>
public sealed record RunGate(string? Refusal, int Attempts);

/// <summary>What a live run tells the state store, and what the store tells it back (plan Phase 3).</summary>
public interface IRunHooks
{
    /// <summary>Once the head and the prompt are known, before the worktree and the agent.</summary>
    Task<RunGate> BeforeReviewAsync(PullRequest pullRequest, ReviewCommits commits, string promptHash, CancellationToken cancellationToken);

    /// <summary>After each agent attempt. A usage limit doesn't count (spec "Agent adapter").</summary>
    Task AttemptFinishedAsync(OutcomeKind kind, CancellationToken cancellationToken);
}

/// <param name="RawText">What the agent answered, with every secret rtfd found redacted.</param>
public sealed record AttemptReport(int Number, OutcomeKind Kind, string? Message, IReadOnlyList<string> Errors, string RawText);

/// <summary>Everything one run saw and decided, for the dry-run printout and the tests.</summary>
public sealed record RunReport
{
    public OutcomeKind? Outcome { get; set; }
    public string? Message { get; set; }
    public PullRequest? PullRequest { get; set; }
    public ReviewCommits? Commits { get; set; }
    public DiffModel? Diff { get; set; }
    public CiState? Ci { get; set; }
    public ConfigReset? ConfigReset { get; set; }
    public IReadOnlyList<ReviewThread> Threads { get; set; } = [];

    /// <summary>What rtfd reviewed on the PR before, when the run had a history to ask.</summary>
    public PriorReview? Prior { get; set; }

    /// <summary>rtfd's threads from earlier reviews, joined with the provider's.</summary>
    public IReadOnlyList<OwnThread> OwnThreads { get; set; } = [];

    /// <summary>Set on a follow-up: the last reviewed head, and the interdiff or why there is none.</summary>
    public FollowUpPass? FollowUp { get; set; }

    /// <summary>Set when the author changed nothing since the last review, so there is nothing to review.</summary>
    public string? Unchanged { get; set; }

    public string? PostingIdentity { get; set; }
    public string? PromptHash { get; set; }
    public RunFolder? RunFolder { get; set; }
    public List<AttemptReport> Attempts { get; } = [];
    public ReviewPlan? Plan { get; set; }
    public LimitHit? Limit { get; set; }

    /// <summary>Set when the run stopped before the agent: policy, or a problem with the setup.</summary>
    public string? Refused { get; set; }

    /// <summary>
    /// The refusal is about this PR at this head (already reviewed, attempts used up, blocked,
    /// nothing to review, an author or fork rtfd skips): asking again changes nothing until the PR
    /// does. Otherwise it is about the setup (a token, the prompt file), which can be fixed.
    /// </summary>
    public bool RefusedForHead { get; set; }
}

/// <summary>
/// One review of one PR head, end to end (spec "Run manager"; plan Phases 1 and 4): the
/// provider's read side, the mirror, the diff model, what rtfd reviewed before and the interdiff,
/// the worktree with its agent configuration reset, the run folder, the agent with retries, the
/// validator, rtfd's own findings, the secret check and the verdict. Posting is the caller's. The
/// run folder and the worktree go when it ends, timeout kills included, unless
/// <see cref="RunRequest.Keep"/>.
/// </summary>
public static class ReviewRun
{
    public static async Task<RunReport> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        var report = new RunReport();
        var (repo, agent, log) = (request.Repo, request.Agent, request.Log);

        var tokenValue = request.Environment(repo.TokenEnv);
        if (string.IsNullOrEmpty(tokenValue))
        {
            report.Refused = $"{repo.TokenEnv} isn't set. It should hold the token rtfd reads {repo.Slug} with (for GitHub, `gh auth token` prints one).";
            return report;
        }

        // Every token rtfd holds, for the secret check and the environment filter: not just this repo's.
        var tokens = request.Config.Repos
            .Select(r => request.Environment(r.TokenEnv))
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct()
            .ToList();

        var provider = request.Providers(repo, new ProviderToken(tokenValue));
        try
        {
            log.WriteLine($"Reading PR #{request.Number} from {repo.Slug}...");
            report.PostingIdentity = await provider.WhoAmIAsync(cancellationToken).ConfigureAwait(false);
            var pr = report.PullRequest = await provider.GetPullRequestAsync(request.Number, cancellationToken).ConfigureAwait(false);
            if (Refusal(repo, pr) is { } refusal)
            {
                report.Refused = refusal;
                report.RefusedForHead = true;
                return report;
            }

            report.Threads = await provider.GetReviewThreadsAsync(pr.Number, cancellationToken).ConfigureAwait(false);

            var git = new GitRunner(request.Config.Data.GitDirectory, request.Environment, tokens);
            var mirror = Mirror.For(request.Config.Data, repo.Name, git);
            log.WriteLine($"Fetching into {mirror.Path}...");
            var commits = report.Commits = await mirror.FetchAsync(provider.FetchSource(pr), pr.Number, pr.BaseRef, cancellationToken).ConfigureAwait(false);
            if (commits.Head != pr.HeadSha)
            {
                log.WriteLine($"The PR moved while rtfd read it; reviewing the fetched head {commits.Head[..8]}.");
            }

            // A merged PR's head is in today's base branch, so its diff against today's base is
            // empty. Review it against the base it was compared with instead.
            if (pr.State != "open" && pr.BaseSha.Length > 0 && await mirror.TryResolveAsync(pr.BaseSha, cancellationToken).ConfigureAwait(false) is { } oldBase && oldBase != commits.BaseTip)
            {
                commits = report.Commits = commits with
                {
                    BaseTip = oldBase,
                    MergeBase = await mirror.MergeBaseAsync(oldBase, commits.Head, cancellationToken).ConfigureAwait(false),
                };
                log.WriteLine($"The PR is {pr.State}: reviewing it against its base as it was ({oldBase[..8]}).");
            }

            report.Ci = await provider.GetCiStateAsync(commits.Head, cancellationToken).ConfigureAwait(false);
            var diff = report.Diff = (await DiffModel.ReadAsync(git, mirror.Path, commits.MergeBase, commits.Head, cancellationToken).ConfigureAwait(false))
                .Apply(repo.Settings.Exclude, repo.Settings.MaxDiffLines);

            if (diff.Files.Count == 0)
            {
                report.Refused = $"the PR has no changes against {pr.BaseRef}: its head is already there";
                report.RefusedForHead = true;
                return report;
            }

            // What rtfd reviewed before: its threads, and on a new head the interdiff (plan Phase 4).
            if (request.History is { } history)
            {
                var prior = report.Prior = await history.LoadAsync(repo, pr.Number, report.PostingIdentity, provider, report.Threads, cancellationToken).ConfigureAwait(false);
                report.OwnThreads = OwnThreads.Join(prior.Threads, report.Threads, report.PostingIdentity, repo);
                if (prior.Last is { } last && last.Head != commits.Head)
                {
                    log.WriteLine($"Following up on the review of {last.Head[..8]}...");
                    var pass = await Interdiff.BuildAsync(git, mirror, provider.FetchSource(pr), pr.Number, last.Head, last.MergeBase, commits, cancellationToken).ConfigureAwait(false);
                    report.FollowUp = pass.Interdiff is { } interdiff ? pass with { Interdiff = interdiff.Apply(repo.Settings.Exclude, repo.Settings.MaxDiffLines) } : pass;
                }
            }

            var promptFile = ReadPrompt(repo);
            if (promptFile is null)
            {
                report.Refused = $"the prompt file {repo.PromptPath} can't be read";
                return report;
            }

            report.PromptHash = PromptRenderer.Hash(promptFile);
            var attempts = repo.Settings.MaxAttempts;
            if (request.Hooks is { } hooks && !request.InputsOnly)
            {
                var gate = await hooks.BeforeReviewAsync(pr, commits, report.PromptHash, cancellationToken).ConfigureAwait(false);
                if (gate.Refusal is { } refused)
                {
                    report.Refused = refused;
                    report.RefusedForHead = true;
                    return report;
                }

                attempts = gate.Attempts;

                // Kept in the mirror, so the next follow-up's interdiff can start here even after a force-push.
                await mirror.KeepAsync(pr.Number, commits.Head, cancellationToken).ConfigureAwait(false);
            }

            if (report.FollowUp is { Unchanged: true } unchanged)
            {
                report.Unchanged = $"since the last review, of {unchanged.LastHead[..8]}, the author changed nothing; only the base branch moved";
                return report;
            }

            var run = report.RunFolder = RunFolder.Create(request.Config.Data, repo.Name, pr.Number, commits.Head);
            Worktree? worktree = null;
            try
            {
                worktree = await Worktree.CreateAsync(git, mirror, run.WorktreePath, commits.Head, cancellationToken).ConfigureAwait(false);
                report.ConfigReset = await worktree.ResetAgentConfigAsync(agent.ProjectConfig, commits.BaseTip, commits.Head, diff, cancellationToken).ConfigureAwait(false);

                run.WriteInputs(pr, commits, diff, report.Ci, report.OwnThreads, report.FollowUp);

                // rtfd-git may name the last reviewed head and its commits too, once the mirror has them.
                var follow = report.FollowUp is { LastMergeBase: not null } f ? f : null;
                new GitToolsBinding(mirror.Path, worktree.Path, commits.Head, commits.MergeBase, follow?.LastHead, request.Config.Data.GitDirectory)
                {
                    LastMergeBase = follow?.LastMergeBase,
                }.Write(run.Path);
                File.WriteAllText(run.SettingsPath, ClaudeAdapter.Settings());
                File.WriteAllText(run.McpConfigPath, ClaudeAdapter.McpConfig(request.RtfdExecutable, run.Path));

                if (request.InputsOnly)
                {
                    File.WriteAllText(run.PromptPath, PromptRenderer.Render(promptFile, run, diff, report.FollowUp, report.OwnThreads));
                    return report;
                }

                await ReviewAsync(request, report, run, mirror, commits, promptFile, tokens, attempts, cancellationToken).ConfigureAwait(false);
                return report;
            }
            finally
            {
                if (!request.Keep)
                {
                    if (worktree is not null)
                    {
                        await worktree.RemoveAsync().ConfigureAwait(false);
                    }

                    await Folders.DeleteAsync(run.WorktreePath).ConfigureAwait(false);
                    await Folders.DeleteAsync(run.Path).ConfigureAwait(false);
                }
                else if (Directory.Exists(run.Path))
                {
                    // Kept on purpose: serve's startup sweep leaves it. Written last, so a run killed midway has none.
                    File.WriteAllText(run.KeptMarkerPath, "");
                }
            }
        }
        catch (ProviderException ex)
        {
            report.Outcome = ex.IsAuth ? OutcomeKind.Fatal : OutcomeKind.Transient;
            report.Message = ex.Message;
            return report;
        }
        catch (GitException ex)
        {
            report.Outcome = OutcomeKind.Transient;
            report.Message = ex.Message;
            return report;
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    /// <summary>Whose PRs get reviewed is a security property: outside text never reaches the agent (spec decision 12).</summary>
    public static string? Refusal(RepoConfig repo, PullRequest pr)
    {
        if (!repo.Authors.Contains("*") && !repo.Authors.Contains(pr.Author, StringComparer.OrdinalIgnoreCase))
        {
            return $"{pr.Author} isn't in {repo.Name}'s authors, so rtfd won't review this PR";
        }

        return pr.IsFork ? "the PR comes from a fork, and rtfd skips forks" : null;
    }

    private static async Task ReviewAsync(
        RunRequest request, RunReport report, RunFolder run, Mirror mirror, ReviewCommits commits, string promptFile,
        IReadOnlyList<string> tokens, int attempts, CancellationToken cancellationToken)
    {
        var (repo, agent) = (request.Repo, request.Agent);
        var diff = report.Diff!;
        var scanner = SecretScanner.Create(request.Config.Secrets.Extra, tokens);
        var environment = ChildEnvironment.Build(request.Environment, tokens);
        var paths = new AgentRunPaths(run.Path, run.WorktreePath, run.PromptPath, run.SettingsPath, run.McpConfigPath);
        var context = new ReviewContext
        {
            PullRequest = report.PullRequest!,
            Diff = diff,
            Ci = report.Ci!,
            ConfigReset = report.ConfigReset!,
            Repo = repo,
            PostingIdentity = report.PostingIdentity!,
            OwnThreads = report.OwnThreads,
        };

        async Task<string?> ReadFile(DiffSide side, string path)
        {
            try
            {
                return await mirror.ReadFileAsync(side == DiffSide.New ? commits.Head : commits.MergeBase, GitToolset.CheckPath(path), cancellationToken).ConfigureAwait(false);
            }
            catch (ToolRefusal)
            {
                return null;
            }
        }

        async Task Record(AttemptReport attempt)
        {
            report.Attempts.Add(attempt);
            if (request.Hooks is { } hooks)
            {
                await hooks.AttemptFinishedAsync(attempt.Kind, cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<string>? rejection = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var prompt = PromptRenderer.Render(promptFile, run, diff, report.FollowUp, report.OwnThreads, rejection);
            File.WriteAllText(run.PromptPath, prompt);
            request.Log.WriteLine($"Attempt {attempt} of {attempts}: running {agent.Name}...");

            var outcome = await AgentRunner.RunAsync(agent, paths, prompt, environment, repo.Settings.Timeout,
                request.Clock.GetUtcNow(), request.Zone, cancellationToken).ConfigureAwait(false);
            var raw = Redacted(outcome.Text ?? outcome.Process.Stdout + outcome.Process.Stderr, scanner);

            if (outcome.Kind != OutcomeKind.Ok)
            {
                await Record(new AttemptReport(attempt, outcome.Kind, outcome.Message, [], raw)).ConfigureAwait(false);
                report.Outcome = outcome.Kind;
                report.Message = outcome.Message;
                report.Limit = outcome.Limit;
                if (outcome.Kind == OutcomeKind.Transient && attempt < attempts)
                {
                    await request.Delay(TimeSpan.FromSeconds(10 * attempt)).ConfigureAwait(false);
                    continue;
                }

                return;
            }

            var errors = new List<string>();
            ValidationResult? validation = null;
            FindingsDocument? document = null;
            if (OutputExtractor.Document(outcome.Text!) is not { } json)
            {
                errors.Add("there is no findings document in your answer. End it with one JSON object in a ```json block.");
            }
            else
            {
                var parsed = FindingsDocument.Parse(json);
                errors.AddRange(parsed.Errors);
                document = parsed.Document;
                if (document is not null)
                {
                    validation = await Validator.ValidateAsync(document, diff, ReadFile, report.OwnThreads).ConfigureAwait(false);
                    errors.AddRange(validation.Errors);
                }
            }

            if (errors.Count > 0)
            {
                await Record(new AttemptReport(attempt, OutcomeKind.Invalid, "the findings document was rejected", errors, raw)).ConfigureAwait(false);
                report.Outcome = OutcomeKind.Invalid;
                report.Message = "the findings document was rejected on every attempt";
                rejection = errors;
                continue;
            }

            var plan = report.Plan = ReviewBuilder.Build(document!, validation!, context, scanner);
            await Record(new AttemptReport(attempt, OutcomeKind.Ok, null, [], raw)).ConfigureAwait(false);
            if (plan.Blocked)
            {
                // Not retried: a retry would likely do the same (spec "Validator").
                report.Outcome = OutcomeKind.Fatal;
                report.Message = "the review contains a secret that isn't in the diff, so nothing would post";
                return;
            }

            report.Outcome = OutcomeKind.Ok;
            report.Message = null;
            return;
        }
    }

    private static string? ReadPrompt(RepoConfig repo)
    {
        try
        {
            return File.ReadAllText(repo.PromptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The agent's raw answer, safe to print: every secret the scanner finds is redacted.</summary>
    private static string Redacted(string text, SecretScanner scanner) => scanner.RedactAll(text);
}
