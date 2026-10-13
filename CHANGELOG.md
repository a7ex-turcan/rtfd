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
