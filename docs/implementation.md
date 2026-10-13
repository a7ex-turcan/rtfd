# rtfd: implementation notes

Where the code goes further than [`spec.md`](spec.md) says, or settles something the spec left
open, and why. The spec is updated in the same change (its "Decisions taken" log, from 42 on);
this file keeps the reasoning that doesn't belong in the design. Newest first.

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
