using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Rtfd.State;

public sealed record AttemptState(int Used, string? Blocked);

/// <summary>One body of a posting: the summary (ordinal 0), an inline comment, or a reply on one of rtfd's threads.</summary>
/// <param name="Severity">For inline comments, the finding's severity: the thread keeps it.</param>
public sealed record PostedBody(int Ordinal, string? Path, int? Line, string? Side, string? Severity, string Body, string BodyHash, string? CommentId)
{
    /// <summary>An inline comment's anchor, which a follow-up passes back to the agent.</summary>
    public string? Anchor { get; init; }

    /// <summary>A reply: the comment that starts the thread it goes on.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>A reply: <c>reply</c>, or <c>resolve</c> when the thread is resolved after it.</summary>
    public string? Action { get; init; }

    public bool IsInline => Ordinal > 0 && ReplyTo is null;
    public bool IsReply => ReplyTo is not null;
}

/// <param name="MergeBase">The head's merge base, where the next follow-up's interdiff starts.</param>
/// <param name="Reports">The threads whose hand resolve this posting's summary reports.</param>
public sealed record PostingRow(
    long Id, long RunId, string Repo, int Pr, string Head, string? MergeBase, string Identity, string Event, DateTimeOffset StartedAt,
    string State, string? ReviewId, string? ReviewUrl, string? Message, IReadOnlyList<string> Reports);

/// <summary>The last review of a PR: its head, and the merge base the head had then.</summary>
public sealed record LastReview(string Head, string? MergeBase);

/// <summary>One of rtfd's threads (spec "State store"). Whether it is resolved now is the provider's to say.</summary>
/// <param name="CommentId">The inline comment that starts it.</param>
/// <param name="Head">The head it was posted on.</param>
/// <param name="PostingId">The posting it came from; null when rebuilt from the provider.</param>
/// <param name="ResolvedByRtfd">When rtfd resolved it, or null.</param>
/// <param name="HandResolveReported">When a summary that reported its hand resolve landed, or null.</param>
public sealed record ThreadRow(
    string CommentId, string Head, string Severity, string Path, int? Line, string? Side, string? Anchor, string Body,
    long? PostingId, string? ResolvedByRtfd, string? HandResolveReported);

/// <summary>One cleared entry, for what <c>rtfd retry</c> prints.</summary>
public sealed record ClearedAttempts(string Head, int Used, string? Blocked);

/// <summary>A repo's flags: <c>rtfd pause</c>, and a stop after a fatal outcome. <c>rtfd resume</c> clears both.</summary>
public sealed record RepoFlags(DateTimeOffset? PausedAt, DateTimeOffset? StoppedAt, string? StoppedReason)
{
    public static readonly RepoFlags None = new(null, null, null);
}

/// <summary>A usage limit on an agent account: every repo whose agent uses it waits until <paramref name="Until"/>.</summary>
public sealed record AccountPause(string Account, DateTimeOffset Until, string? Message);

/// <summary>The latest review of a PR that landed, for <c>status</c>.</summary>
public sealed record LandedReview(int Pr, string Head, string Event, string? Url, DateTimeOffset At);

/// <summary>A head's attempts and block, for <c>status</c>.</summary>
public sealed record HeadAttempts(int Pr, string Head, int Used, string? Blocked);

/// <summary>A PR's latest run, for <c>status</c>.</summary>
public sealed record LastRun(int Pr, string Head, DateTimeOffset StartedAt, string? Outcome, string? Message);

/// <summary>
/// <c>state.db</c> in the data directory (plan Phase 3): SQLite in WAL mode, hand-written SQL,
/// as in rtfc. A transaction is what makes the write-ahead record honest: the posting and the
/// hash of every body it will send are recorded together or not at all.
/// </summary>
public sealed class StateStore : IDisposable
{
    public const int CurrentSchemaVersion = 3;

