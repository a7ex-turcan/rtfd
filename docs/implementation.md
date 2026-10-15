# rtfd: implementation notes

Where the code goes further than [`spec.md`](spec.md) says, or settles something the spec left
open, and why. The spec is updated in the same change (its "Decisions taken" log, from 42 on);
this file keeps the reasoning that doesn't belong in the design. Newest first.

## Phase 5 (2026-10-15)

### One live run, shared by `rtfd run` and `serve`

`RunCommand`'s posting path moved into `Runs/LiveRun.cs`, so a review from serve and one by hand go
through the same recovery, gate, write-ahead record and finish. It also records a usage limit on
the agent's account, whoever hit it: a manual run that hits the limit pauses serve too. Posting never
takes serve's stop token. Once a review is decided it goes out, and a stop waits for it; a kill
mid-post is what the write-ahead record is for.

### What the watcher remembers, and what it asks the store

Whether a head is reviewed, blocked or out of attempts comes from the store on every tick, so a
manual `rtfd run`, `rtfd retry` or a rebuilt store shows at once. The watcher keeps only what
doesn't matter after a restart: the detector's view of each PR (when its head was first seen, CI),
backoffs, heads refused for good reasons (`RunReport.RefusedForHead`: no changes, an author or fork
rtfd skips, the gate's refusals), and which skips it has already logged. A refusal about the setup
(a missing prompt file) backs off instead, so fixing the file is enough.

### A head the store doesn't know goes through a run, not a lookup

On a fresh data directory every open PR looks unreviewed until `ReviewHistory` rebuilds it from the
markers, which happens inside the run, before the gate. So serve's first pass on such a PR fetches
and refuses ("already reviewed") without starting the agent, and the next tick reads it from the
store. Checking the markers in the watcher would have been a second rebuild path.

### `status` reads the lock, not a pid

A pid in `status.json` can be reused by another process; the lock can't be held by anyone but a
live serve. `status` takes the lock for an instant when nobody holds it, which a serve starting at
that very moment would read as another instance; the window is a few microseconds.

### The heartbeat has its own task

A tick runs a review, which can take half an hour. If the ticks wrote the heartbeat, a long review
would read as a hang. A separate task writes `status.json` every 30 seconds, and after every tick, so
`status` sees a review start straight away.

### Daily logs, kept 14 days, not configurable

A key is easy to add when someone wants another number; until then 14 days is a constant in
`ServeLog`. Lines are UTC like the store. The console gets the same lines, for a serve run by hand.

### The job object is joined by serve only

`ChildProcessJob` puts the whole process in a kill-on-close job, so it is called by `serve` and
nothing else: `rtfd run` is interactive (Ctrl+C reaches its children through the console), and the
test host must not join one. Verified live: serve killed with `Stop-Process -Force` 8 seconds into
a review, and `claude.exe` and its `rtfd git-tools` were gone three seconds later.

### Closing the console gives seconds

On Windows closing the console window (`SIGHUP` in .NET's terms) lets the handler run, but the
process is ended a few seconds later whatever it does. serve starts stopping like on Ctrl+C; a post
cut short is settled by the write-ahead record at the next start. Phase 6 runs serve with no console.

### CI was asked once

The first version asked for CI only while the detector didn't know it, so a head seen with CI
pending waited the whole `ci_max_wait`. `A_head_waits_for_CI_to_settle` caught it. CI is asked on
every tick until it settles, which costs nothing while GitHub answers 304.

## Phase 4 (2026-10-15)

### The interdiff is `git merge-tree`, not a scratch worktree (spec decision 54)

The plan applied the old PR diff in a scratch worktree. `git merge-tree --write-tree --merge-base`
does the same apply as a three-way merge, inside the bare mirror: nothing is checked out, so no
file from the PR is written anywhere, and a conflict is exit code 1. It is more forgiving than
`git apply` (context that moved still merges) and needs git 2.40; an older git fails the run with a
message saying so, rather than quietly reviewing the whole PR every time. A merge driver from a
PR's `.gitattributes` can't run: drivers need config, and the mirror's config is rtfd's.

### A rebuild reads the PR's comment list, and counts resolved threads as rtfd's

GitHub's list of a review's own comments has `line: null` for every comment; the PR's comment list
has the line, the review id and `in_reply_to_id`, so the rebuild uses that. A thread that was
already resolved can't be told apart: GitHub's `resolvedBy` names the owner either way. Counting it
as resolved by rtfd means a lost store never makes a summary claim that someone closed a thread
by hand when rtfd did; the cost is that a hand resolve not yet reported when the store was lost
goes unreported.

### Every reply gets an empty review

Found on the sandbox: `POST /pulls/{n}/comments/{id}/replies` creates a COMMENTED review with an
empty body for each reply. Crash recovery matches reviews by the summary's hash and the rebuild
looks for the marker, so neither mistakes one for rtfd's review. The fake provider does the same.

### A resolve is recorded before it is sent

If rtfd crashed after GitHub resolved a thread and before the store said so, the next run would see
a resolved thread rtfd hadn't resolved: a hand resolve, reported in a summary, which would be false.
Recorded first, the worst case is the opposite: a resolve that failed leaves the thread open on
GitHub, which decides, so it still counts; only a later hand resolve of that thread goes
unreported.

### The interdiff's size cap doesn't make the review partial

Excludes and the cap apply to the interdiff as the plan says, and files the cap leaves out are
named in the prompt. The review is partial only when the full diff is: the agent has the full diff
either way, and a revert can make an interdiff bigger than the PR.

### A dry run reads the store

So the follow-up a dry run prints is the one a live run would do, with the same threads and the
same interdiff. It records nothing: a PR the store doesn't know is rebuilt in memory, and a home
without a `state.db` still gets none.

### The marker carries the merge base

Without it, a rebuild would work the old merge base out from the old head and today's base branch,
which is right unless the PR was stacked on a branch that has since merged. Old markers without
`base=` still parse.

## Phase 3 (2026-10-13)

### Body hashes ignore line endings and trailing whitespace

`Poster.Hash` takes the SHA-256 of the body with CRLF turned into LF and the end trimmed. On the
sandbox GitHub gave bodies back unchanged, but a provider (or a proxy) that normalized either would
otherwise make crash recovery miss a review that landed, and post it twice. Nothing a reader sees
depends on those characters.

### Every agent attempt counts, a good one too

The spec counts invalid attempts and not usage limits; it says nothing of the rest. rtfd counts
every attempt but a limit: a timeout or an unexplained failure would otherwise retry forever, and a
good attempt ends in a review, after which the head is refused anyway.

### The marker carries the run, and the run is recorded first

`<!-- rtfd sha=<head> run=<id> -->` names the run row the store creates when the head passes the
gate, before the agent starts. The marker is part of the summary's hash, so a re-post from the
record is byte-identical to the first attempt and can't be mistaken for a different review.

## Phase 1 (2026-10-13)

### The diff is scanned for credentials too (spec decision 45)

The spec triggers the credential Should when a secret shows up in the *outgoing text*. A PR that
commits a token the agent doesn't mention would then get no Should at all, and the plan's hostile
PR exit criterion expects one. So `ReviewBuilder` also runs the scanner over every added line of
the diff (excluded and skipped files included) and puts rtfd's own Should on the line, with the
secret redacted. The outgoing-text check is unchanged: a match in the diff is redacted, a match
outside it blocks the post.

The cost is the false positives gitleaks has anyway, such as a test fixture with a key-shaped
string. That is a Should the author can resolve by hand, which beats a credential going unremarked.

Where the agent already put a Should on the credential's line, rtfd doesn't add its own: the
first real run on the sandbox's hostile PR said everything twice. The same goes for agent
configuration: rtfd's Should on an executable config file is left out when the agent raised a
Should on that file. The guarantee is one Should, and redaction applies to the agent's text too.

### The findings contract lives in the binary (spec decision 46)

The plan had the prompt file carry the schema and the input paths. Instead `PromptRenderer`
appends them to the repo's prompt file on every run: where the inputs are (absolute worktree path
included), the schema with an example, the anchor rules, and on a retry the validator's errors.
The prompt file keeps what the owner tunes: the job, the conventions, the tone. Editing it can't
break the contract, and the contract can't drift from the validator that checks it.

### rtfd writes the verdict line (spec decision 44)

On nutrx every review posts as a COMMENT (GitHub refuses self-approval), so the verdict only shows
in the text. The old watcher opened each review with "Not ready: 1 Should open." or "Ready to merge
from my side.", and if the agent wrote that line it could contradict rtfd's verdict. So rtfd writes
it from the verdict, and the prompt tells the agent its summary shouldn't say whether the PR is
ready.

### Findings parsed over the JSON node tree, not with a serializer

The plan says "source-generated JSON, strict". `FindingsDocument.Parse` walks `JsonNode` by hand
instead (AOT-safe without a generator) because the messages are the point: a retry only fixes what
the model is told, and "unknown field 'verdict': rtfd decides the verdict from your findings, so
leave it out" does better than System.Text.Json's "could not be mapped to any .NET member".

### A closed PR is reviewed against its recorded base

Tuning uses merged PRs (plan 1e). Once merged, a PR's head is in today's base branch, so the diff
against today's base is empty. For a PR that isn't open, `ReviewRun` uses the provider's `base.sha`
(the base as it was) for the merge base and the agent-config reset. A PR with an empty diff is
refused before the agent runs.

### Claude's limit fields count as a limit

Found on a real session limit: Claude Code 2.1.296's wrapper carries `api_error:
"usage_limit_reached"` and `api_error_status: 429` next to the message. The configurable patterns
still parse the reset time from the message; the fields catch a limit whose wording no pattern
matches yet, with the default 30-minute pause.
