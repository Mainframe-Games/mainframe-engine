---
summary: >-
  Piloted OpenSpec, dropped it, and adopted the shared Mainframe workflow v1.0.0 in the engine.
---

# 2026-11-02: Mainframe workflow in the engine

Branch `feature/mainframe-workflow`. Decision: [ADR 0233](../decisions/0233-mainframe-workflow-not-openspec.md).

## What changed

- Synced workflow v1.0.0 into `.agents/` and `.claude/skills/`, and filled in `.agents/workflow.md`.
- Added the `workflow` CI job under `ci-success`, and made the workflow's paths docs-only in `build/ci-changes.sh`.
- Added `## Scenarios` to the G12 proposal and fixed its three defects.

## Decisions

See the ADR. Plans and logs move to `memory/plans/` and `memory/logs/`, matching the games.

## Dead ends

- OpenSpec: setup and propose worked, but validate found nothing the scenarios had not. Implementation and archive were not
  tried.

## Next

- Adopt the workflow in the three games.
- Try a second agent, since only Claude Code is tested.
