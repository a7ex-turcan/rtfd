# rtfd: a PR review watcher for any AI agent

**rtfd**: Read The Fine Diff. For the client deck: Review The Full Diff. It sits next to rtfq (databases), rtfm (docs) and rtfc (relay between sessions). It watches pull requests, hands each change to whichever AI agent you configure, and posts the review back to the PR. On a clean pass it says "lgtfm".

Status: design settled for v0. Nothing is built yet.

**How to read this doc.** The sections up to "Scope" describe the current design. "Decisions taken" is the log of how it got there; entries replaced by later ones are marked. Where the two disagree, the design sections win.

## Why

The same watcher has been built twice by hand:

| | Work machine | Personal machine |
|---|---|---|
| Provider | Bitbucket | GitHub |
| Agent | GitHub Copilot CLI | Claude Code (`claude -p`) |
| Glue | PowerShell loop + scheduled task per repo | PowerShell loop + scheduled task per repo |
| Posts as | a shared automation account | the repo owner (comment only) |

Both work, and both hit the same problems one at a time. Every new repo or agent is another copy of the script carrying all of them. A tool would solve them once.

## What the scripts taught us

These are the requirements that came out of running the two watchers. Each one cost a debugging session.

1. **Exit code 0 is not a posted review.** The agent can exit cleanly while its post was refused, for example by a permission mode or a blocked command. The loop has to check the provider for a posted review on that commit, and retry only if there is none.
2. **Usage limits must not count as failed attempts.** A Claude session limit used up all 3 attempts on a PR in 11 minutes, so the PR was never reviewed. The tool must spot the limit message, pause until the stated reset time, and give the attempt back. Two watchers on the same Claude account hit the same limit the same day, so the pause has to cover everything that uses that account.
3. **Agents in locked-down modes fight structured output.** Claude's don't-ask mode refuses heredocs and multi-line shell commands. The JSON had to go into files and then to `gh api --input`. If the tool does the posting, this goes away.
4. **Keeping a background process alive on Windows is its own job.** Problems seen:
   - the default stop-on-battery setting and a 72 h task limit killed a service;
   - a "hidden" pwsh window was hosted by Windows Terminal and closed by hand;
   - restart-on-failure doesn't catch either of those.

   The answer is one service process with a `status` command, rather than a scheduled task per repo. Its console must be headless (no window to close).
5. **A quiet log doesn't prove the loop is alive.** The loops don't log idle polls, so "is it watching?" needs a real answer: `rtfd status`.
6. **PR content is untrusted.** One repo is public. Anyone can open a PR whose text tries to steer the agent, and today the agent holds a token that can write. Reviews are limited to an author allowlist, forks and drafts are skipped, and the prompt treats PR content as material, never as instructions. But the real fix is structural (see the next section).
7. **Self-review rules differ by provider.** GitHub refuses to let the PR author approve or request changes on their own PR, so those reviews are COMMENT only. Bitbucket allows it through the API, and it shouldn't be left in place. The posting identity and the allowed verdict are per-repo settings, and rtfd checks the identity against the author before every approve or request-changes.
8. **Follow-up reviews are most of the value.** After the author pushes fixes, the reviewer should:
   - reply "Looks good now." on each fixed thread and resolve it;
   - leave open threads alone;
   - review only what's new.

   That needs rtfd to recognise its own earlier threads reliably (see Posting and idempotency).
9. **Review tone is a product requirement.** The convention is Should / Could / MINOR, short, human wording, no "Automated review:" prefix or any other visible bot marker. All of this lives in the prompt, so the prompt must be per repo and easy to edit, and edits should take effect without a restart.

## Core design decisions

### 1. The tool posts, the agent doesn't

Today the agent reads the PR *and* writes to the provider. In rtfd:

- The agent gets a checkout of the PR head (a git worktree), the diffs, the CI status, the open review threads on the PR and any hook output. It has no provider token at all: rtfd fetches everything the agent needs and passes it in.
- The agent's only output is a **findings document** (JSON, see below).
- rtfd **validates** it, decides the verdict, posts one review, and confirms it landed.

What this solves:
- Lessons 1 and 3 go away, because the tool knows whether its own post succeeded.
- Lesson 6 is contained for writes: a malicious PR can at worst produce a bad review, never a bad write.
- Any CLI that can print JSON becomes an agent: Claude Code, Copilot CLI, Codex, a local model.

### 2. The agent can only read, and only what rtfd hands it

Taking the provider token away is not the whole story. An agent that is prompt-injected by PR content can still do damage through four other routes:

