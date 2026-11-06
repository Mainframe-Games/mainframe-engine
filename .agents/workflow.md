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
- `just state-guard`

## Shipping

`move`

When a proposal in `docs/design/future/` ships, move its content into the current-state doc in `docs/design/`, add a "Changes from the proposal" section, repoint links, and put the tick of its `docs/milestones.md` row in the log's State changes ([State sync](#state-sync)).

Update the `docs/design/` doc for any subsystem a change touches, and `AGENTS.md` when the project structure changes. `docs-site/` is generated from `docs/`.

`memory/context/progress-log.md` is history. New entries go in `memory/logs/`.

## State sync

`docs/milestones.md` and `memory/context/current-state.md` are shared status files. Feature PRs ran in parallel and each
edited the same rows and lines there, so every merge left the others in conflict (ADR 0280). Now only a state sync
edits them:

- **A feature PR never edits either file** (`build/state-guard.sh` fails it in CI). Ship steps 3 and 5 (tick the
  milestone, correct the state file) and the `move` step's milestone tick become a `## State changes` section in the
  PR's log: each change to make, said exactly, e.g. "milestones G8a: 🚧 → ✅ (G8a.8 foliage done)", "state: add gotcha
  …", "milestones: add row G21 …". Write "None." when there are none. A log is a new file, so it never conflicts.
  One exception: a PR that moves or renames a doc repoints the links to it in the two files, changing nothing else on
  those lines (the guard allows a change to link targets only), so no link is dead until the sync.
- **Reading state:** `current-state.md` and `docs/milestones.md`, then `just state-pending`: the State changes of every
  log merged since the last sync. Those are true even though the two files do not show them yet.
- **A state sync** runs from an up-to-date `main` when the owner asks, and before a release (the docs site publishes
  `docs/milestones.md`). Its branch name contains `state-sync` (`feature/state-sync-YYYY-MM-DD`) and its PR title
  starts `State: sync` (the next `just state-pending` counts from that commit). It applies every pending section to the
  two files, by judgment where two logs touch the same row, and nothing else. Only one sync is open at a time.
- **The state file** holds only what is true and slow-changing: no dates, PR numbers, "in review" notes, test counts or
  feature lists. One fact per line, hard-wrapped at 120 characters. Per-feature status lives in milestones rows.

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
