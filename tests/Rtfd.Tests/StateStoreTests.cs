using Microsoft.Data.Sqlite;
using Rtfd.State;

namespace Rtfd.Tests;

/// <summary>The state store's schema and its migrations (CLAUDE.md "SQLite").</summary>
public sealed class StateStoreTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private string DbPath => Path.Combine(_home.Root, "state.db");

    [Fact]
    public void A_new_file_is_created_at_the_current_version()
    {
        using var store = StateStore.Open(DbPath);

        Assert.Equal(StateStore.CurrentSchemaVersion, store.SchemaVersion);
    }

    [Fact]
    public void A_version_1_file_from_Phase_3_is_migrated_and_keeps_its_postings()
    {
        // The shape Phase 3 shipped, with one landed posting in it.
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meta VALUES ('schema_version', '1');
                CREATE TABLE prs (repo TEXT NOT NULL, pr INTEGER NOT NULL, last_reviewed_head TEXT, PRIMARY KEY (repo, pr));
                CREATE TABLE runs (id INTEGER PRIMARY KEY AUTOINCREMENT, repo TEXT NOT NULL, pr INTEGER NOT NULL, head TEXT NOT NULL,
                  started_at TEXT NOT NULL, finished_at TEXT, outcome TEXT, message TEXT, prompt_hash TEXT);
                CREATE TABLE attempts (repo TEXT NOT NULL, pr INTEGER NOT NULL, head TEXT NOT NULL, used INTEGER NOT NULL DEFAULT 0, blocked TEXT,
                  PRIMARY KEY (repo, pr, head));
                CREATE TABLE postings (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL REFERENCES runs (id), repo TEXT NOT NULL,
                  pr INTEGER NOT NULL, head TEXT NOT NULL, identity TEXT NOT NULL, event TEXT NOT NULL, started_at TEXT NOT NULL, state TEXT NOT NULL,
                  review_id TEXT, review_url TEXT, message TEXT, completed_at TEXT);
                CREATE TABLE posted_bodies (posting_id INTEGER NOT NULL REFERENCES postings (id), ordinal INTEGER NOT NULL, path TEXT, line INTEGER,
                  side TEXT, severity TEXT, body TEXT NOT NULL, body_hash TEXT NOT NULL, comment_id TEXT, PRIMARY KEY (posting_id, ordinal));
                INSERT INTO runs (repo, pr, head, started_at) VALUES ('repo', 7, 'abc', '2026-10-13T10:00:00.000Z');
                INSERT INTO postings (run_id, repo, pr, head, identity, event, started_at, state, review_id)
                  VALUES (1, 'repo', 7, 'abc', 'owner', 'COMMENT', '2026-10-13T10:00:00.000Z', 'verified', '1000');
                INSERT INTO posted_bodies VALUES (1, 0, NULL, NULL, NULL, NULL, 'Ready.', 'hash', NULL);
                INSERT INTO prs VALUES ('repo', 7, 'abc');
                """;
            command.ExecuteNonQuery();
        }

        using var store = StateStore.Open(DbPath);

        Assert.Equal(StateStore.CurrentSchemaVersion, store.SchemaVersion);
        var landed = store.LandedPosting("repo", 7, "abc")!;
        Assert.Null(landed.MergeBase);
        Assert.Empty(landed.Reports);
        Assert.Null(Assert.Single(store.Bodies(landed.Id)).ReplyTo);
        Assert.Equal(new LastReview("abc", null), store.LastReviewed("repo", 7));
        Assert.Empty(store.Threads("repo", 7));
    }

    [Fact]
    public void Resolved_and_reported_marks_are_kept_once()
    {
        using var store = StateStore.Open(DbPath);
        store.AddThread("repo", 7, new ThreadRow("1001", "abc", "should", "a.py", 3, "new", "x = 1", "Should: fix.", null, null, null));
        store.AddThread("repo", 7, new ThreadRow("1001", "def", "minor", "b.py", 9, "new", null, "ignored", null, null, null));

        store.MarkResolved("repo", 7, "1001", new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
        store.MarkResolved("repo", 7, "1001", new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        store.MarkReported("repo", 7, ["1001"], new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));

        var thread = Assert.Single(store.Threads("repo", 7));
        Assert.Equal(("should", "2026-10-15T08:00:00.000Z", "2026-10-15T09:00:00.000Z"), (thread.Severity, thread.ResolvedByRtfd, thread.HandResolveReported));
    }

    [Fact]
    public void Pause_stop_and_resume_are_kept_per_repo()
    {
        using var store = StateStore.Open(DbPath);
        var at = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

        store.Pause("repo", at);
        store.Pause("repo", at.AddHours(1));
        store.Stop("repo", "the agent isn't logged in", at.AddHours(2));

        Assert.Equal(new RepoFlags(at, at.AddHours(2), "the agent isn't logged in"), store.Flags("repo"));
        Assert.Equal(RepoFlags.None, store.Flags("other"));
        Assert.Equal(new RepoFlags(at, at.AddHours(2), "the agent isn't logged in"), store.Resume("repo"));
        Assert.Equal(RepoFlags.None, store.Flags("repo"));
    }

    [Fact]
    public void An_account_pause_lasts_until_its_time_and_a_later_limit_replaces_it()
    {
        using var store = StateStore.Open(DbPath);
        var at = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

        store.PauseAccount("claude-personal", at.AddHours(2), "resets 10am");
        store.PauseAccount("claude-personal", at.AddHours(1), "resets 9am");

        Assert.Equal(new AccountPause("claude-personal", at.AddHours(1), "resets 9am"), store.AccountPaused("claude-personal", at));
        Assert.Null(store.AccountPaused("claude-personal", at.AddHours(1)));
        Assert.Null(store.AccountPaused("copilot-work", at));
    }
}
