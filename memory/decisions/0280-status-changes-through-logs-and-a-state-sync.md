# ADR 0280 — Status changes go through logs; only a state sync edits the shared status files

- **Date:** 2026-11-07
- **Status:** accepted
- **Docs:** .agents/workflow.md#state-sync, memory/context/current-state.md, docs/milestones.md

## Context

The workflow's ship step has every PR tick its milestone and correct `memory/context/current-state.md`. With several
agent lanes shipping in parallel, every PR edited the same lines: the state file's "last updated" line, its one-line
feature inventory and its next-steps list, and adjacent rows of `docs/milestones.md` (G6 and G8a–G8e sit on
neighbouring lines, and git treats edits to adjacent lines as a conflict). Each merge to `main` left every other open
PR in conflict in one or both files; on 2026-11-07 all six open PRs (#186, #187, #189, #190, #192, #194) edited
milestones rows 316–323. Restructuring the state file to one fact per line (the first attempt on this branch) helped the
state file but could not help milestones rows, which several PRs legitimately change at once.

## Decision

- Feature PRs never edit `docs/milestones.md` or `memory/context/current-state.md`. The status change a PR would have
  made goes, said exactly, into a `## State changes` section of its log, which is a new file and never conflicts.
- `just state-pending` (`build/state-pending.sh`) prints the State changes of every log added since the last state
  sync: the newest commit whose subject starts `State: sync`, or, before the first, the commit adding the script.
  Agents read it together with the two files.
- A state sync is a docs-only PR from an up-to-date `main` on a branch whose name contains `state-sync`, run when the
  owner asks and before a release. It applies the pending sections and nothing else; only one is open at a time, so it
  cannot conflict.
- `build/state-guard.sh` (`just state-guard`, a gate, and a step of CI's `workflow` job) fails a branch that edits
  either file unless its name contains `state-sync` or it changes the guard itself.
- The state file holds only slow-changing facts, one per line, with no dates, PR numbers, test counts or feature
  inventory; per-feature status lives in milestones rows.

This overrides the pinned workflow's ship steps 3 and 5 for this project through `.agents/workflow.md`; the workflow
repo may want the same rule.

## Ruled out

- **`merge=union` in `.gitattributes`.** GitHub's mergeability check ignores repository merge drivers, so the banner
  stays; locally it silently keeps both versions of a changed line.
- **One fact per line only.** Fixes most state-file conflicts but not milestones rows, where two PRs on one feature, or
  on neighbouring features, must change the same or adjacent lines.
- **Generated status** (milestones built from fragment files). Mechanical merging of free-text status cells loses
  information when two logs touch one row, and `docs/milestones.md` must stay readable on GitHub.
- **A custom merge driver.** Needs per-clone setup and still does nothing for GitHub's banner.

## Consequences

- No feature PR conflicts in these files again; the conflicts that remain are real code conflicts.
- `docs/milestones.md` and the state file lag behind `main` until the next sync; `just state-pending` covers the gap
  for agents, and a sync before each release keeps the docs site right.
- Someone has to ask for a sync now and then, and the sync applies free-text instructions by judgment.

## Revisit when

- Pending sections pile up for long because syncs are not run, or a sync misapplies one.
- The workflow repo adopts its own rule for parallel lanes.
