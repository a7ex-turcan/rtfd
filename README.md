# rtfd: Read The Fine Diff

A pull request review watcher for any AI agent. rtfd watches your PRs, hands each change to the
agent you configure (Claude Code, Copilot CLI, anything that can print JSON), and posts the review
back to the PR. When there's nothing to say, it says "lgtfm".

Sibling of [rtfm](https://github.com/a7ex-turcan/rtfm) (docs),
[rtfq](https://github.com/a7ex-turcan/rtfq) (databases) and
[rtfc](https://github.com/a7ex-turcan/rtfc) (relay between sessions).

**Status: design settled, nothing built yet.** You can't run it today. The design is
[`docs/spec.md`](docs/spec.md) and the build order is [`docs/plan.md`](docs/plan.md).

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
- **Nothing from your setup.** No plugins, no MCP servers of yours, no built-in ones. `rtfd validate`
  prints exactly which tools each agent ends up with.
- **No configuration from the PR.** Agent settings and instruction files in the checkout are reset to
  the base branch before the agent starts, and a PR that changes them gets a finding of its own.
- **A check on the way out.** Every comment is scanned for secrets before posting. A credential the PR
  commits is redacted and flagged; anything else that looks like a secret blocks the post.

## A taste of the config

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

The full draft is in the spec.

## Commands (planned)

| Command | What it does |
|---|---|
| `rtfd serve` | One long-running process for every configured repo |
| `rtfd install-service` | Registers `serve` to start at logon, headless, with no battery stop and no time limit |
| `rtfd status` | Per repo: alive, last poll, open PRs, last review, attempts left. Per agent account: paused until |
| `rtfd run <repo> --pr 12 [--dry-run]` | One review now. `--dry-run` prints what it would post |
| `rtfd retry <repo> --pr 12` | Gives a PR its attempts back |
| `rtfd pause` / `rtfd resume <repo>` | Stops and restarts watching one repo |
| `rtfd validate` | Checks the config, the tokens and the agents, and prints each agent's tools |

## Roadmap

- **v0: GitHub + Claude Code.** Built dry-run first: `rtfd run --dry-run` end to end against real
  PRs, then `validate`, posting, follow-ups, `serve` and the service.
- **v1: Bitbucket + Copilot CLI.** Plus rebase handling, other reviewers' threads as context, and
  notifications through rtfc.
- **Later:** pre-review hooks (build, browser checks), an untrusted tier for public repos open to
  anyone, code suggestions, webhooks, GitLab and Azure DevOps.

Phases and exit criteria are in [`docs/plan.md`](docs/plan.md).

## License

[MIT](LICENSE)
