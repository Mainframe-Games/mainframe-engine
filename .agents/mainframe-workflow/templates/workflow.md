---
decisions: docs/decisions
decision_naming: numbered
proposals: docs/proposals
plans: docs/plans
logs: docs/logs
state: docs/state.md
state_max_lines: 100
---

# Project workflow

This project's settings for the [workflow](mainframe-workflow/workflow/README.md). They win over the workflow pages.

In the block above, paths are relative to the repo root and a blank value means unused. `decision_naming` is `numbered`, `dated` or `slug`.

## Gates

Commands that must pass before shipping.

- `TODO: build command`
- `TODO: test command`

## Shipping

`move`

Use `move` when finished proposals move to a current-state doc. Use `status` when they stay where they are and gain a status and a dated build log.

## Commits

TODO: commit message style, and whether commits happen as work lands or at ship time.

## Git

TODO: branch naming, how PRs merge, and when to push.

## Human checks

TODO: what only a person can check in this project.
