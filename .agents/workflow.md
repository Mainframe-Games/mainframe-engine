---
decisions: memory/decisions
decision_naming: numbered
proposals: docs/design/future
plans: memory/plans
logs: memory/logs
state: memory/context/current-state.md
state_max_lines: 100
---

# Project workflow

This project's settings for the [workflow](mainframe-workflow/workflow/README.md). They win over the workflow pages.

In the block above, paths are relative to the repo root and a blank value means unused. `decision_naming` is `numbered`, `dated` or `slug`.

## Gates

Commands that must pass before shipping. Rendering changes need a GPU and a display; on macOS keep the display awake.

- `just build`
- `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`
- `just test`
- `just test-render`
- `just format-check`
- `just shaders-check`
- `bash .agents/mainframe-workflow/scripts/check.sh`

## Shipping

`move`

When a proposal in `docs/design/future/` ships, move its content into the current-state doc in `docs/design/`, add a "Changes from the proposal" section, repoint links, and tick its row in `docs/milestones.md`.

Update the `docs/design/` doc for any subsystem a change touches, and `AGENTS.md` when the project structure changes. `docs-site/` is generated from `docs/`.

`memory/context/progress-log.md` is history. New entries go in `memory/logs/`.

## Commits

Commit and push only when the owner asks. Subjects are plain: `Area: outcome`, and the PR number lands with the merge.

## Git

- `main` is PR-only and `ci-success` must pass. Squash or rebase, never merge commits, no force pushes.
- Branches are `feature/<slug>` or `fix/<slug>`.
- Releases come from `publish.yml`. Never bump versions in files.
- GitHub Actions minutes are scarce: run things locally first, using Docker for Linux.

## Human checks

- Inspect the PNGs of any golden changed by `just golden-update` before committing.
- Rumble, gamepads, audio feel and visual quality on real hardware.
- Benchmarks count only on the machine that recorded `baseline.json`, when it is quiet.