    private static readonly Lazy<string> Schema = new(() =>
    {
        using var stream = typeof(StateStore).Assembly.GetManifestResourceStream("Rtfd.State.schema.sql")
            ?? throw new InvalidOperationException("the schema is missing from the binary");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    private StateStore(SqliteConnection connection) => _connection = connection;

    public static StateStore Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();

        var store = new StateStore(connection);
        try
        {
            store.Execute("PRAGMA journal_mode = WAL;");
            store.Execute("PRAGMA busy_timeout = 5000;");
            store.Execute("PRAGMA foreign_keys = ON;");
            store.Execute(Schema.Value);
            store.Migrate();
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public int SchemaVersion => int.Parse(Scalar<string>("SELECT value FROM meta WHERE key = 'schema_version'")!, CultureInfo.InvariantCulture);

    /// <summary>
    /// Brings an older file up to <see cref="CurrentSchemaVersion"/>. schema.sql runs first and
    /// creates the current shape for a new file and every table that is new, so a step only
    /// changes what was already there.
    /// </summary>
    private void Migrate()
    {
        var version = SchemaVersion;
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"state.db is schema version {version}, newer than this rtfd knows ({CurrentSchemaVersion}). Update rtfd.");
        }

        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            if (version < 2)
            {
                // v2 (Phase 4, follow-ups): replies in the write-ahead record, the merge base for the
                // interdiff, and hand-resolve reports. The threads table came with schema.sql.
                AddColumn("prs", "last_merge_base", "TEXT");
                AddColumn("postings", "merge_base", "TEXT");
                AddColumn("postings", "reports", "TEXT");
                AddColumn("posted_bodies", "anchor", "TEXT");
                AddColumn("posted_bodies", "reply_to", "TEXT");
                AddColumn("posted_bodies", "action", "TEXT");
                Execute("UPDATE meta SET value = '2' WHERE key = 'schema_version'");
            }

            if (version < 3)
            {
                // v3 (Phase 5, serve): repo pauses and stops, account pauses. Both tables are new,
                // so schema.sql made them.
                Execute("UPDATE meta SET value = '3' WHERE key = 'schema_version'");
            }

            transaction.Commit();
        }
    }

    private void AddColumn(string table, string column, string type)
    {
        if (Scalar<long>($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column", ("$column", column)) == 0)
        {
            Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
        }
    }

    // ---- runs ----

    public long StartRun(string repo, int pr, string head, string? promptHash, DateTimeOffset now) => Insert(
        "INSERT INTO runs (repo, pr, head, started_at, prompt_hash) VALUES ($repo, $pr, $head, $at, $prompt)",
        ("$repo", repo), ("$pr", pr), ("$head", head), ("$at", Time(now)), ("$prompt", promptHash));

    public void FinishRun(long id, string outcome, string? message, DateTimeOffset now) => Execute(
        "UPDATE runs SET finished_at = $at, outcome = $outcome, message = $message WHERE id = $id",
        ("$id", id), ("$at", Time(now)), ("$outcome", outcome), ("$message", message));

    /// <summary>Runs a killed process never finished, marked abandoned; call it holding the repo's lock. Returns how many.</summary>
    public int AbandonUnfinishedRuns(string repo, DateTimeOffset now) => Execute(
        "UPDATE runs SET finished_at = $at, outcome = 'abandoned', message = 'rtfd stopped before the run finished' WHERE repo = $repo AND finished_at IS NULL",
        ("$repo", repo), ("$at", Time(now)));

    // ---- attempts ----

    public AttemptState Attempts(string repo, int pr, string head) => QuerySingle(
        "SELECT used, blocked FROM attempts WHERE repo = $repo AND pr = $pr AND head = $head",
        r => new AttemptState((int)r.GetInt64(0), StringOrNull(r, 1)),
        ("$repo", repo), ("$pr", pr), ("$head", head)) ?? new AttemptState(0, null);

    public void CountAttempt(string repo, int pr, string head) => Execute(
        """
        INSERT INTO attempts (repo, pr, head, used) VALUES ($repo, $pr, $head, 1)
        ON CONFLICT (repo, pr, head) DO UPDATE SET used = used + 1
        """,
        ("$repo", repo), ("$pr", pr), ("$head", head));

    /// <summary>A secret from outside the diff stops the PR until someone has looked (spec "Validator").</summary>
    public void Block(string repo, int pr, string head, string reason) => Execute(
        """
        INSERT INTO attempts (repo, pr, head, blocked) VALUES ($repo, $pr, $head, $reason)
        ON CONFLICT (repo, pr, head) DO UPDATE SET blocked = excluded.blocked
        """,
        ("$repo", repo), ("$pr", pr), ("$head", head), ("$reason", reason));

    /// <summary><c>rtfd retry</c>: gives every head of the PR its attempts back and lifts any secret block.</summary>
    public IReadOnlyList<ClearedAttempts> Retry(string repo, int pr)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var cleared = Query(
                "SELECT head, used, blocked FROM attempts WHERE repo = $repo AND pr = $pr AND (used > 0 OR blocked IS NOT NULL) ORDER BY head",
                r => new ClearedAttempts(r.GetString(0), (int)r.GetInt64(1), StringOrNull(r, 2)),
                ("$repo", repo), ("$pr", pr));
            Execute("DELETE FROM attempts WHERE repo = $repo AND pr = $pr", ("$repo", repo), ("$pr", pr));
            transaction.Commit();
            return cleared;
        }
    }

