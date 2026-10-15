# Changelog

All notable changes to rtfd are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and rtfd follows
[semantic versioning](https://semver.org/): patch for fixes, minor for additive features,
major for a breaking change to the CLI, the config file or the findings contract.

**Before 1.0, treat minor versions as potentially breaking.**

## [Unreleased]

Nothing released yet. The design is in `docs/spec.md` and the build order in `docs/plan.md`.

### Added

- `rtfd validate [--config <path>]` checks a config: every mistake with its line number, what
  this build doesn't support yet (hooks, the untrusted tier, Bitbucket, Copilot, context tools),
  and the guards a config can't loosen (an agent's isolation and tools, flags rtfd sets itself,
  a token pasted where its variable's name belongs, which is refused without being shown).
- The config lives in `~/.rtfd/rtfd.yaml`, or under `RTFD_HOME`. `examples/` is a home to copy.
- `rtfd --version`. The other commands say which phase of the plan brings them.
- `rtfd run <repo> --pr <n> --dry-run` reviews one GitHub PR with Claude Code and prints what it
  would post: the inputs, each attempt, the review with its inline comments, redactions, and the
  verdict with why. `--verbose`, `--keep` and `--inputs-only` show more. Nothing is posted yet.
- The agent only reads: no shell, no provider token, an environment built from an allowlist,
  nothing from your Claude Code setup (plugins, MCP servers, skills, CLAUDE.md), and git history
  only through rtfd's own read-only `rtfd-git` tools with checked arguments.
- Agent configuration in the PR (`.claude/`, `.mcp.json`, `CLAUDE.md`, `AGENTS.md`) is reset to the
  base branch before the agent starts. A PR that changes it gets a Should (settings that can run
  code) or a note (instructions).
- Every comment is checked for secrets with gitleaks' rules and the tokens rtfd holds. A credential
  the PR commits is redacted and gets a Should; anything else blocks the review.
- rtfd decides the verdict from the findings and writes the review's first line itself ("Not
  ready: 1 Should open." or "Ready to merge from my side.").
- A usage limit is recognised, with its reset time and zone, and doesn't count as a failed attempt.
- `rtfd validate` now checks each repo with GitHub (the token works, the repo is reachable, who
  rtfd posts as, and `authors: ["*"]` refused on a public repo) and each agent: it starts Claude
  exactly as a review would, prints everything Claude reports it loaded, and fails on any tool,
  MCP server, plugin or skill beyond rtfd's own. It stops Claude before any model call.
  `--config-only` skips both.
- `rtfd run` without `--dry-run` posts the review to the PR, on the reviewed commit, and reads it
  back to confirm the summary, the event and every inline comment arrived as sent. It posts once
  per head: after a crash the next run finds the review by the hash of its text instead of posting
  it twice, or posts what it had recorded if it never landed. State lives in `state.db` in the data
  folder.
- Each head gets `max_attempts` agent runs. `rtfd retry <repo> --pr <n>` gives them back, and lifts
  the block on a head whose review held a secret that wasn't in the diff.
- Follow-ups. On a PR rtfd reviewed before, `rtfd run` reviews only what the author changed since
  the last review (the interdiff, without changes merged in from the base branch, even after a
  rebase), replies "Looks good now." on its own threads that are fixed and resolves them, and leaves
  the rest alone. The verdict counts rtfd's earlier threads that are still open. A push that only
  merges the base branch isn't reviewed.
- A Should resolved by hand counts as closed and is reported once in the next summary;
  `human_resolve_closes: false` keeps it counting until rtfd resolves it.
- A lost `state.db` is rebuilt from the hidden markers in rtfd's reviews on GitHub, so a lost store
  neither loses rtfd's threads nor posts a second review of a head.
- The hidden marker in each review now names the head's merge base too.
