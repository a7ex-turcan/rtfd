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