    // ---- postings ----

    /// <summary>The write-ahead record: the posting and every body, in one transaction, before the post.</summary>
    /// <param name="reports">The threads whose hand resolve the summary reports, marked reported once it lands.</param>
    public long BeginPosting(
        long runId, string repo, int pr, string head, string? mergeBase, string identity, string verdictEvent, DateTimeOffset now,
        IReadOnlyList<PostedBody> bodies, IReadOnlyList<string>? reports = null)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var id = Insert(
                """
                INSERT INTO postings (run_id, repo, pr, head, merge_base, identity, event, started_at, state, reports)
                VALUES ($run, $repo, $pr, $head, $base, $identity, $event, $at, 'pending', $reports)
                """,
                ("$run", runId), ("$repo", repo), ("$pr", pr), ("$head", head), ("$base", mergeBase), ("$identity", identity),
                ("$event", verdictEvent), ("$at", Time(now)), ("$reports", reports is { Count: > 0 } ? string.Join(' ', reports) : null));
            foreach (var body in bodies)
            {
                Execute(
                    """
                    INSERT INTO posted_bodies (posting_id, ordinal, path, line, side, severity, body, body_hash, anchor, reply_to, action)
                    VALUES ($posting, $ordinal, $path, $line, $side, $severity, $body, $hash, $anchor, $reply, $action)
                    """,
                    ("$posting", id), ("$ordinal", body.Ordinal), ("$path", body.Path), ("$line", body.Line), ("$side", body.Side),
                    ("$severity", body.Severity), ("$body", body.Body), ("$hash", body.BodyHash), ("$anchor", body.Anchor),
                    ("$reply", body.ReplyTo), ("$action", body.Action));
            }

