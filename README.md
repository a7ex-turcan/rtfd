# rtfd: Read The Fine Diff

A pull request review watcher for any AI agent. rtfd watches your PRs, hands each change to the
agent you configure (Claude Code, Copilot CLI, anything that can print JSON), and posts the review
back to the PR. When there's nothing to say, it says "lgtfm".

Sibling of [rtfm](https://github.com/a7ex-turcan/rtfm) (docs),
[rtfq](https://github.com/a7ex-turcan/rtfq) (databases) and
[rtfc](https://github.com/a7ex-turcan/rtfc) (relay between sessions).

**Status: early. It reviews, but doesn't post yet.** `rtfd run <repo> --pr <n> --dry-run` runs
a real review of a GitHub PR with Claude Code and prints exactly what it would post; `rtfd validate`
checks a config. Posting arrives in Phase 3 of the plan. The design is
[`docs/spec.md`](docs/spec.md) and the build order is [`docs/plan.md`](docs/plan.md).

---

## Getting started

This gets you from a clone to a review of one of your PRs, printed in the terminal. Nothing is
posted to GitHub.

### What you need

- **The .NET 10 SDK** (10.0.300 or later), to build rtfd. There are no release binaries yet.
- **git** 2.25 or later on `PATH`.
- **Claude Code**, installed and logged in: run `claude` once and log in. rtfd starts Claude with a
  short allowlist of environment variables (`PATH`, your profile folders, the temp folder), so a
  login is what works. An `ANTHROPIC_API_KEY` set only in your environment doesn't reach it.
- **A GitHub token that can read your repo.** The easiest is the GitHub CLI's: `gh auth token`.

Tested on Windows 11 with Claude Code 2.1.296 and git 2.47.

### 1. Build it

```bash
git clone https://github.com/a7ex-turcan/rtfd.git
cd rtfd
dotnet publish src/Rtfd -c Release -o "$HOME/.rtfd/bin"
```

That puts `rtfd` (`rtfd.exe` on Windows) in `~/.rtfd/bin`. Add the folder to your `PATH`, or call
rtfd by its full path. To update later, pull and run the same `dotnet publish` again.

### 2. Set up your home

rtfd reads its config from `~/.rtfd/rtfd.yaml` (`%USERPROFILE%\.rtfd\rtfd.yaml` on Windows). Set
`RTFD_HOME` to use another folder, or pass `--config <path>` to any command. Start from the sample:

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

Every mistake comes with its line number (`error [config.unknown_key] line 14, repos[0].promt:
...`), and the command exits non-zero until there are none. A warning that `GH_TOKEN is not set`
means the variable isn't in this shell.

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
| `--dry-run` | Required for now: print the review instead of posting it |
| `--verbose` | Also print the agent's raw answer, with secrets redacted |
| `--inputs-only` | Stop before the agent: just the PR, the diff and the files rtfd would hand over |
| `--keep` | Leave the run folder and the worktree on disk, and print where they are |
| `--config <path>` | Use another config file |

The command exits 0 when a review came out, and 1 when it didn't; the last line says why.

### 5. Make the reviews yours

`prompts/review.md` is the reviewer's brief: what to look at, how severe is severe, and how to
write. rtfd re-reads it on every run, so edit it and run the same PR again until the review reads
like one you'd post. rtfd adds its own part after it: where the inputs are, and the format of the
answer. Each printout names the prompt's hash, so you can tell which version produced which tone.

### Where things go

Everything rtfd writes lives under its data folder: the home, unless `data_dir` in the config
points elsewhere.

| Folder | What |
|---|---|
| `mirrors/` | A bare git mirror per repo, fetched into on every run |
| `runs/`, `worktrees/` | One run's working folder and checkout. Deleted when the run ends, unless `--keep` |
| `git/` | An empty git config and hooks folder, so your own git setup never applies |

### When something goes wrong

| You see | What to do |
|---|---|
| `GH_TOKEN isn't set` | Set the variable in this shell (step 3) |
| `... isn't in myapp's authors` | The PR's author isn't in `authors` |
| `the PR comes from a fork` | rtfd skips forks |
| `the agent isn't logged in` | Run `claude` and log in |
| `Usage limit: ... would pause until ...` | Your Claude plan's limit; run again after the time it gives |
| `GitHub refused the token (401)` | The token expired, or can't see the repo |
| Folders left in `runs/` or `worktrees/` after a crash | Safe to delete |

---

## Why

The same watcher was built twice by hand, as a PowerShell loop and a scheduled task per repo: once
for Bitbucket with Copilot CLI, once for GitHub with Claude Code. Both work, and both hit the same
problems one at a time. An agent exiting cleanly while its post was refused. A usage limit burning
every retry in eleven minutes. A background window someone closed by hand. A PR whose text tried to
steer an agent that held a token with write access. rtfd solves them once.

## What it does

- **Watches PRs** on GitHub (Bitbucket next), waits for the pushes to settle and for CI to finish, and
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
  environment built from an allowlist. `rtfd validate` will print exactly which tools each agent
  ends up with.
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
| `rtfd validate` | Checks the config, the tokens and the agents, and prints each agent's tools | Config checks now; tokens and agents in Phase 2 |
| `rtfd run <repo> --pr 12 --dry-run` | One review now, printed instead of posted | Built |
| `rtfd run <repo> --pr 12` | One review now, posted | Phase 3 |
| `rtfd retry <repo> --pr 12` | Gives a PR its attempts back | Phase 3 |
| `rtfd serve` | One long-running process for every configured repo | Phase 5 |
| `rtfd status` | Per repo: alive, last poll, open PRs, last review, attempts left. Per agent account: paused until | Phase 5 |
| `rtfd pause` / `rtfd resume <repo>` | Stops and restarts watching one repo | Phase 5 |
| `rtfd install-service` | Registers `serve` to start at logon, headless, with no battery stop and no time limit | Phase 6 |

## Roadmap

- **v0: GitHub + Claude Code.** Built dry-run first: `rtfd run --dry-run` end to end against real
  PRs, then `validate`, posting, follow-ups, `serve` and the service.
- **v1: Bitbucket + Copilot CLI.** Plus rebase handling, other reviewers' threads as context, and
  notifications through rtfc.
- **Later:** pre-review hooks (build, browser checks), an untrusted tier for public repos open to
  anyone, code suggestions, webhooks, GitLab and Azure DevOps.

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
