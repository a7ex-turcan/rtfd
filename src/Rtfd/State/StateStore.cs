using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Rtfd.State;

public sealed record AttemptState(int Used, string? Blocked);

/// <param name="Severity">For inline comments, the finding's severity, which Phase 4 needs for rtfd's threads.</param>
public sealed record PostedBody(int Ordinal, string? Path, int? Line, string? Side, string? Severity, string Body, string BodyHash, string? CommentId);

public sealed record PostingRow(
    long Id, long RunId, string Repo, int Pr, string Head, string Identity, string Event, DateTimeOffset StartedAt,
    string State, string? ReviewId, string? ReviewUrl, string? Message);

/// <summary>One cleared entry, for what <c>rtfd retry</c> prints.</summary>
public sealed record ClearedAttempts(string Head, int Used, string? Blocked);

/// <summary>
/// <c>state.db</c> in the data directory (plan Phase 3): SQLite in WAL mode, hand-written SQL,
/// as in rtfc. A transaction is what makes the write-ahead record honest: the posting and the
/// hash of every body it will send are recorded together or not at all.
/// </summary>
public sealed class StateStore : IDisposable
{
    public const int CurrentSchemaVersion = 1;

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

    /// <summary>Brings an older file up to <see cref="CurrentSchemaVersion"/>; schema.sql creates the current shape for new ones.</summary>
    private void Migrate()
    {
        if (SchemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"state.db is schema version {SchemaVersion}, newer than this rtfd knows ({CurrentSchemaVersion}). Update rtfd.");
        }
    }

    // ---- runs ----

    public long StartRun(string repo, int pr, string head, string? promptHash, DateTimeOffset now) => Insert(
        "INSERT INTO runs (repo, pr, head, started_at, prompt_hash) VALUES ($repo, $pr, $head, $at, $prompt)",
        ("$repo", repo), ("$pr", pr), ("$head", head), ("$at", Time(now)), ("$prompt", promptHash));

    public void FinishRun(long id, string outcome, string? message, DateTimeOffset now) => Execute(
        "UPDATE runs SET finished_at = $at, outcome = $outcome, message = $message WHERE id = $id",
        ("$id", id), ("$at", Time(now)), ("$outcome", outcome), ("$message", message));

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
    public long BeginPosting(long runId, string repo, int pr, string head, string identity, string verdictEvent, DateTimeOffset now, IReadOnlyList<PostedBody> bodies)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var id = Insert(
                """
                INSERT INTO postings (run_id, repo, pr, head, identity, event, started_at, state)
                VALUES ($run, $repo, $pr, $head, $identity, $event, $at, 'pending')
                """,
                ("$run", runId), ("$repo", repo), ("$pr", pr), ("$head", head), ("$identity", identity), ("$event", verdictEvent), ("$at", Time(now)));
            foreach (var body in bodies)
            {
                Execute(
                    """
                    INSERT INTO posted_bodies (posting_id, ordinal, path, line, side, severity, body, body_hash)
                    VALUES ($posting, $ordinal, $path, $line, $side, $severity, $body, $hash)
                    """,
                    ("$posting", id), ("$ordinal", body.Ordinal), ("$path", body.Path), ("$line", body.Line), ("$side", body.Side),
                    ("$severity", body.Severity), ("$body", body.Body), ("$hash", body.BodyHash));
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
        "SELECT ordinal, path, line, side, severity, body, body_hash, comment_id FROM posted_bodies WHERE posting_id = $posting ORDER BY ordinal",
        r => new PostedBody((int)r.GetInt64(0), StringOrNull(r, 1), r.IsDBNull(2) ? null : (int)r.GetInt64(2), StringOrNull(r, 3), StringOrNull(r, 4),
            r.GetString(5), r.GetString(6), StringOrNull(r, 7)),
        ("$posting", postingId));

    // ---- prs ----

    public void SetLastReviewedHead(string repo, int pr, string head) => Execute(
        """
        INSERT INTO prs (repo, pr, last_reviewed_head) VALUES ($repo, $pr, $head)
        ON CONFLICT (repo, pr) DO UPDATE SET last_reviewed_head = excluded.last_reviewed_head
        """,
        ("$repo", repo), ("$pr", pr), ("$head", head));

    public string? LastReviewedHead(string repo, int pr) =>
        Scalar<string>("SELECT last_reviewed_head FROM prs WHERE repo = $repo AND pr = $pr", ("$repo", repo), ("$pr", pr));

    // ---- plumbing ----

    private const string PostingColumns = "id, run_id, repo, pr, head, identity, event, started_at, state, review_id, review_url, message";

    private static PostingRow ReadPosting(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), (int)r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetString(6),
        DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), r.GetString(8),
        StringOrNull(r, 9), StringOrNull(r, 10), StringOrNull(r, 11));

    public static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

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
