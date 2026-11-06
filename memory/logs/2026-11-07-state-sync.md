---
summary: >-
  Feature PRs stop editing docs/milestones.md and current-state.md (their edits made every open PR conflict); status
  changes go in each log's State changes section and a serial state sync applies them (ADR 0280).
---

# 2026-11-07: Status through logs and a state sync

Branch `feature/state-file-conflicts`.

## What changed

- `memory/context/current-state.md` rewritten to slow-changing facts, one per line: no "last updated" line, feature
  inventory, test counts or per-feature next steps (those are in `docs/milestones.md` rows and logs).
- `docs/milestones.md`: the owner-pending and remaining-work notes that only lived in the state file moved into their
  rows (G8a–G8d, G12, G1c).
- `.agents/workflow.md#state-sync`: feature PRs write a `## State changes` section in their log instead of editing those
  two files; a `state-sync` branch applies them. `just state-pending` lists what is pending, `just state-guard` (a gate
  and a CI step in the `workflow` job) fails a branch that edits the files outside a sync, except a change to link
  targets only (a moved doc's links; the G19 PR moving `protected-exports.md` showed the need). AGENTS.md's Project
  memory points at `just state-pending`.

## Decisions

- [ADR 0280](../decisions/0280-status-changes-through-logs-and-a-state-sync.md). Numbered 0280, not 0274, because busy
  lanes were taking numbers in the 027x range at the time.

## Dead ends

- `merge=union` for the state file: GitHub's PR conflict check ignores repository merge drivers.
- One fact per line alone: milestones rows still conflict (six open PRs edited adjacent rows 316–323).

## Verified

- `just state-guard` on this branch (exempt: it changes the guard), and on a scratch branch editing
  `docs/milestones.md` (fails with the restore hint), and on one that only repoints a link in it (passes);
  `just state-pending` before and after a scratch log.
- `bash .agents/mainframe-workflow/scripts/check.sh`.

## Not verified

- The CI step runs only on a PR; its first run is this PR's.

## State changes

None beyond this branch's own edits to the two files (it changes the rule, so the guard exempts it); this PR counts as
the first sync point for `just state-pending`.

## Next

- The six open PRs move their edits of the two files into their logs (their sessions were asked to).
- The first `State: sync` PR once they merge.