- **Inherited environment and credentials.** Environment variables inherited from rtfd (including provider tokens), and the agent CLI's own credentials on disk.
- **A shell.** It can run code from the PR (install scripts, test runners, build steps), and even "read-only" commands can write: `git diff --output=<path>` and `git log --output=<path>` write a file anywhere, with content the PR controls (for example a `.cmd` in the Startup folder).
- **Configuration the agent loads.** Two sources:
  - *from the PR:* the worktree is the PR head, so a PR can add or change files an agent CLI loads from a project (`.claude/settings.json`, `.mcp.json`, `CLAUDE.md`, `.github/copilot/settings.json`, `.github/copilot-instructions.md`, `AGENTS.md`, and the like). Claude Code project settings can define hooks, which are shell commands that run with no model involved;
  - *from the user:* the agent CLI also loads the user's own setup: plugins and MCP servers. On the personal machine that includes rtfc, which can send messages to the user's contacts. Copilot CLI ships with GitHub's MCP server built in, authenticated as the user's GitHub account. Both are write channels.
- **The review itself.** An agent that can read files can copy a credentials file into a finding, and rtfd would post it on the PR, publicly on a public repo.

So:

- **Every process rtfd starts gets a scrubbed environment**: the agent and the hooks alike. Provider tokens never reach either. This is easy to get wrong because rtfd itself holds them.
- **The agent's tools are deny by default.** It gets exactly:
  - file reads, confined to the worktree and the run folder;
  - rtfd's own git tools (`rtfd-git`, below);
  - a write to its one output file, when the agent uses file output;
  - any read-only context tools the repo explicitly opts into (for example rtfm for design docs).

  Nothing from the user's own setup loads: no plugins, no user MCP servers, no built-in MCP servers. Each agent's config says how to achieve that on its CLI (an isolated MCP config, skipped setting sources, disabled built-ins), and `rtfd validate` prints the full effective tool list for every agent, not just the gaps.
- **No shell, on any tier.** The agent needs no provider CLI (rtfd passes the PR data in), and the git history it needs comes through `rtfd-git`: a small MCP server rtfd runs per review, with validated arguments and fixed command lines (see "rtfd-git" below).
- **The agent never starts inside the worktree.** Its working directory is a per-run folder rtfd owns, holding the inputs (diffs, threads, CI state, hook output), rtfd's own agent settings and the output file. The worktree is passed to it as a path to read.
- **Agent configuration files in the worktree match the base branch**, not the PR head. rtfd keeps a list of these paths per agent. After checkout it overwrites each one with the base branch's version, and **deletes any that don't exist on the base branch**, so a PR can't slip in a brand new `.claude/settings.json`. It also tells the CLI to skip project-level settings where it has a way to.
- **A PR that changes agent configuration gets a finding**, graded by what kind of file it is:
  - **executable configuration** (settings that can define hooks, tools or permissions, MCP server definitions): always a Should. It runs code on every developer's machine that opens the repo with that agent;
  - **instruction files** (`CLAUDE.md`, `AGENTS.md`, `copilot-instructions.md`): a note in the summary when the author is on the allowlist, a Should otherwise. Without this, every legitimate edit to the repo's agent docs would be held as "not ready".

  Either way the reviewer judges the change with the base version loaded. It reviews the new instructions; it doesn't follow them.
- **Outgoing text is checked before posting** (see Validator). This is a speed bump, not a wall: a secret base64-encoded or split across two findings gets past it. Confinement is the real defence.
- **Trust tiers per repo.** With no shell anywhere, the tiers differ only in what outside text and code reach the run:
  - `trusted` (internal repos, or any repo whose author allowlist excludes outsiders): other people's threads are passed in, and pre-review hooks may run.
  - `untrusted` (public repos open to outside authors): neither; later, optionally a container with network limited to the model API. Not in the first versions (see scope).
- Steps that need running code (build, browser check, acceptance criteria) are **pre-review hooks** that rtfd runs. Their output goes to the agent as input. They are not permissions granted to the agent. Hooks run PR code, so they run with the same scrubbed environment and are **allowed on `trusted` repos only**.

### 3. rtfd decides the verdict, not the agent

The agent supplies findings and the summary prose. rtfd computes the verdict from policy, for example "any open `should` means not ready". The agent can never post "ready" next to two Shoulds, and the verdict always matches the findings. Severity names live in the schema because rtfd reasons about them. Tone and wording live in the prompt.

"Open" means everything still unresolved, not just this run's findings:
- the new findings from this run;
- plus rtfd's own threads from earlier reviews that are still unresolved, after this run's `resolve` actions.

rtfd stores each thread's severity when it posts it. **Resolution state is read from the provider on every run**, never trusted from the store, because people resolve threads by hand.

A thread a person resolved counts as closed (`human_resolve_closes`, default yes, on one-person and team repos alike). The author can overrule the reviewer, **but never silently**: the next review's summary says so ("1 Should closed by hand without a change"). It is reported **once**: rtfd marks the thread as reported in its store, so later summaries don't repeat it. rtfd does **not** reply on the thread itself; a reply stirs a thread the author deliberately closed. A repo that wants a hand-resolved Should to keep the PR not ready sets `human_resolve_closes` to no.

