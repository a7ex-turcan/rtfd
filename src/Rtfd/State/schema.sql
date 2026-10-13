-- rtfd's state store (spec "State store"). Every statement is idempotent so the file can be
-- applied on every open; changes to existing files are versioned steps in StateStore.Migrate.
-- Timestamps are ISO 8601 UTC.

CREATE TABLE IF NOT EXISTS meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '1');

-- Per PR: what was last reviewed, for follow-ups (Phase 4).
CREATE TABLE IF NOT EXISTS prs (
  repo               TEXT NOT NULL,
  pr                 INTEGER NOT NULL,
  last_reviewed_head TEXT,
  PRIMARY KEY (repo, pr)
);

-- One review run of one head: the agent attempts, and the prompt that drove them.
CREATE TABLE IF NOT EXISTS runs (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  repo        TEXT NOT NULL,
  pr          INTEGER NOT NULL,
  head        TEXT NOT NULL,
  started_at  TEXT NOT NULL,
  finished_at TEXT,
  outcome     TEXT,                    -- ok | invalid | limit | transient | fatal
  message     TEXT,
  prompt_hash TEXT
);

CREATE INDEX IF NOT EXISTS runs_by_head ON runs (repo, pr, head);

-- Attempts per commit (spec "Policy"), and a secret block that waits for `rtfd retry`.
CREATE TABLE IF NOT EXISTS attempts (
  repo    TEXT NOT NULL,
  pr      INTEGER NOT NULL,
  head    TEXT NOT NULL,
  used    INTEGER NOT NULL DEFAULT 0,
  blocked TEXT,
  PRIMARY KEY (repo, pr, head)
);

-- The write-ahead record (spec "Posting and idempotency"): written in one transaction before the
-- post, completed after it. A posting still 'pending' at the next run means a crash or a failed
-- request between the two, and is matched against the provider by body hash.
CREATE TABLE IF NOT EXISTS postings (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  run_id       INTEGER NOT NULL REFERENCES runs (id),
  repo         TEXT NOT NULL,
  pr           INTEGER NOT NULL,
  head         TEXT NOT NULL,
  identity     TEXT NOT NULL,          -- the login the token posts as
  event        TEXT NOT NULL,          -- COMMENT | APPROVE | REQUEST_CHANGES
  started_at   TEXT NOT NULL,
  state        TEXT NOT NULL,          -- pending | posted | verified | unverified | lost | rejected
  review_id    TEXT,
  review_url   TEXT,
  message      TEXT,
  completed_at TEXT
);

CREATE INDEX IF NOT EXISTS postings_by_pr ON postings (repo, pr, state);

-- Every body a posting sends, exactly as sent, with its hash. Kept in full so a posting that
-- never landed can be sent again without running the agent again.
CREATE TABLE IF NOT EXISTS posted_bodies (
  posting_id INTEGER NOT NULL REFERENCES postings (id),
  ordinal    INTEGER NOT NULL,         -- 0 is the review's summary; inline comments follow
  path       TEXT,
  line       INTEGER,
  side       TEXT,                     -- new | old
  severity   TEXT,                     -- should | could | minor, for rtfd's threads (Phase 4)
  body       TEXT NOT NULL,
  body_hash  TEXT NOT NULL,
  comment_id TEXT,                     -- the provider's id, once known
  PRIMARY KEY (posting_id, ordinal)
);
