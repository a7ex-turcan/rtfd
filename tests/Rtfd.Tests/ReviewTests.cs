using Rtfd.Config;
using Rtfd.Git;
using Rtfd.Providers;
using Rtfd.Review;
using Rtfd.Review.Secrets;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

public sealed class FindingsDocumentTests
{
    [Fact]
    public void A_good_document_parses()
    {
        var (document, errors) = FindingsDocument.Parse("""
            {
              "schema_version": 1,
              "summary": "One Should.",
              "findings": [
                {"severity": "should", "path": "a.py", "line": 3, "side": "new", "anchor": "x = 1", "body": "Should: rename x."},
                {"severity": "could", "path": null, "body": "Could: mention the migration."}
              ],
              "threads": [{"id": "T1", "action": "resolve", "reply": "Looks good now."}]
            }
            """);

        Assert.Empty(errors);
        Assert.Equal(2, document!.Findings.Count);
        Assert.Equal(FindingSeverity.Could, document.Findings[1].Severity);
        Assert.Null(document.Findings[1].Path);
        Assert.Equal(ThreadActionKind.Resolve, document.Threads[0].Action);
    }

    [Fact]
    public void A_verdict_field_is_refused_with_the_reason()
    {
        var (_, errors) = FindingsDocument.Parse("""{"schema_version": 1, "summary": "", "findings": [], "verdict": "approve"}""");

        Assert.Contains(errors, e => e.Contains("rtfd decides the verdict", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"summary": "", "findings": []}""", "schema_version must be 1")]
    [InlineData("""{"schema_version": 2, "summary": "", "findings": []}""", "schema_version must be 1")]
    [InlineData("""{"schema_version": 1, "findings": []}""", "summary is required")]
    [InlineData("""{"schema_version": 1, "summary": ""}""", "findings must be an array")]
    [InlineData("""{"schema_version": 1, "summary": "", "findings": [{"severity": "blocker", "body": "x"}]}""", "severity must be should, could or minor")]
    [InlineData("""{"schema_version": 1, "summary": "", "findings": [{"severity": "should", "path": "a.py", "body": "x"}]}""", "needs an anchor")]
    [InlineData("""{"schema_version": 1, "summary": "", "findings": [{"severity": "should", "path": "a.py", "anchor": "a", "side": "left", "body": "x"}]}""", "side must be new")]
    [InlineData("""{"schema_version": 1, "summary": "", "findings": [{"severity": "should", "body": "x", "suggestion": "y"}]}""", "unknown field 'findings[0].suggestion'")]
    [InlineData("""{"schema_version": 1, "summary": "", "findings": [], "threads": [{"id": "1", "action": "reply"}]}""", "needs the reply text")]
    [InlineData("""not json""", "isn't valid JSON")]
    [InlineData("""[1, 2]""", "must be one JSON object")]
    public void Each_mistake_gets_a_message_a_model_can_act_on(string json, string expected)
    {
        var (document, errors) = FindingsDocument.Parse(json);

        Assert.Null(document);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }
}

public sealed class OutputExtractorTests
{
    [Fact]
    public void The_result_comes_out_of_claude_s_wrapper() =>
        Assert.Equal("hello", Agents.OutputExtractor.Select("""{"type": "result", "result": "hello"}""", ".result"));

    [Fact]
    public void The_last_fenced_block_that_parses_is_the_document()
    {
        var text = """
            For example:
            ```json
            {"example": true}
            ```
            My review:
            ```json
            {"schema_version": 1}
            ```
            Thanks.
            """;

        Assert.Equal("""{"schema_version": 1}""", Agents.OutputExtractor.Document(text));
    }

    [Fact]
    public void A_bare_object_is_the_document_and_prose_alone_is_none()
    {
        Assert.Equal("""{"a": 1}""", Agents.OutputExtractor.Document("""  {"a": 1}  """));
        Assert.Null(Agents.OutputExtractor.Document("I looked and it's fine."));
    }
}

public sealed class ValidatorTests
{
    private const string Head = "def f():\n    total = 0\n    for x in items:\n        total += x\n    return total\n";
    private const string Base = "def f():\n    return sum(items)\n";

    private static DiffModel Diff() => DiffModel.Parse("M\0a.py\0", """
        diff --git a/a.py b/a.py
        --- a/a.py
        +++ b/a.py
        @@ -1,2 +1,5 @@
         def f():
        -    return sum(items)
        +    total = 0
        +    for x in items:
        +        total += x
        +    return total

        """);

    private static Task<string?> Read(DiffSide side, string path) =>
        Task.FromResult<string?>(path != "a.py" ? null : side == DiffSide.New ? Head : Base);

    private static async Task<ValidationResult> ValidateAsync(params Finding[] findings) =>
        await Validator.ValidateAsync(new FindingsDocument("s", findings, []), Diff(), Read, new HashSet<string>());

    [Fact]
    public async Task A_wrong_line_snaps_to_the_anchor()
    {
        var result = await ValidateAsync(new Finding(FindingSeverity.Should, "a.py", 2, DiffSide.New, "total += x", "Should: x"));

        var comment = Assert.Single(result.Inline);
        Assert.Equal((4, 2), (comment.Line, comment.ClaimedLine!.Value));
    }

    [Fact]
    public async Task An_anchor_on_a_removed_line_works_on_the_old_side()
    {
        var result = await ValidateAsync(new Finding(FindingSeverity.Could, "a.py", null, DiffSide.Old, "return sum(items)", "Could: x"));

        Assert.Equal((2, DiffSide.Old), (result.Inline[0].Line, result.Inline[0].Side));
    }

    [Fact]
    public async Task An_anchor_copied_from_the_diff_with_its_marker_still_matches()
    {
        var result = await ValidateAsync(new Finding(FindingSeverity.Should, "a.py", 3, DiffSide.New, "+    for x in   items:", "Should: x"));

        Assert.Equal(3, Assert.Single(result.Inline).Line);
    }

    [Fact]
    public async Task A_multi_line_anchor_points_at_its_first_line()
    {
        var result = await ValidateAsync(new Finding(FindingSeverity.Should, "a.py", null, DiffSide.New, "for x in items:\n        total += x", "Should: x"));

        Assert.Equal(3, Assert.Single(result.Inline).Line);
    }

    [Fact]
    public async Task An_anchor_that_isn_t_there_rejects_the_document()
    {
        var missing = await ValidateAsync(new Finding(FindingSeverity.Should, "a.py", 2, DiffSide.New, "nowhere()", "Should: x"));
        var noFile = await ValidateAsync(new Finding(FindingSeverity.Should, "b.py", 2, DiffSide.New, "x", "Should: x"));

        Assert.Contains("isn't in a.py", Assert.Single(missing.Errors), StringComparison.Ordinal);
        Assert.Contains("there is no b.py", Assert.Single(noFile.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anchor_outside_every_hunk_is_folded_into_the_summary()
    {
        var file = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
        var diff = DiffModel.Parse("M\0b.txt\0", "diff --git a/b.txt b/b.txt\n--- a/b.txt\n+++ b/b.txt\n@@ -1,1 +1,1 @@\n-line one\n+line 1\n");
        var result = await Validator.ValidateAsync(
            new FindingsDocument("s", [new Finding(FindingSeverity.Minor, "b.txt", 25, DiffSide.New, "line 25", "MINOR: x")], []),
            diff, (_, _) => Task.FromResult<string?>(file), new HashSet<string>());

        Assert.Empty(result.Inline);
        Assert.Equal(("b.txt", 25), (result.Folded[0].Path, result.Folded[0].Line));
    }

    [Fact]
    public async Task A_thread_that_isn_t_rtfd_s_own_is_refused()
    {
        var result = await Validator.ValidateAsync(
            new FindingsDocument("s", [], [new ThreadAction("PRRT_someone", ThreadActionKind.Resolve, null)]),
            Diff(), Read, new HashSet<string>());

        Assert.Contains("isn't one of your threads", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FindingSeverity.Should, "this crashes", "Should: this crashes")]
    [InlineData(FindingSeverity.Could, "Could: rename", "Could: rename")]
    [InlineData(FindingSeverity.Minor, "minor: typo", "minor: typo")]
    [InlineData(FindingSeverity.Minor, "typo", "MINOR: typo")]
    public void Every_body_leads_with_its_severity(FindingSeverity severity, string body, string expected) =>
        Assert.Equal(expected, Validator.Labelled(severity, body));
}

public sealed class VerdictTests
{
    private static RepoConfig Repo(params Verdict[] verdicts) => new()
    {
        Name = "repo",
        Path = "repos[0]",
        Provider = ProviderKind.GitHub,
        Slug = "owner/repo",
        Trust = TrustTier.Trusted,
        Agent = "claude",
        PromptPath = "p.md",
        Authors = ["owner"],
        Verdicts = verdicts,
        TokenEnv = "GH_TOKEN",
        Hooks = [],
        Settings = new RepoSettings(),
    };

    [Fact]
    public void An_open_Should_means_not_ready()
    {
        var decision = ReviewBuilder.Decide([FindingSeverity.Could, FindingSeverity.Should], false, Repo(Verdict.Comment, Verdict.Approve, Verdict.RequestChanges), "author", "bot");

        Assert.Equal((false, Verdict.RequestChanges), (decision.Ready, decision.Event));
    }

    [Fact]
    public void Coulds_and_minors_alone_are_ready()
    {
        var decision = ReviewBuilder.Decide([FindingSeverity.Could, FindingSeverity.Minor], false, Repo(Verdict.Comment, Verdict.Approve), "author", "bot");

        Assert.Equal((true, Verdict.Approve), (decision.Ready, decision.Event));
    }

    [Fact]
    public void A_partial_review_is_never_ready()
    {
        var decision = ReviewBuilder.Decide([], true, Repo(Verdict.Comment, Verdict.Approve, Verdict.RequestChanges), "author", "bot");

        Assert.Equal((false, Verdict.RequestChanges), (decision.Ready, decision.Event));
        Assert.Contains("partial", decision.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_verdict_the_repo_doesn_t_allow_becomes_a_comment()
    {
        var decision = ReviewBuilder.Decide([], false, Repo(Verdict.Comment), "author", "bot");

        Assert.Equal((true, Verdict.Comment), (decision.Ready, decision.Event));
    }

    [Fact]
    public void Approving_your_own_PR_is_downgraded_to_a_comment()
    {
        var decision = ReviewBuilder.Decide([], false, Repo(Verdict.Comment, Verdict.Approve), "Owner", "owner");

        Assert.Equal(Verdict.Comment, decision.Event);
        Assert.Contains("APPROVE downgraded to COMMENT: the posting identity is the PR author", decision.Why, StringComparison.Ordinal);
    }
}

public sealed class SecretScannerTests
{
    [Fact]
    public void Every_bundled_gitleaks_rule_compiles_with_the_linear_time_engine()
    {
        var scanner = SecretScanner.Create([], []);

        Assert.True(SecretScanner.BundledRuleCount >= 200, $"{SecretScanner.BundledRuleCount} rules");
        Assert.Empty(scanner.Scan("nothing secret here, just a sentence about rate limits"));
    }

    [Fact]
    public void A_GitHub_token_is_found_and_redacted()
    {
        var token = Tokens.GitHubLike();
        var scanner = SecretScanner.Create([], []);

        var match = Assert.Single(scanner.Scan($"const token = \"{token}\";"));

        Assert.Equal(("github-pat", token), (match.Rule, match.Secret));
        Assert.Equal("ghp_…REDACTED", SecretScanner.Redact(token));
    }

    [Fact]
    public void A_low_entropy_lookalike_is_not_a_secret() =>
        Assert.Empty(SecretScanner.Create([], []).Scan("ghp_" + new string('a', 36)));

    [Fact]
    public void A_token_rtfd_holds_is_found_whatever_its_shape()
    {
        var held = "plain-looking-value-" + Tokens.Random(12);
        var scanner = SecretScanner.Create([], [held]);

        var match = Assert.Single(scanner.Scan($"see {held} here"));

        Assert.Equal("rtfd-token", match.Rule);
    }

    [Fact]
    public void Extra_patterns_from_the_config_apply()
    {
        var scanner = SecretScanner.Create([new ConfigPattern(@"INTERNAL-[0-9]{6}", "defaults.secrets.extra[0]")], []);

        Assert.Equal("extra[0]", Assert.Single(scanner.Scan("key INTERNAL-123456")).Rule);
    }

    [Fact]
    public void Scanning_a_large_hostile_text_stays_fast()
    {
        var scanner = SecretScanner.Create([], []);
        var text = string.Concat(Enumerable.Repeat("api_key = \"" + new string('=', 200) + "\" token: secret password=", 2000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        scanner.Scan(text);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }
}
