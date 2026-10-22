# rtfd: Read The Fine Diff

A pull request review watcher for any AI agent. rtfd watches your PRs, hands each change to the
agent you configure (Claude Code, Copilot CLI, anything that can print JSON), and posts the review
back to the PR. When there's nothing to say, it says "lgtfm".

Sibling of [rtfm](https://github.com/a7ex-turcan/rtfm) (docs),
[rtfq](https://github.com/a7ex-turcan/rtfq) (databases) and
[rtfc](https://github.com/a7ex-turcan/rtfc) (relay between sessions).

**Status: early. It watches, reviews, posts and follows up.** `rtfd serve` watches every configured
repo and reviews each new head once, when the pushes have settled and CI has finished; `rtfd status`
says whether it is watching. `rtfd run <repo> --pr <n>` reviews one GitHub, Azure DevOps or Bitbucket PR with Claude Code now,
posting once, crash-safe, and reading it back; `--dry-run` prints it instead. After a push, rtfd
reviews only what changed and resolves its own threads the author fixed. `rtfd validate` checks the
config, the tokens and exactly what the agent loads. Starting `serve` at logon (`install-service`)
comes next. The design is [`docs/spec.md`](docs/spec.md) and the build order is
[`docs/plan.md`](docs/plan.md).

---

## Getting started

This gets you from a clone to a review of one of your PRs: printed in the terminal first, then
posted when you're happy with it.

### What you need

- **The .NET 10 SDK** (10.0.300 or later), only to build rtfd from source. A release archive
  needs nothing else.
- **git** 2.25 or later on `PATH`.
- **Claude Code**, installed and logged in: run `claude` once and log in. rtfd starts Claude with a
  short allowlist of environment variables (`PATH`, your profile folders, the temp folder), so a
  login is what works. An `ANTHROPIC_API_KEY` set only in your environment doesn't reach it.
- **A GitHub token that can read your repo.** The easiest is the GitHub CLI's: `gh auth token`.
  On Azure DevOps, the Azure CLI logged in to your organization's tenant, or a PAT (see
  [On Azure DevOps](#on-azure-devops)). On Bitbucket, an Atlassian API token (see
  [On Bitbucket](#on-bitbucket)).

Tested on Windows 11 with Claude Code 2.1.296 and git 2.47.

### 1. Get it

Download the archive for your platform from the
[releases page](https://github.com/a7ex-turcan/rtfd/releases): `win-x64` (a zip), `linux-x64` or
`osx-arm64` (a tar.gz). Unpack it anywhere and add the folder to your `PATH`. It holds `rtfd`
(`rtfd.exe` on Windows) with its SQLite library, `examples/` for step 2, and these docs. Each
release's notes are its section of [`CHANGELOG.md`](CHANGELOG.md), and `SHA256SUMS` lists the
archives' checksums.

Before the first release, or for the latest `main`, build it instead:

```bash
git clone https://github.com/a7ex-turcan/rtfd.git
cd rtfd
dotnet publish src/Rtfd -c Release -o "$HOME/.rtfd/bin"
```

That puts `rtfd` in `~/.rtfd/bin`. Add the folder to your `PATH`, or call rtfd by its full path. To
update later, pull and run the same `dotnet publish` again.

### 2. Set up your home

rtfd reads its config from `~/.rtfd/rtfd.yaml` (`%USERPROFILE%\.rtfd\rtfd.yaml` on Windows). Set
`RTFD_HOME` to use another folder, or pass `--config <path>` to any command. Start from the sample,
in the unpacked archive or the clone:

```bash
# bash
cp -r examples/rtfd.yaml examples/prompts "$HOME/.rtfd/"
```

```powershell
# PowerShell
Copy-Item -Recurse examples/rtfd.yaml, examples/prompts "$HOME/.rtfd/"
```

Then edit `~/.rtfd/rtfd.yaml`. The sample watches the author's repos, so change the `repos` list
to yours:

```yaml
repos:
  - name: myapp                       # what you call it on the command line; also names folders
    provider: github
    repo: your-name/myapp             # owner/name on GitHub
    trust: trusted
    agent: claude
    prompt: prompts/review.md         # relative to rtfd.yaml
    authors: [your-name]              # only these authors' PRs are reviewed
    verdicts: [comment]
    token_env: GH_TOKEN               # the NAME of the variable that holds the token
```

`token_env` names a variable; it never holds the token itself, and rtfd refuses a config where it
looks like one. Keep `authors` to people you trust: their PR text and code reach the agent.

### 3. Check the config

Put the token in the variable you named, for this shell only:

```bash
# bash
export GH_TOKEN="$(gh auth token)"
```

```powershell
# PowerShell
$env:GH_TOKEN = gh auth token
```

Then:

```bash
rtfd validate
```

It checks three things, and exits non-zero until there are no errors:

- **The config.** Every mistake comes with its line number (`error [config.unknown_key] line 14,
  repos[0].promt: ...`). A warning that `GH_TOKEN is not set` means the variable isn't in this shell.
- **Each repo**, with GitHub: the token works, the repo is reachable, and who rtfd reads and will
  post as.
- **Each agent.** rtfd starts Claude exactly as a review would and prints what Claude itself
  reports it loaded, then stops it before it calls a model, so it costs nothing.

For the agent, the output looks like this:

```text
Agents:
  claude: 2.1.296 (Claude Code) (C:\Users\you\.local\bin\claude.exe)
    tools: Glob, Grep, Read; rtfd-git: blame, diff, log, show
    MCP servers: rtfd-git (connected)
    plugins: none; built into the CLI: cc-plugin-agents-md, cc-plugin-telemetry, cc-plugin-plugin-authoring
    skills: none; commands: none
```

Anything more (a shell, a write tool, one of your MCP servers or plugins) is an error that names
it. `--config-only` skips the GitHub and agent checks.

### 4. Run a review

```bash
rtfd run myapp --pr 12 --dry-run
```

rtfd reads the PR, fetches it into its own mirror (your clones aren't touched), checks it out, runs
Claude on it read-only and prints the review it would post. A review takes a few minutes. A merged
PR works too: rtfd reviews it against the base it was compared with, which makes your PR history a
handy place to try it. The output looks like this:

```text
PR #39  Onboarding: Done in the bottom panel instead of a keyboard toolbar (closes #19)
  author a7ex-turcan · base main · head fix/onboarding-keyboard-cycles
  CI: none
  Posting identity: a7ex-turcan (the PR author)
  Files: 3 reviewed, 0 excluded, 0 skipped (+56 -49)
  The PR modified agent config: CLAUDE.md (instructions)

Attempt 1: ok

Review, as it would post: COMMENT
  Verdict: ready (nothing blocking is open); APPROVE isn't in verdicts, so COMMENT

--- summary ---
Ready to merge from my side.

Moving Done into the panel and dropping the NavigationStack is safe: ...

--- CHANGELOG.md:40 (new) ---
MINOR: "the button" reads as the Done button just mentioned, but it means Start tracking. ...

Dry run: nothing was posted.
```

| Flag | What it does |
|---|---|
| `--dry-run` | Print the review instead of posting it. Records nothing and counts no attempt |
| `--verbose` | Also print the agent's raw answer, with secrets redacted |
| `--inputs-only` | Stop before the agent: just the PR, the diff and the files rtfd would hand over |
| `--keep` | Leave the run folder and the worktree on disk, and print where they are |
| `--config <path>` | Use another config file |

The command exits 0 when a review came out, and 1 when it didn't; the last line says why.

### 5. Post it

Without `--dry-run`, the same command posts:

```bash
rtfd run myapp --pr 12
```

```text
Posted the review as COMMENT, with 2 inline comments. https://github.com/you/myapp/pull/12#pullrequestreview-...
Read back from GitHub: the summary, the event and every inline comment are there as sent.
```

- **Once per head.** A PR head that has a review is refused until a new push. If rtfd dies
  mid-post, the next run finds the review on GitHub by the hash of its text and doesn't post it
  again, or posts what it had recorded if it never landed.
- **Attempts.** Each head gets `max_attempts` agent runs (3 by default). When they're used up,
  `rtfd retry myapp --pr 12` gives them back. The same command lifts the block on a head whose
  review held a secret that wasn't in the diff, once you've looked.
- **The verdict.** It posts as COMMENT unless `verdicts` allows more, and never approves or
  requests changes on a PR the token's own account wrote: GitHub forbids it, so rtfd downgrades it
  to a comment and says so.

### 6. Follow up after a push

When the author pushes fixes, run the same command again. It's a follow-up now:

```text
  Follow-up on 34823a2e8e25: the interdiff has 2 file(s) (+8 -1)
...
--- thread 4280907838 (tally/counter.py:31, Should): resolve ---
Looks good now.

Posted the review as COMMENT, with 0 inline comments, and 1 reply on earlier threads (1 resolved).
```

- **Only what changed.** The agent reviews the interdiff: what the author changed since the last
  review, without anything that came in from merging the base branch, with the full diff as
  context. A rebased PR gets an interdiff too, from the head rtfd kept in its mirror. A push that
  only merges the base branch isn't reviewed at all. When the changes can't be told apart from the
  base branch's, the whole PR is reviewed again.
- **Its own threads.** rtfd replies "Looks good now." on each of its threads that is fixed and
  resolves it, and leaves the rest alone. It never touches a thread someone else started.
- **What's still open counts.** "Not ready: 1 Should open." counts this review's findings and
  rtfd's earlier threads that are still unresolved, and the summary says which.
- **Resolved by hand.** A Should someone resolves without rtfd counts as closed, and the next
  summary says so, once. Set `human_resolve_closes: false` to keep such a thread counting until rtfd
  resolves it.
- **A lost `state.db`.** rtfd rebuilds what it needs from GitHub: every review it posted carries a
  hidden marker, and every comment leads with its severity.

### 7. Leave it watching

`rtfd serve` does what you've been doing by hand, for every repo in the config:

```bash
GH_TOKEN="$(gh auth token)" rtfd serve
```

```text
2026-10-15T11:01:32Z sandbox: #2 has a new head, f80410e4
2026-10-15T11:01:32Z sandbox: 2 open PRs: #1 reviewed; #2 quiet for 3m more
2026-10-15T11:04:33Z sandbox: 2 open PRs: #1 reviewed; #2 due; reviewing now
2026-10-15T11:05:07Z sandbox #2: posted the review as COMMENT, with 2 inline comments: https://github.com/...
2026-10-15T11:05:09Z sandbox: 2 open PRs, nothing to do (#1 reviewed; #2 reviewed)
```

- **When.** Every `interval` (5m) it polls the open PRs. A new head waits until nothing has been
  pushed for `quiet_period` (3m), so five quick pushes get one review, and until CI has finished, or
  `ci_max_wait` (30m) has passed, when the summary says CI hadn't finished. A repo with no checks
  doesn't wait. A CI result on a head that's already reviewed changes nothing; the next push gets the
  next review.
- **What.** Your `authors`' PRs, not drafts, not forks. Each head is reviewed once, and follow-ups
  work as in step 6.
- **Usage limits.** A limit pauses every repo on that agent `account` until the reset time Claude
  gives, without spending an attempt, and serve goes on by itself afterwards.
- **`rtfd status`** says whether serve is running (and exits 0), stopped, or hung, then per repo
  the last poll and each open PR: reviewed, waiting and until when, out of attempts, blocked.
- **`rtfd pause <repo>`** stops watching one repo and **`rtfd resume <repo>`** starts again, from
  serve's next tick, no restart. Resume also lifts a stop after something only you can fix, such as
  Claude being logged out.
- **Stopping.** Ctrl+C. A post already on its way finishes; a review in progress is dropped and
  runs again at the next start. If serve is killed instead, nothing is posted twice, and the next
  start cleans up what it left.
- **One at a time.** One serve per data folder. An `rtfd run` on a repo serve is reviewing waits a
  few seconds, then says so.
- **The config is read once**: restart serve after editing it (it logs that the file changed). The
  prompt is read for every review, as with `run`.

Until `rtfd install-service` arrives (it starts serve at logon, with no window), run it in a
terminal you leave open. The log is also in `logs/` in the data folder, a file per day.

### 8. Make the reviews yours

`prompts/review.md` is the reviewer's brief: what to look at, how severe is severe, and how to
write. rtfd re-reads it on every run, so edit it and run the same PR again until the review reads
like one you'd post. rtfd adds its own part after it: where the inputs are, and the format of the
answer. Each printout names the prompt's hash, so you can tell which version produced which tone.

### On Azure DevOps

Azure DevOps Services repos work the same way, with a few differences in the config:

```yaml
  - name: web-app
    provider: azure_devops
    repo: contoso/Fabrikam Fiber/web-app   # organization/project/repository, spaces and all
    trust: trusted
    agent: claude
    prompt: prompts/review.md
    authors: [ada@contoso.com]             # sign-in names, as Azure DevOps shows them
    verdicts: [comment, approve, request_changes]
    auth: azure_cli                        # instead of token_env
    tenant: contoso.onmicrosoft.com        # your organization's tenant, if it isn't az's default
```

- **The token.** With `auth: azure_cli`, rtfd asks the Azure CLI for an Entra ID token itself
  (`az account get-access-token`), keeps it in memory and asks again before it expires, so `serve`
  keeps going for days. Log in once with `az login --tenant <tenant>`. If your organization lets you
  make PATs, `token_env: ADO_TOKEN` with a PAT that can read code and write pull request threads
  works too.
- **The review** is a summary comment (posted resolved, so it never holds a PR whose policy wants
  every comment resolved) and a thread per inline comment. A crash halfway through posts only what
  didn't land.
- **The verdict is a vote**: approve is "Approved", request changes "Waiting for author". Never on
  your own PR, and never on a draft, which Azure DevOps doesn't allow.

### On Bitbucket

Bitbucket Cloud repos work the same way too:

```yaml
  - name: web-app
    provider: bitbucket
    repo: contoso/web-app                  # workspace/repository
    trust: trusted
    agent: claude
    prompt: prompts/review.md
    authors: [Ada Lovelace, grace.hopper]  # nicknames, as Bitbucket shows them on your profile
    verdicts: [comment, approve, request_changes]
    token_env: BB_TOKEN                    # holding email:token
```

- **The token.** Make an Atlassian API token with Bitbucket scopes (read the account, read
  repositories, read and write pull requests) and put `your-email:the-token` in the variable. rtfd
  sends it as basic auth, and git gets the token part only. `rtfd validate` says who it posts as.
- **Authors are nicknames.** Bitbucket shows no email or user name on a PR, so `authors` lists the
  nickname each account goes by; `rtfd validate` prints yours.
- **The review** is a summary comment and a comment per inline comment. Each ends with a line
  Bitbucket doesn't show (`[//]: # (rtfd ...)`), which lets rtfd rebuild what it reviewed if its
  state is ever lost. A crash halfway through posts only what didn't land.
- **The verdict** is Bitbucket's Approve or Request changes. Never on your own PR, though Bitbucket
  would take it.

### Where things go

Everything rtfd writes lives under its data folder: the home, unless `data_dir` in the config
points elsewhere.

| Folder | What |
|---|---|
| `mirrors/` | A bare git mirror per repo, fetched into on every run. Every head rtfd reviewed stays in it, so follow-ups work after a force-push |
| `runs/`, `worktrees/` | One run's working folder and checkout. Deleted when the run ends, unless `--keep` |
| `state.db` | What rtfd posted, with the hash of every body; its threads and their severities; the attempts used per head; paused repos and accounts |
| `git/` | An empty git config and hooks folder, so your own git setup never applies |
| `logs/` | serve's log, `rtfd-yyyy-MM-dd.log`, one line per poll and per event, kept 14 days |
| `status.json` | What serve is doing, rewritten every 30 seconds; `rtfd status` reads it |
| `serve.lock`, `locks/` | One serve per data folder, and one worker per repo |

### When something goes wrong

| You see | What to do |
|---|---|
| `GH_TOKEN isn't set` | Set the variable in this shell (step 3) |
| `... isn't in myapp's authors` | The PR's author isn't in `authors` |
| `the PR comes from a fork` | rtfd skips forks |
| `the agent isn't logged in` | Run `claude` and log in |
| `Usage limit: ... would pause until ...` | Your Claude plan's limit; run again after the time it gives |
| `GitHub refused the token (401)` | The token expired, or can't see the repo |
| `agent.tool_unexpected`, `agent.mcp_unexpected`, `agent.plugin_unexpected` | Something from outside rtfd loads into Claude. Please report it, with the `validate` output |
| `agent.flag_missing` | Your Claude Code is too old for a flag rtfd needs: update it |
| Folders left in `runs/` or `worktrees/` after a crash | Safe to delete; serve's next start does it |
| `rtfd serve: already running` | Another serve uses this data folder; `rtfd status` shows it |
| `another rtfd is working on <repo> right now` | serve is reviewing one of its PRs; try again when it's done |
| `rtfd serve is not responding` | It holds the lock but stopped writing its heartbeat: stop it and start it again |
| `stopped: ... rtfd resume <repo>` | Fix what it says, then `rtfd resume <repo>` |
| `not watched until serve restarts` | A token is missing or refused: fix it and restart serve |
| `the Azure CLI ... gave no token` | Run `az login --tenant <tenant>`; serve picks it up on its next try, no restart |
| `Azure DevOps refused the token ... sign-in page` | The token is for another tenant: set `tenant` to your organization's |
| `Bitbucket refused the token in BB_TOKEN (401)` | The variable must hold `email:token`, with your Atlassian account's email; the token may have expired |
| `Bitbucket refused ... (403)` | The token lacks a scope (pull requests need read and write), or access to the repo |

---

## Why

The same watcher was built twice by hand, as a PowerShell loop and a scheduled task per repo: once
for Bitbucket with Copilot CLI, once for GitHub with Claude Code. Both work, and both hit the same
problems one at a time. An agent exiting cleanly while its post was refused. A usage limit burning
every retry in eleven minutes. A background window someone closed by hand. A PR whose text tried to
steer an agent that held a token with write access. rtfd solves them once.

## What it does

- **Watches PRs** on GitHub, Azure DevOps and Bitbucket, waits for the pushes to settle and for CI to finish, and
  starts one review per new head.
- **Runs your agent read-only** on a checkout of the PR, with the diff, the CI state and the open
  review threads handed to it as files.
- **Posts one review itself**, with inline comments in your team's convention (Should / Could /
  MINOR, short, no bot marker), then reads it back to confirm it landed.
- **Follows up.** After the author pushes fixes, it reviews only what changed, replies "Looks good
  now." on each fixed thread and resolves it, and leaves the rest alone.
- **Decides the verdict from policy**, not from the agent. Any open Should means not ready, so a
  review can never say "ready" next to two Shoulds.
- **Pauses on usage limits** until the stated reset time, for every repo on that agent account,
  without spending retries.
- **Answers "is it watching?"** with `rtfd status`, from one long-running process instead of a task
  per repo.

## How it keeps the agent harmless

A PR is untrusted input, and the agent reads it. So the agent gets as little as possible:

- **No provider token.** rtfd fetches everything and posts everything. The worst a malicious PR can
  get out of the agent is a bad review, never a bad write.
- **No shell.** Git history comes through `rtfd-git`, a tiny tool server with four read commands,
  checked arguments and fixed command lines.
- **Nothing from your setup.** No plugins, no MCP servers of yours, no built-in ones, and an
  environment built from an allowlist. `rtfd validate` prints exactly which tools each agent ends
  up with, as the CLI reports them, and fails on anything extra.
- **No configuration from the PR.** Agent settings and instruction files in the checkout are reset to
  the base branch before the agent starts, and a PR that changes them gets a finding of its own.
- **A check on the way out.** Every comment is scanned for secrets before posting. A credential the PR
  commits is redacted and flagged; anything else that looks like a secret blocks the post.

## The config

```yaml
agents:
  claude:
    account: claude-personal          # usage-limit pauses are per account
    command: claude -p --output-format json
    output: { from: stdout, extract: ".result" }

repos:
  - name: nutrx
    provider: github
    repo: owner/nutrx
    trust: trusted
    agent: claude
    prompt: prompts/review.md         # your tone and conventions; re-read on every run
    authors: [owner]                  # only these authors' PRs get reviewed
    verdicts: [comment]
    token_env: GH_TOKEN               # the name of the variable, never the token
```

[`examples/rtfd.yaml`](examples/rtfd.yaml) has every key with a comment, and the design behind
each one is in the spec.

## Commands

| Command | What it does | |
|---|---|---|
| `rtfd validate` | Checks the config, the tokens and the agents, and prints each agent's tools | Built |
| `rtfd run <repo> --pr 12` | One review now, posted once and read back; a follow-up when rtfd reviewed the PR before. `--dry-run` prints it instead | Built |
| `rtfd retry <repo> --pr 12` | Gives a PR its attempts back, or lifts a secret block | Built |
| `rtfd serve` | One long-running process for every configured repo | Built |
| `rtfd status` | Whether serve is running. Per repo: last poll, open PRs, last review, attempts left. Per agent account: paused until | Built |
| `rtfd pause` / `rtfd resume <repo>` | Stops and restarts watching one repo, without a restart | Built |
| `rtfd install-service` | Registers `serve` to start at logon, headless, with no battery stop and no time limit | Phase 6 |

## Roadmap

- **v0: GitHub + Claude Code.** Built dry-run first: `rtfd run --dry-run` end to end against real
  PRs, then `validate`, posting, follow-ups, `serve` and the service.
- **Azure DevOps**, ahead of v1: the same reviews on Azure DevOps Services, with tokens from the
  Azure CLI. Built.
- **Bitbucket**, ahead of v1: the same reviews on Bitbucket Cloud. Built.
- **v1: Copilot CLI.** Plus other reviewers' threads as context, and notifications through rtfc.
- **Later:** pre-review hooks (build, browser checks), an untrusted tier for public repos open to
  anyone, code suggestions, webhooks, GitLab.

Phases and exit criteria are in [`docs/plan.md`](docs/plan.md).

## Working on rtfd

```bash
dotnet build
dotnet test                          # unit and end-to-end tests; no network, no real model
dotnet format --verify-no-changes
```

[`CLAUDE.md`](CLAUDE.md) has the rules the code keeps (most of them are security guards) and how
the tests work.

## License

[MIT](LICENSE). The secret check bundles [gitleaks](https://github.com/gitleaks/gitleaks)' rule
set, also MIT; its notice is in `src/Rtfd/Resources/gitleaks-LICENSE.txt` and in the binary.
