using Rtfd.Config;
using Rtfd.Providers;
using Rtfd.Serve;
using Rtfd.Tests.Support;

namespace Rtfd.Tests;

/// <summary>The change detector on its own (spec "Change detector"): events in, readiness out.</summary>
public sealed class ChangeDetectorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly TempHome _home = new();
    private readonly RepoConfig _repo;

    public ChangeDetectorTests()
    {
        var loaded = _home.Load(Configs.Minimal(repoExtra: "    quiet_period: 3m\n    ci_max_wait: 30m"));
        _repo = loaded.Config!.Repos[0];
    }

    public void Dispose() => _home.Dispose();

    private static PullRequest Pr(string head, string author = "owner", bool draft = false) =>
        FakeProvider.Pr(head, author: author) with { IsDraft = draft };

    [Fact]
    public void Only_a_push_resets_the_quiet_period()
    {
        var detector = new ChangeDetector(_repo);
        detector.Poll([Pr("a1")], T0);
        detector.Apply(new CiSeen(7, "a1", CiSummary.Pending, T0.AddMinutes(1)));
        detector.Apply(new CiSeen(7, "a1", CiSummary.Success, T0.AddMinutes(2)));
        detector.Poll([Pr("a1") with { Title = "Renamed" }], T0.AddMinutes(2));

        Assert.Equal(new PrReadiness(Readiness.Quiet, Until: T0.AddMinutes(3)), detector.Readiness(7, T0.AddMinutes(2)));
        Assert.Equal(Readiness.Due, detector.Readiness(7, T0.AddMinutes(3)).State);

        var note = Assert.Single(detector.Poll([Pr("b2")], T0.AddMinutes(4)));
        Assert.Equal("#7 has a new head, b2", note);
        Assert.Equal(new PrReadiness(Readiness.Quiet, Until: T0.AddMinutes(7)), detector.Readiness(7, T0.AddMinutes(4)));
    }

    [Fact]
    public void A_new_head_forgets_CI_and_a_result_for_an_old_head_is_ignored()
    {
        var detector = new ChangeDetector(_repo);
        detector.Poll([Pr("a1")], T0);
        detector.Apply(new CiSeen(7, "a1", CiSummary.Success, T0));
        detector.Poll([Pr("b2")], T0.AddMinutes(1));
        detector.Apply(new CiSeen(7, "a1", CiSummary.Failure, T0.AddMinutes(2)));

        Assert.Equal(Readiness.NeedsCi, detector.Readiness(7, T0.AddMinutes(10)).State);
    }

    [Fact]
    public void Pending_CI_waits_until_ci_max_wait_from_the_push()
    {
        var detector = new ChangeDetector(_repo);
        detector.Poll([Pr("a1")], T0);
        detector.Apply(new CiSeen(7, "a1", CiSummary.Pending, T0.AddMinutes(5)));

        Assert.Equal(new PrReadiness(Readiness.WaitingForCi, Until: T0.AddMinutes(30)), detector.Readiness(7, T0.AddMinutes(5)));
        Assert.Equal(new PrReadiness(Readiness.Due, CiUnfinished: true), detector.Readiness(7, T0.AddMinutes(30)));
    }

    [Fact]
    public void No_checks_means_nothing_to_wait_for()
    {
        var detector = new ChangeDetector(_repo);
        detector.Poll([Pr("a1")], T0);
        detector.Apply(new CiSeen(7, "a1", CiSummary.None, T0.AddMinutes(3)));

        Assert.Equal(new PrReadiness(Readiness.Due), detector.Readiness(7, T0.AddMinutes(3)));
    }

    [Theory]
    [InlineData("stranger", false, "stranger isn't in nutrx's authors")]
    [InlineData("owner", true, "it's a draft")]
    public void Drafts_and_authors_outside_the_allowlist_are_skipped(string author, bool draft, string reason)
    {
        var detector = new ChangeDetector(_repo);
        detector.Poll([Pr("a1", author, draft)], T0);

        var readiness = detector.Readiness(7, T0.AddHours(1));

        Assert.Equal(Readiness.Skipped, readiness.State);
        Assert.StartsWith(reason, readiness.Reason);
    }

    [Fact]
    public void A_PR_missing_from_a_poll_is_closed()
    {
        var detector = new ChangeDetector(_repo);
        detector.Poll([Pr("a1")], T0);

        var notes = detector.Poll([], T0.AddMinutes(5));

        Assert.Equal(["#7 was closed or merged"], notes);
        Assert.Empty(detector.Open);
    }
}
