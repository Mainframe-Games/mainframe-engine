# ADR 0233 — The Mainframe workflow, not OpenSpec

- **Date:** 2026-11-02
- **Status:** accepted
- **Docs:** .agents/workflow.md, docs/design/build-and-platforms.md, https://github.com/Mainframe-Games/workflow

## Context

The engine already ran a spec-driven process by hand: proposals in `docs/design/future/`, ADRs, current-state docs and
gates. The engine and three games each grew their own version of it, and the goal was one standard for all of them that
any coding agent can follow. OpenSpec 1.16.0 does much the same job with a CLI, so it was piloted on G12.1 and G12.2.

The pilot covered setup and the propose step only. Implementation and archive were not tried, so OpenSpec's delta merge
at archive was never measured. Writing 32 scenarios found three defects in the G12 proposal: `in RumbleOptions options =
default` skips the struct's initializers, so a default call is silent; `RumbleTarget.LastUsed` needs a later phase; and
one task was never defined. `openspec validate` found none of them. It checks structure, and the value came from
writing scenarios.

## Decision

- **Use our own workflow, in its own repo.** `Mainframe-Games/workflow` is public, because its files are copied into this
  public repo, and holds nothing private to a game.
- **Plain Markdown is the workflow.** Skills are thin wrappers, so any agent or person can follow it.
- **Take scenarios from OpenSpec.** Proposals gain a `## Scenarios` section whose entries name their tests.
- **Pin a copy, not a submodule.** Fresh worktrees and cloud agents do not see submodule contents. `.agents/mainframe-workflow/.lock`
  makes hand edits fail the check.
- **Check only added and changed files,** so older ADRs and proposals never need migrating.
- **The check always runs in CI,** as the `workflow` job under `ci-success`, because docs-only pull requests add these
  documents. Those paths count as documentation in `build/ci-changes.sh`.
- **New plans and logs go in `memory/plans/` and `memory/logs/`,** the same place the games keep theirs.
  `memory/context/progress-log.md` stays as history.

## Consequences

- There is no mechanical merge from a proposal to its current-state doc. The ship step does it by hand. If several people
  start reviewing the same behaviour, or parallel work keeps colliding on one spec, reconsider delta specs.
- The workflow is ours to maintain, and a skill is only advice: `check.sh` is the only part every agent obeys.
- Updating the workflow means running `sync.sh` from a newer tag and merging the diff in each project.
- Only Claude Code has been tested. Other agents need a trial before they count as supported.
- `CLAUDE.md` still imports `progress-log.md`, 952 lines loaded each session. Moving to `memory/logs/` makes it possible to
  drop that import later.