            transaction.Commit();
            return id;
        }
    }

    public void RecordReview(long postingId, string reviewId, string? reviewUrl) => Execute(
        "UPDATE postings SET review_id = $review, review_url = $url, state = 'posted' WHERE id = $id",
        ("$id", postingId), ("$review", reviewId), ("$url", reviewUrl));

    public void RecordComment(long postingId, int ordinal, string commentId) => Execute(
        "UPDATE posted_bodies SET comment_id = $comment WHERE posting_id = $posting AND ordinal = $ordinal",
        ("$posting", postingId), ("$ordinal", ordinal), ("$comment", commentId));

    /// <summary>Ends a posting: verified, unverified, lost (never landed) or rejected (the provider refused it).</summary>
    public void FinishPosting(long postingId, string state, string? message, DateTimeOffset now) => Execute(
        "UPDATE postings SET state = $state, message = $message, completed_at = $at WHERE id = $id",
        ("$id", postingId), ("$state", state), ("$message", message), ("$at", Time(now)));

    /// <summary>Postings a crash or a failed request left open: pending (no id yet) or posted (not yet verified).</summary>
    public IReadOnlyList<PostingRow> OpenPostings(string repo, int pr) => Query(
        $"SELECT {PostingColumns} FROM postings WHERE repo = $repo AND pr = $pr AND state IN ('pending', 'posted') ORDER BY id",
        ReadPosting, ("$repo", repo), ("$pr", pr));

    /// <summary>A review that landed for this head, if there is one: one review per head.</summary>
    public PostingRow? LandedPosting(string repo, int pr, string head) => QuerySingle(
        $"SELECT {PostingColumns} FROM postings WHERE repo = $repo AND pr = $pr AND head = $head AND state IN ('verified', 'unverified') ORDER BY id DESC LIMIT 1",
        ReadPosting, ("$repo", repo), ("$pr", pr), ("$head", head));

    public IReadOnlyList<PostedBody> Bodies(long postingId) => Query(
        "SELECT ordinal, path, line, side, severity, body, body_hash, comment_id, anchor, reply_to, action FROM posted_bodies WHERE posting_id = $posting ORDER BY ordinal",
        r => new PostedBody((int)r.GetInt64(0), StringOrNull(r, 1), r.IsDBNull(2) ? null : (int)r.GetInt64(2), StringOrNull(r, 3), StringOrNull(r, 4),
            r.GetString(5), r.GetString(6), StringOrNull(r, 7))
        {
            Anchor = StringOrNull(r, 8),
            ReplyTo = StringOrNull(r, 9),
            Action = StringOrNull(r, 10),
        },
        ("$posting", postingId));

    // ---- prs ----

    /// <summary>Whether the store knows this PR at all. One it doesn't is rebuilt from the provider first.</summary>
    public bool HasRecord(string repo, int pr) => Scalar<long>(
        "SELECT EXISTS (SELECT 1 FROM prs WHERE repo = $repo AND pr = $pr) OR EXISTS (SELECT 1 FROM postings WHERE repo = $repo AND pr = $pr)",
        ("$repo", repo), ("$pr", pr)) == 1;

    public void SetLastReviewed(string repo, int pr, string head, string? mergeBase) => Execute(
        """
        INSERT INTO prs (repo, pr, last_reviewed_head, last_merge_base) VALUES ($repo, $pr, $head, $base)
        ON CONFLICT (repo, pr) DO UPDATE SET last_reviewed_head = excluded.last_reviewed_head, last_merge_base = excluded.last_merge_base
        """,
        ("$repo", repo), ("$pr", pr), ("$head", head), ("$base", mergeBase));

    public LastReview? LastReviewed(string repo, int pr) => QuerySingle(
        "SELECT last_reviewed_head, last_merge_base FROM prs WHERE repo = $repo AND pr = $pr AND last_reviewed_head IS NOT NULL",
        r => new LastReview(r.GetString(0), StringOrNull(r, 1)),
        ("$repo", repo), ("$pr", pr));

    // ---- threads ----

    /// <summary>Records one of rtfd's threads; one already known is left as it is.</summary>
    public void AddThread(string repo, int pr, ThreadRow thread) => Execute(
        """
        INSERT OR IGNORE INTO threads (repo, pr, comment_id, head, severity, path, line, side, anchor, body, posting_id, resolved_by_rtfd, hand_resolve_reported)
        VALUES ($repo, $pr, $comment, $head, $severity, $path, $line, $side, $anchor, $body, $posting, $resolved, $reported)
        """,
        ("$repo", repo), ("$pr", pr), ("$comment", thread.CommentId), ("$head", thread.Head), ("$severity", thread.Severity), ("$path", thread.Path),
        ("$line", thread.Line), ("$side", thread.Side), ("$anchor", thread.Anchor), ("$body", thread.Body), ("$posting", thread.PostingId),
        ("$resolved", thread.ResolvedByRtfd), ("$reported", thread.HandResolveReported));

    public IReadOnlyList<ThreadRow> Threads(string repo, int pr) => Query(
        """
        SELECT comment_id, head, severity, path, line, side, anchor, body, posting_id, resolved_by_rtfd, hand_resolve_reported
        FROM threads WHERE repo = $repo AND pr = $pr ORDER BY rowid
        """,
        r => new ThreadRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : (int)r.GetInt64(4), StringOrNull(r, 5),
            StringOrNull(r, 6), r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8), StringOrNull(r, 9), StringOrNull(r, 10)),
        ("$repo", repo), ("$pr", pr));

    /// <summary>rtfd resolved the thread, or meant to and found it resolved: it is never a hand resolve.</summary>
    public void MarkResolved(string repo, int pr, string commentId, DateTimeOffset now) => Execute(
        "UPDATE threads SET resolved_by_rtfd = COALESCE(resolved_by_rtfd, $at) WHERE repo = $repo AND pr = $pr AND comment_id = $comment",
        ("$repo", repo), ("$pr", pr), ("$comment", commentId), ("$at", Time(now)));

    /// <summary>A summary that reports these threads' hand resolves landed, so it isn't said again (spec decision 37).</summary>
    public void MarkReported(string repo, int pr, IEnumerable<string> commentIds, DateTimeOffset now)
    {
        foreach (var id in commentIds)
        {
            Execute(
                "UPDATE threads SET hand_resolve_reported = COALESCE(hand_resolve_reported, $at) WHERE repo = $repo AND pr = $pr AND comment_id = $comment",
                ("$repo", repo), ("$pr", pr), ("$comment", id), ("$at", Time(now)));
        }
    }

    /// <summary>What a rebuild from the provider found, in one transaction. The PR is known afterwards, even when nothing was found.</summary>
    public void Rebuilt(string repo, int pr, LastReview? last, IReadOnlyList<ThreadRow> threads)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            Execute("INSERT OR IGNORE INTO prs (repo, pr) VALUES ($repo, $pr)", ("$repo", repo), ("$pr", pr));
            if (last is not null)
            {
                SetLastReviewed(repo, pr, last.Head, last.MergeBase);
            }

            foreach (var thread in threads)
            {
                AddThread(repo, pr, thread);
            }

            transaction.Commit();
        }
    }

    // ---- repos and accounts (Phase 5) ----

    public RepoFlags Flags(string repo) => QuerySingle(
        "SELECT paused_at, stopped_at, stopped_reason FROM repos WHERE repo = $repo",
        r => new RepoFlags(TimeOrNull(r, 0), TimeOrNull(r, 1), StringOrNull(r, 2)),
        ("$repo", repo)) ?? RepoFlags.None;

    /// <summary><c>rtfd pause</c>. A repo already paused keeps the time it was paused.</summary>
    public void Pause(string repo, DateTimeOffset now) => Execute(
        """
        INSERT INTO repos (repo, paused_at) VALUES ($repo, $at)
        ON CONFLICT (repo) DO UPDATE SET paused_at = COALESCE(paused_at, excluded.paused_at)
        """,
        ("$repo", repo), ("$at", Time(now)));

    /// <summary>A fatal outcome stops the repo until <c>rtfd resume</c> (spec "Agent adapter").</summary>
    public void Stop(string repo, string reason, DateTimeOffset now) => Execute(
        """
        INSERT INTO repos (repo, stopped_at, stopped_reason) VALUES ($repo, $at, $reason)
        ON CONFLICT (repo) DO UPDATE SET stopped_at = excluded.stopped_at, stopped_reason = excluded.stopped_reason
        """,
        ("$repo", repo), ("$at", Time(now)), ("$reason", reason));

    /// <summary><c>rtfd resume</c>: lifts a pause and a stop, and returns what was there.</summary>
    public RepoFlags Resume(string repo)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var flags = Flags(repo);
            Execute("UPDATE repos SET paused_at = NULL, stopped_at = NULL, stopped_reason = NULL WHERE repo = $repo", ("$repo", repo));
            transaction.Commit();
            return flags;
        }
    }

    /// <summary>
    /// A usage limit pauses the account until <paramref name="until"/>. A later limit replaces an
    /// earlier one: the newest message knows the reset time best.
    /// </summary>
    public void PauseAccount(string account, DateTimeOffset until, string? message) => Execute(
        """
        INSERT INTO accounts (account, paused_until, message) VALUES ($account, $until, $message)
        ON CONFLICT (account) DO UPDATE SET paused_until = excluded.paused_until, message = excluded.message
        """,
        ("$account", account), ("$until", Time(until)), ("$message", message));

    /// <summary>The account's pause, while it lasts.</summary>
    public AccountPause? AccountPaused(string account, DateTimeOffset now) =>
        AccountPauses().FirstOrDefault(p => p.Account == account && p.Until > now);

    /// <summary>Every account's last pause, over or not.</summary>
    public IReadOnlyList<AccountPause> AccountPauses() => Query(
        "SELECT account, paused_until, message FROM accounts WHERE paused_until IS NOT NULL ORDER BY account",
        r => new AccountPause(r.GetString(0), ParseTime(r.GetString(1)), StringOrNull(r, 2)));

    /// <summary>Each PR's latest review that landed, newest PR first.</summary>
    public IReadOnlyList<LandedReview> LandedReviews(string repo) => Query(
        """
        SELECT p.pr, p.head, p.event, p.review_url, COALESCE(p.completed_at, p.started_at) FROM postings p
        WHERE p.repo = $repo AND p.state IN ('verified', 'unverified')
          AND p.id = (SELECT MAX(id) FROM postings q WHERE q.repo = p.repo AND q.pr = p.pr AND q.state IN ('verified', 'unverified'))
        ORDER BY p.pr DESC
        """,
        r => new LandedReview((int)r.GetInt64(0), r.GetString(1), r.GetString(2), StringOrNull(r, 3), ParseTime(r.GetString(4))),
        ("$repo", repo));

    /// <summary>Every head with attempts used or a block.</summary>
    public IReadOnlyList<HeadAttempts> AllAttempts(string repo) => Query(
        "SELECT pr, head, used, blocked FROM attempts WHERE repo = $repo AND (used > 0 OR blocked IS NOT NULL) ORDER BY pr DESC, head",
        r => new HeadAttempts((int)r.GetInt64(0), r.GetString(1), (int)r.GetInt64(2), StringOrNull(r, 3)),
        ("$repo", repo));

    /// <summary>Each PR's latest run.</summary>
    public IReadOnlyList<LastRun> LastRuns(string repo) => Query(
        """
        SELECT r.pr, r.head, r.started_at, r.outcome, r.message FROM runs r
        WHERE r.repo = $repo AND r.id = (SELECT MAX(id) FROM runs s WHERE s.repo = r.repo AND s.pr = r.pr)
        ORDER BY r.pr DESC
        """,
        r => new LastRun((int)r.GetInt64(0), r.GetString(1), ParseTime(r.GetString(2)), StringOrNull(r, 3), StringOrNull(r, 4)),
        ("$repo", repo));

    // ---- plumbing ----

    private const string PostingColumns = "id, run_id, repo, pr, head, merge_base, identity, event, started_at, state, review_id, review_url, message, reports";

    private static PostingRow ReadPosting(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), (int)r.GetInt64(3), r.GetString(4), StringOrNull(r, 5), r.GetString(6), r.GetString(7),
        DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), r.GetString(9),
        StringOrNull(r, 10), StringOrNull(r, 11), StringOrNull(r, 12), StringOrNull(r, 13)?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []);

    public static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? TimeOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseTime(r.GetString(i));

    internal int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var command = Command(sql, parameters);
            return command.ExecuteNonQuery();
        }
    }

    private long Insert(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var command = Command(sql + "; SELECT last_insert_rowid();", parameters);
            return (long)command.ExecuteScalar()!;
        }
    }

    private T? Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var command = Command(sql, parameters);
            var value = command.ExecuteScalar();
            return value is null or DBNull ? default : (T)value;
        }
    }

    private T? QuerySingle<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
        where T : class
    {
        lock (_lock)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            return reader.Read() ? read(reader) : null;
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read())
            {
                rows.Add(read(reader));
            }

            return rows;
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string? StringOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public void Dispose() => _connection.Dispose();
}
