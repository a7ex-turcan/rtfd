-- rtfd's state store (spec "State store"). Every statement is idempotent so the file can be
-- applied on every open, and it holds the current shape; StateStore.Migrate brings an older file
-- up to it, one versioned step at a time. Timestamps are ISO 8601 UTC.

CREATE TABLE IF NOT EXISTS meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '3');

-- Per PR: the head the last review was of, and its merge base then, for the interdiff (Phase 4).
CREATE TABLE IF NOT EXISTS prs (
  repo               TEXT NOT NULL,
  pr                 INTEGER NOT NULL,
  last_reviewed_head TEXT,
  last_merge_base    TEXT,
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
  outcome     TEXT,                    -- ok | invalid | limit | transient | fatal | unchanged
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
-- request between the two, and is matched against the provider by body hash. 'posted' means the
-- review landed and its replies and resolves may not all have.
CREATE TABLE IF NOT EXISTS postings (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  run_id       INTEGER NOT NULL REFERENCES runs (id),
  repo         TEXT NOT NULL,
  pr           INTEGER NOT NULL,
  head         TEXT NOT NULL,
  merge_base   TEXT,                   -- the head's merge base, kept for the next interdiff
  identity     TEXT NOT NULL,          -- the login the token posts as
  event        TEXT NOT NULL,          -- COMMENT | APPROVE | REQUEST_CHANGES
  started_at   TEXT NOT NULL,
  state        TEXT NOT NULL,          -- pending | posted | verified | unverified | lost | rejected
  review_id    TEXT,
  review_url   TEXT,
  message      TEXT,
  completed_at TEXT,
  reports      TEXT                    -- the threads whose hand resolve the summary reports, space-separated
);

CREATE INDEX IF NOT EXISTS postings_by_pr ON postings (repo, pr, state);

-- Every body a posting sends, exactly as sent, with its hash. Kept in full so a posting that
-- never landed can be sent again without running the agent again.
CREATE TABLE IF NOT EXISTS posted_bodies (
  posting_id INTEGER NOT NULL REFERENCES postings (id),
  ordinal    INTEGER NOT NULL,         -- 0 is the review's summary; inline comments, then replies follow
  path       TEXT,
  line       INTEGER,
  side       TEXT,                     -- new | old
  severity   TEXT,                     -- should | could | minor, for rtfd's threads
  body       TEXT NOT NULL,
  body_hash  TEXT NOT NULL,
  comment_id TEXT,                     -- the provider's id, once known
  anchor     TEXT,                     -- an inline comment's anchor, for the follow-up's inputs
  reply_to   TEXT,                     -- a reply: the comment that starts the thread
  action     TEXT,                     -- a reply: reply | resolve
  PRIMARY KEY (posting_id, ordinal)
);

-- rtfd's own threads (spec "Posting and idempotency"): every inline comment of a review that
-- landed, or found again from the review markers when the store was lost. Whether a thread is
-- resolved is always read from the provider; this says whether rtfd resolved it, and whether a
-- summary has reported that someone resolved it by hand.
CREATE TABLE IF NOT EXISTS threads (
  repo                  TEXT NOT NULL,
  pr                    INTEGER NOT NULL,
  comment_id            TEXT NOT NULL,  -- the inline comment that starts the thread
  head                  TEXT NOT NULL,  -- the head it was posted on
  severity              TEXT NOT NULL,  -- should | could | minor
  path                  TEXT NOT NULL,
  line                  INTEGER,
  side                  TEXT,
  anchor                TEXT,
  body                  TEXT NOT NULL,
  posting_id            INTEGER REFERENCES postings (id),  -- null when rebuilt from the provider
  resolved_by_rtfd      TEXT,           -- when rtfd resolved it
  hand_resolve_reported TEXT,           -- when a summary saying it was closed by hand landed
  PRIMARY KEY (repo, pr, comment_id)
);

-- Per repo (Phase 5): `rtfd pause`, and a stop after a fatal outcome that `rtfd resume` lifts.
-- serve reads both on every tick.
CREATE TABLE IF NOT EXISTS repos (
  repo           TEXT PRIMARY KEY,
  paused_at      TEXT,                  -- `rtfd pause`
  stopped_at     TEXT,                  -- a fatal outcome, such as the agent not being logged in
  stopped_reason TEXT
);

-- Per agent account (spec "State store"): a usage limit pauses every repo whose agent uses it.
CREATE TABLE IF NOT EXISTS accounts (
  account      TEXT PRIMARY KEY,
  paused_until TEXT,
  message      TEXT                     -- the limit message, for the log and `rtfd status`
);