Overrides:
- A **partial review** (diff over the size cap) is never "ready", whatever the findings say. It posts as not ready, or as a plain comment where the repo only allows comments, and the summary says what was skipped.
- An **approve or request-changes** is only posted when the posting identity is not the PR author (checked through the provider's whoami). Otherwise it is downgraded to a comment.

## Findings contract (draft)

```json
{
  "schema_version": 1,
  "summary": "One Should open: first-launch crash in Foo.",
  "findings": [
    {
      "severity": "should | could | minor",
      "path": "src/app/Foo.swift",
      "line": 42,
      "side": "new | old",
      "anchor": "let value = cache[key]!",
      "body": "Should: this force-unwraps a value that is nil on first launch."
    },
    {
      "severity": "could",
      "path": null,
      "body": "Could: the PR description doesn't mention the migration."
    }
  ],
  "threads": [
    { "id": "12345", "action": "resolve", "reply": "Looks good now." },
    { "id": "12346", "action": "reply", "reply": "Still crashes when the list is empty." }
  ]
}
```

Rules:
- `path: null` is a whole-PR comment.
- `anchor` is a short piece of the code the finding refers to. Models get line numbers wrong often enough to matter, so the validator snaps the line to where the anchor actually is, or rejects the finding.
- Anchors are validated against the **full PR diff**, even on a follow-up pass. A fix in one hunk can make a finding on another hunk worth raising.
- A finding on a line outside the full PR diff is folded into the summary, not dropped.
- No `verdict` field: rtfd computes it (decision 3).
- `threads` may only name rtfd's own threads. Other people's threads are input only (see below).
- The schema is versioned from day one.

### What goes in

The run folder holds:
- **the full PR diff** (head against its merge base), always;
- **the interdiff** on a follow-up pass: only what the author changed since the last review (see the change detector). The prompt says to focus the review there and use the full diff as context;
- the CI state and any hook output;
- the PR's open review threads, in two groups:
  - **rtfd's own threads**, which it may reply to or resolve;
  - **other people's threads**, read-only, so the agent doesn't repeat a point a person already made. On trusted repos only: on a public repo those comments are outside text, so they wait for the untrusted tier.

### Getting the document out of the agent

Agents don't all print clean JSON. For example, `claude -p --output-format json` wraps the answer in its own JSON, with the model's text as a string in `result`. So each agent's config says where the findings come from:
- `output: stdout`, with an optional `extract` step: a JSON path such as `.result`, then the last fenced JSON block in that text;
- `output: file`: rtfd passes a path (`RTFD_OUTPUT`) and the agent writes the document there. That needs a write permission scoped to that one file, which lives in the per-run folder, not the worktree.

## rtfd-git

The agent's only way to look at git history. rtfd starts it per review as a stdio MCP server (`rtfd git-tools --run <run-folder>`), bound to that review's mirror, worktree and SHAs.

Tools:
- `log(path?, max?)`: commits on the PR, or touching a path, up to a cap;
- `show(sha, path?)`: one commit, or one file at one commit;
- `blame(path, start, end)`: a line range at the PR head;
- `diff(from, to, path?)`: between two commits of this PR or its merge base.

Rules:
- every `sha` must be the PR head, the merge base, the last reviewed head, or a commit between them;
- every `path` must resolve inside the repo; no absolute paths, no `..`;
- command lines are fixed by rtfd: no `--output`, no pager, `--no-ext-diff`, `--no-textconv`, and the mirror's own config only (see Run manager);
- output is capped in size; a truncated result says so.

## Validator

Before anything is posted:
- the document matches the schema;
- every path is in the full PR diff, and every line is in it or snapped through its anchor;
- severities are known;
- every thread id is one of **rtfd's own threads** (see Posting and idempotency). rtfd never replies to or resolves a human reviewer's thread;
- **secrets in the outgoing text**: the summary, every finding body and every reply are checked against:
  - the values of the token env vars rtfd holds;
  - the gitleaks rule set, loaded as data, plus extra patterns from the config.

  What happens on a match depends on where the secret came from:
  - **It is in the diff** (the PR commits a credential): rtfd redacts it in the post (`ghp_…REDACTED`) and adds a Should of its own: "This commits a credential. Remove it and rotate it." This is often the most important thing the review says, so it is never silently dropped.
  - **It is not in the diff** (for example, one of rtfd's own tokens, or a file from outside the PR): that looks like exfiltration. The whole post is blocked and counts as `fatal` for that PR, surfaced in `status` and through rtfc. It is not retried, because a retry would likely do the same.

If validation fails for any other reason, the run counts as `invalid`: retry with the errors appended to the prompt.

## Posting and idempotency

**Recognising its own threads.** rtfd's own threads are **the comment ids in its state store whose author is the posting identity**. Both must hold. A hidden marker alone isn't reliable:
- on GitHub, a marker in the review body doesn't reach the inline comments that start the threads;
- Bitbucket Cloud may render a `<!-- -->` comment as visible text, which breaks lesson 9.

**No duplicate reviews.** The state store is written ahead of the post:
1. Before posting, rtfd records "run 7 posting for SHA abc123", with the time and **a hash of every body it is about to post** (the summary, each inline comment, each reply).
2. It posts the review.
3. It records the comment ids it got back.

If rtfd crashes between steps 1 and 3, the next run sees the unfinished record. It lists comments by the posting identity since that time, and **matches them by body hash**. Matching by author and time alone isn't enough: on GitHub the posting identity is the repo owner, who also comments by hand, and on Bitbucket it is a shared automation account other tools may post as. Found means the review landed: record its ids and stop. Not found means post again.

**Confirming the post.** After posting, rtfd reads the review back and confirms it is there, with its inline comments.

**A lost state store.** On GitHub, the review body carries a hidden marker (`<!-- rtfd sha=abc123 run=7 -->`), and each review lists its own inline comments, so rtfd can rebuild its thread list from the provider. The severity comes from the comment's leading word (Should / Could / MINOR). Where nothing can be rebuilt (Bitbucket), rtfd's older threads are treated like other people's threads: read-only input, never resolved by rtfd.

## Architecture

```
            +----------------+       +------------------+
 config --> |   scheduler    | ----> | provider adapter |  GitHub, Bitbucket (later GitLab, Azure DevOps)
            | (one process)  |       |  list / diff /   |
            +-------+--------+       |  checks / post / |
                    |                |  reply / resolve |
                    v                |  whoami / vis.   |
            +----------------+       +------------------+
            | change detector|  new PR, new commits, CI settled, debounced,
            +-------+--------+  rebase-aware, interdiff
                    v
            +----------------+       +------------------+
            |   run manager  | ----> |  pre-review hooks|  build, browser check, ... (run by rtfd,
            | mirror, work-  |       +------------------+   scrubbed env, trusted repos only)
            | tree, run      | ----> +------------------+       +-----------+
            | folder, lock,  |       |  agent adapter   | <---> |  rtfd-git |  log / show / blame / diff
            | timeout, retry |       |  prompt in, JSON |       |  (MCP)    |  validated args, fixed cmds
            +-------+--------+       |  out, failure    |       +-----------+
                    |                |  classification  |  scrubbed env, cwd = run folder,
                    v                +------------------+  deny-by-default tools, no shell
            +----------------+
            | validator +    |
            | verdict policy |  schema, anchors vs full diff, own threads only,
            | + poster       |  secret scan + redact, write-ahead dedupe by hash, verify posted
            +-------+--------+
                    v
                 state store (per PR, per head commit; per agent account)
```

**Provider adapter.** It lists open PRs, with author, draft flag, fork flag and head commit. It also provides:
- the diff, the checks, and all review threads with their author and resolution state, including who resolved them;
- posting one review with inline comments, and returning the ids of everything it created;
- replying to and resolving a thread;
- comments by a given identity since a given time, with their bodies, for the write-ahead check;
- whoami, for the self-review check and for recognising its own threads;
- the repo's visibility (public or private), for the config checks;
- the clone URL and the ref to fetch the PR head from (GitHub has `refs/pull/N/head`; Bitbucket has the source branch, or the fork's);
- on GitHub: the reviews carrying rtfd's marker and their inline comments, for rebuilding a lost store.

Each provider has its own quirks. For example, Bitbucket's comment list doesn't reliably report whether a thread is resolved, so each thread has to be checked through the single-comment endpoint. And inline anchors (`to`/`from` on Bitbucket, `line`/`side` on GitHub) differ between providers.

**Change detector.** Triggers on a new PR, new commits or a CI change. It consumes events, so polling feeds it today and webhooks can feed it later. Before a review it waits for two things, separately:
- **Quiet period:** no new pushes for `quiet_period`, so five quick pushes produce one review, not five. Only pushes reset it.
- **CI settled:** all checks finished. A repo with no checks configured doesn't wait. Checks still pending after `ci_max_wait` don't block forever: the review goes ahead and the summary says CI hadn't finished. A later CI result triggers a CI-change pass as usual.

It also decides what a follow-up pass looks at:
- **Normal push:** the last reviewed head can be fetched. rtfd builds the interdiff:
  1. take the PR diff as it was at the last review (old head against its merge base);
  2. apply it onto the new merge base, in a scratch worktree;
  3. diff that against the new head.

  What remains is exactly what the author changed, without code that came in from merging the base branch into the PR. (A plain `git diff old_head new_head` would include other people's base-branch code.)
- **The apply conflicts, the last reviewed head can't be fetched** (after a force-push it may be gone from the remote), **or it is no longer an ancestor** (rebase): a full review of the PR. The existing threads are kept and passed in as usual. Inline threads on lines that moved show as outdated on the provider; their anchors are what the agent uses to decide whether they're fixed.

**Run manager.** rtfd keeps **its own bare mirror of each repo** under its data directory. It never touches the user's working clones: their refs, config and lock files stay theirs, and the mirror's git config is rtfd's alone (no hooks, no fsmonitor, nothing that runs a command). Per run, it creates:
- a worktree from the mirror, keyed by SHA, with agent configuration files reset to the base branch (overwritten, or deleted where the base branch has none);
- a run folder next to it, holding the inputs, rtfd's agent settings (permissions, the isolated MCP config with `rtfd-git` and any opted-in context tools) and the output file. This is the agent's working directory.

Both are deleted afterwards. Runs go in parallel across repos and serially within one repo. It applies path excludes (lockfiles, generated code, snapshots) and a diff size cap. An oversized diff is reviewed partially, the summary says so, and the verdict can't be "ready" (decision 3).

**Agent adapter.** It is:
- a command template;
- a prompt file;
- an output setting (stdout with extraction, or a file);
- the **account** it runs under;
- the list of configuration files the agent loads from a project, split into executable and instructions;
- how to isolate it from the user's own setup (MCP servers, plugins, settings, built-in tools);
- how to render the deny-by-default tool list into its permission flags.

It sorts each run's outcome:
- `ok`: valid JSON that passes the validator;
- `invalid`: JSON missing, bad, or rejected by the validator. Retry with the errors appended, and count the attempt;
- `limit`: a usage or rate limit. Pause **every repo whose agent uses that account** (two agent entries can share one subscription) until the parsed reset time, and don't count the attempt. The limit-message patterns live in config per agent, because the wording will change. Parsing the reset time:
  - a time zone, when given, is honoured;
  - a time with no date that is already past today means tomorrow;
  - a date, when given (weekly limits), is used as is;
  - no time at all means a 30-minute pause;
- `transient`: network, timeout. Retry with backoff;
- `fatal`: auth, missing binary, or a secret not from the diff caught by the validator. Stop the repo (or the PR, for a secret) and surface it in `status`.

**State store.** Per repo and PR:
- the last reviewed head commit;
- attempts per commit;
- the last CI state seen;
- the write-ahead posting records, with body hashes, and the ids of everything rtfd posted;
- each of rtfd's threads with its severity, and whether a hand resolve has been reported (resolution itself is always re-read from the provider);
- the hash of the prompt file used for each review, so it's clear which prompt produced which tone.

Per agent account: the paused-until time.

SQLite, or a JSON file per repo.

**Policy.** Per repo:
- the trust tier;
- the author allowlist;
- skip forks and drafts;
- max attempts per commit;
- the allowed verdicts (comment only / approve / request changes). Approve and request-changes are always subject to the self-review check;
- the verdict rule (for example, any open `should` means not ready), and whether a person's resolve closes a thread (`human_resolve_closes`);
- read-only context tools to offer the agent, if any;
- path excludes and the diff size cap;
- pre-review hooks (trusted repos only);
- the posting identity, as a token env var name, never the value;
- whether to re-review on a CI change only, or on any push.

rtfd refuses to start a repo whose config contradicts itself:
- `authors: ["*"]` on a public repo (visibility from the provider), while the untrusted tier doesn't exist;
- hooks on an untrusted repo.

## Config (draft)

```yaml
defaults:
  data_dir: D:\rtfd                   # mirrors, worktrees, run folders, state; defaults to the home (decision 40)
  interval: 5m
  quiet_period: 3m
  ci_max_wait: 30m
  max_attempts: 3
  timeout: 30m
  max_diff_lines: 3000
  exclude: ["**/*.lock", "**/package-lock.json", "**/__snapshots__/**"]
  verdict_rule: { not_ready_if_open: [should] }
  human_resolve_closes: true          # a hand resolve closes the thread; the next summary says so, once
  context_tools: []                   # read-only MCP tools a repo opts into, e.g. [rtfm]
  secrets:
    rules: gitleaks                   # bundled gitleaks rule set, loaded as data
    extra: []                         # more regexes, if needed

agents:
  claude:
    account: claude-personal          # pauses are per account
    command: claude -p --permission-mode dontAsk --output-format json
    model: opus
    effort: medium
    output: { from: stdout, extract: ".result" }   # Claude wraps the answer in its own JSON
    isolation:                        # nothing from the user's own setup loads (rtfc, rtfq, rtfm, plugins)
      mcp: strict                     # --strict-mcp-config --mcp-config <run folder>/mcp.json (rtfd-git + opted-in tools only)
      settings: none                  # skip user and project setting sources; verify --setting-sources on the installed version
      plugins: none                   # verify user plugins don't load in -p mode with the above
    tools:                            # rendered as permission rules in the run folder; dontAsk refuses everything else
      read: [worktree, run_folder]
      mcp: [rtfd-git]
    project_config:                   # reset to the base branch; deleted if the base has none
      executable: [".claude/settings.json", ".claude/settings.local.json", ".claude/**/*.json", ".mcp.json"]
      instructions: ["CLAUDE.md", "**/CLAUDE.md", ".claude/**/*.md"]
    limit_patterns:
      # e.g. "You've hit your session limit · resets 12:10am (Europe/Chisinau)"
      - "(?i)hit your (session|usage|weekly) limit.*?resets\\s+(?<date>[A-Z][a-z]{2}\\s+\\d{1,2},?\\s+)?(?<reset>\\d{1,2}(:\\d{2})?\\s*[ap]m)(\\s*\\((?<zone>[^)]+)\\))?"
      - "(?i)usage limit reached|rate limit"

  copilot:
    account: copilot-work
    command: copilot -p
    model: gpt-5
    output: { from: file }            # rtfd passes RTFD_OUTPUT
    isolation:
      mcp: no-builtin                 # disable the built-in GitHub MCP server; check `copilot help` for the flag
      paths: [run_folder, worktree]   # working directory + --add-dir <worktree>
    tools:                            # rendered as --allow-tool / --deny-tool; deny takes precedence over allow
      allow: ["rtfd-git", "write(<RTFD_OUTPUT>)"]
      deny: ["shell", "url"]          # no blanket write deny: it would also block the output file
    project_config:
      executable: [".github/copilot/settings.json", ".github/copilot/settings.local.json", ".github/copilot/**"]
      instructions: [".github/copilot-instructions.md", ".github/instructions/**", "AGENTS.md"]

repos:
  - name: nutrx
    provider: github
    repo: owner/nutrx                 # rtfd keeps its own mirror; no local checkout path
    trust: trusted                    # public, but only the owner's PRs are reviewed
    agent: claude
    prompt: prompts/review.md         # re-read on every run
    authors: [owner]
    verdicts: [comment]               # GitHub forbids self-approval and self-request-changes
    token_env: GH_TOKEN

  - name: work-ui
    provider: bitbucket
    repo: workspace/some-ui
    trust: trusted
    agent: copilot
    prompt: prompts/review.md
    hooks:                            # needs hooks, so not in v0 or v1
      - name: browser-check
        command: pwsh scripts/check-acceptance.ps1   # scrubbed env; output goes to the agent as input
    authors: ["*"]                    # private repo, so allowed
    verdicts: [approve, request_changes]
    token_env: BB_TOKEN
```

## CLI (draft)

| Command | What it does |
|---|---|
| `rtfd serve` | One long-running process for every configured repo |
| `rtfd install-service` | Registers `serve` as a Windows service or scheduled task with the settings learnt the hard way (no battery stop, no time limit, headless console), or a systemd/launchd unit |
| `rtfd status` | Per repo: alive, last poll, open PRs, last review per PR, attempts left, blocked PRs. Per agent account: paused until |
| `rtfd run <repo> --pr 12 [--dry-run]` | One review now. Dry-run prints the inputs, the findings, the computed verdict and the review it would post, with any redactions marked |
| `rtfd retry <repo> --pr 12` | Clears used-up attempts for the PR's head commit, or a secret block after it was looked at |
| `rtfd pause/resume <repo>` | Replaces the `.stop` file convention |
| `rtfd validate` | Checks the config (including the contradictions above), the tokens (by calling the provider's whoami), the agent binaries, and prints each agent's **full effective tool list** |
| `rtfd git-tools --run <folder>` | Internal: the per-review `rtfd-git` MCP server. Started by rtfd, not by hand |

## Relationship to the rest of the family

- **rtfc** already polls Bitbucket PRs, Jira and Confluence into an inbox. But its rule is that it never writes to a source, and rtfd is all about writing. So: a sibling tool, sharing provider client code and credential handling where it makes sense. rtfd notifies through rtfc when its state changes (see the decisions below). The reviewing agent itself never gets rtfc's tools.
- **rtfq / rtfm** can be offered to the agent as read-only context through MCP (schema questions, design docs), when a repo opts in with `context_tools`. The agent never gets write tools.
- The existing tools are .NET (`.slnx`, Directory.Build.props), so rtfd would match.

## Scope

The full list is two to three weeks of work, so it ships in steps. Each step replaces something real.

**v0: GitHub + Claude.** Replaces the two personal-machine watchers and proves the design.
- the GitHub provider;
- the claude agent adapter, with output extraction, isolation from the user's own setup, and the account-wide usage-limit pause (time zones, date rollover);
- `rtfd-git`;
- the findings contract, the validator (including the secret check with in-diff redaction) and the verdict policy (older threads re-read from the provider, hand resolves reported once, partial reviews, the self-review check);
- scrubbed environment, bare mirror, run folder as working directory, agent configuration reset to the base branch, deny-by-default tools, reads confined to the worktree;
- the finding for PRs that change agent configuration;
- the `trusted` tier only, with the config contradictions refused;
- posting with write-ahead dedupe by body hash, verification, and store rebuild from markers;
- follow-up passes on normal pushes (interdiff), resolving its own fixed threads;
- quiet period, capped CI wait, path excludes, diff size cap;
- `serve`, `status`, `run --dry-run`, `retry`, `install-service`, `validate`.

**Build order for v0.** Get real output early and let it show what's wrong:
1. **`rtfd run --dry-run`, end to end.** Config loading, the GitHub provider's read side, the mirror and worktree, the run folder, the claude adapter with isolation and `rtfd-git`, output extraction, the validator, the verdict. Print everything. Run it against a handful of real nutrx PRs and tune the prompt until the output reads right.
2. **`rtfd validate`**, including the effective tool list. Confirm on the installed Claude version that nothing from the user's setup loads.
3. **Posting:** the write-ahead record, posting, verification, then `rtfd run` without `--dry-run`.
4. **Follow-ups:** the state store's thread list, the interdiff, resolve and reply, hand-resolve reporting.
5. **`serve`:** the change detector (quiet period, CI wait), the scheduler, usage-limit pauses, `status`, `retry`, `pause/resume`.
6. **`install-service`**, then retire the personal-machine scripts.

**v1: Bitbucket + Copilot.** Replaces the work watchers that don't need a browser check.
- the Bitbucket provider (thread resolution via the single-comment endpoint; no marker, so no store rebuild);
- the copilot agent adapter, with its isolation and permission flags verified on the installed version;
- the rebase and force-push fallback;
- other people's threads as read-only input;
- rtfc notifications on state changes.

**Later:**
- pre-review hooks (needed for the work-ui browser check, which keeps its script until then);
- the `untrusted` tier, optionally with a container;
- code suggestions;
- answering author replies on threads;
- webhooks instead of polling;
- "bring your own runner": emit a job that an open session picks up through rtfc;
- two agents on one PR with merged findings;
- cost and token tracking per review;
- GitLab and Azure DevOps.

## Decisions taken

From the first brainstorm:

1. **Polling vs webhooks:** poll for now. The change detector consumes events, so webhooks can feed it later.
2. **Own runner vs bring your own:** rtfd runs the agent itself. Bring-your-own through rtfc is a later feature.
3. **Per machine vs central:** one process per machine. A central multi-user instance is a different product.
4. **Review style:** severity names and the verdict rule live in rtfd (it reasons about them). Tone and wording live in the prompt.
5. **Extra steps beyond reading:** pre-review hooks run by rtfd, output passed to the agent. Not extra agent permissions. Hooks get the scrubbed environment and run on trusted repos only.
6. **Worktrees:** per run, under rtfd's data dir, keyed by SHA, deleted after. Parallel across repos, serial within a repo. *(Refined by 33: worktrees come from rtfd's own mirror.)*
7. **Earlier threads:** passed in the input. Deterministic, cheap, needs no tools.

From the review of the first brainstorm:

8. **Code suggestions:** later. When they come, each is checked to apply cleanly to the head commit (`git apply --check`) before posting.
9. **Untrusted tier on Windows:** a restricted mode first (file tools confined to the worktree, no shell, no web, plus the secret check). Containers later. *(Since 32, every tier has no shell; the untrusted tier differs by withholding outside threads and hooks.)*
10. **Quiet period:** 3 minutes by default. Only pushes reset it. CI has its own capped wait (`ci_max_wait`).
11. **rtfc notifications:** yes, on state changes only: paused, resumed, fatal, secret blocked. Never on every poll.
12. **First versions are trusted-only.** An author allowlist of just the owner keeps outside text away from the agent even on a public repo. Open-to-all public repos wait for the untrusted tier.

From the second review:

13. **The agent never runs inside the PR's checkout.** Its working directory is a run folder rtfd owns, and agent configuration files in the worktree come from the base branch.
14. **Secrets in the diff are reported, not hidden.** Redact and add a Should. Only a secret from outside the diff blocks the post.
15. **Secret patterns:** the gitleaks rule set, loaded as data, plus extra regexes from the config.
16. **Thread resolution is always read from the provider.**
17. **Rebases fall back to a full review**, keeping the existing threads.
18. **Self-review downgrade covers request-changes too**, not just approve.
19. **Other people's threads are read-only input**, on trusted repos only.
20. **Confinement is declared per agent**, and `validate` reports what an agent can't enforce. *(Extended by 31: `validate` prints the full effective tool list.)*

From the third review:

21. **Agent configuration is reset, not just restored:** files the base branch doesn't have are deleted.
22. ~~**Shell access is an allowlist** of read-only git commands.~~ *Replaced by 32.*
23. **Own threads = stored ids + posting identity as author.** Dedupe uses a write-ahead record in the state store. *(Refined by 34: crash recovery matches by body hash; the marker is only for rebuilding a lost store.)*
24. **Follow-ups review only what the author changed in the PR's diff**, not `git diff old new`. *(Refined by 36: how the interdiff is built, and the full diff as context.)*
25. **An old head that can't be fetched counts as a rebase.**
26. **Usage-limit pauses are per agent account** (`account:`), not per agent entry.
27. **`human_resolve_closes` defaults to yes everywhere, and is never silent.** *(Refined by 37: reported once in the summary, no reply on the thread.)*
28. **Copilot on trusted repos needs no container.** *(Confirmed by 38.)*
29. **Agent configuration findings are graded:** executable configuration is always a Should; instruction files are a summary note for allowlisted authors, a Should otherwise.
30. **The work ships in steps:** v0 GitHub + Claude, v1 Bitbucket + Copilot, then the rest.

From the fourth review:

31. **The agent's tools are deny by default.** Nothing from the user's own setup loads: no plugins, no user MCP servers (rtfc is a write channel), no built-in MCP servers (Copilot's GitHub server is authenticated as the user). Repos opt into read-only context tools explicitly. `validate` prints the full effective tool list.
32. **No shell, on any tier.** Read-only git commands aren't read-only (`--output` writes anywhere), and Copilot's `shell(git:*)` filter allows every git subcommand with every argument. Git history comes through `rtfd-git`, an MCP server rtfd runs per review, with validated arguments and fixed command lines.
33. **rtfd keeps its own bare mirror of each repo.** It never touches the user's working clones; the mirror's git config is rtfd's alone. The per-repo `checkout:` setting is gone.
34. **Crash recovery matches by body hash**, recorded in the write-ahead record. Author and time alone are ambiguous: the GitHub identity is the owner, and the Bitbucket one is shared. No marker is needed for dedupe on either provider.
35. **A lost state store:** rebuilt from review markers on GitHub. Where it can't be rebuilt (Bitbucket), rtfd's older threads become read-only input, never resolved by rtfd.
36. **The interdiff** is the old PR diff applied onto the new merge base, diffed against the new head. A conflict falls back to a full review. The agent also gets the full PR diff as context, and anchors are validated against the full diff.
37. **Hand-resolved Shoulds are reported once, in the summary only.** No reply on the thread.
38. **Copilot confinement:** working directory plus `--add-dir` for paths; `--deny-tool` takes precedence over `--allow-tool`; `.github/copilot/settings.json` and `.github/copilot/settings.local.json` are executable configuration and get reset.
39. **v0 is built dry-run first:** `rtfd run --dry-run` end to end against real PRs before posting, state or `serve`.

From planning:

40. **Where the config lives.** One home folder, `RTFD_HOME`, default `~/.rtfd`, holding `rtfd.yaml` and the prompts, the same pattern as rtfm's and rtfc's homes. `data_dir` moves everything rtfd writes (mirrors, worktrees, run folders, state, logs) elsewhere, and defaults to the home. `--config <path>` points one command at another file, and relative paths in a config resolve against its folder. `install-service` writes the absolute config path into the service's command line, so the service never depends on an environment variable (rtfc found that a login item can't carry `RTFC_HOME`).
41. **What a config can and can't say about an agent.** The CLI an agent entry runs is decided by its command's executable (`claude`, `copilot`, with any path or extension). Anything else, a shell or wrapper in front of either included, is refused, because rtfd can only confine a CLI it knows. Isolation and tools are built in per CLI, and a config can't loosen them. Flags rtfd renders itself (tools, permissions, MCP config, settings, extra directories) are refused in the command, and so is any permission mode but `dontAsk` or `default`. `project_config` adds to the built-in list of files reset to the base branch and never removes from it. `limit_patterns` replace the built-in ones, because their wording changes.

## Open questions

1. **The name.** "rtfd" was Read the Docs' old GitHub org name. Check the NuGet package id and the binary name before settling on it. *(2026-10-13: the NuGet id `rtfd` is free, and `a7ex-turcan/rtfd` was free and now holds this repo. The siblings ship through GitHub Releases only, so the NuGet id matters little.)*
2. **Isolation flags on the installed CLI versions.** For Claude: `--strict-mcp-config`, `--setting-sources`, and whether user plugins load in `-p` mode. For Copilot: the flag that disables the built-in GitHub MCP server, and whether `-p` mode refuses every tool that isn't explicitly allowed. `rtfd validate` should prove each answer, not trust it.
3. **A dedicated OS account for the service**, as a fallback or as the default. Running `rtfd serve` under its own local account gives the agent CLIs an empty user setup by construction, and no access to the user's home directory. The cost: logging each agent CLI in under that account.
4. **Bitbucket without a marker.** A lost state store turns rtfd's older threads read-only there (decision 35). Acceptable, or is it worth backing up the store?
