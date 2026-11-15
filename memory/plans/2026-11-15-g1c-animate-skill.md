# Plan: G1.19 the `animate` skill and an end-to-end run

**Status:** done, see [log](../logs/2026-11-15-g1c-animate-skill.md)
**Docs:** [Animation authoring proposal](../../docs/design/future/animation-authoring.md) (Claude Code skill, task list
G1.19), [Animation and skinning → The `mfe` CLI](../../docs/design/animation.md#the-mfe-cli)
**Started:** 2026-11-15

## Goal

Claude Code knows the G1c loop from one skill, in the engine repository and in every new game project: where `mfe` is,
rig → script → bake → checks → contact sheet → fix, edits on top of existing keys, the script and edit references, what
each check means, and motion notes; its example clip is one an end-to-end run on the test character proved passes every
check.

## Not in scope

- Shipping `mfe` in the MainframeEngine package (the skill names the checkout fallback; a follow-up).

## Steps

- [x] Draft the skill. Verified by: reading it against the CLI's help and the docs.
- [x] End-to-end run on the test character in a scratch project: rig, bake a jump, fix what the checks and the sheet show,
  look at the sheet, edit it, describe, list. Verified by: 0 errors and 0 warnings; the sheet looked at.
- [x] What the run needs: target-then-pin IK (`{ "pos", "pin": true }`). Verified by: parser, baker and edit-parser tests.
- [x] The skill in `.claude/skills/animate/` and the `mfgame` template; docs; AGENTS.md. Verified by: `just template-smoke`
  (the generated game has the skill), `check.sh`.
- [x] Gates: `just build`, Release `-warnaserror`, `just test`, the contact sheet render tests, `just format-check`,
  `check.sh`, `just state-guard`.

## Done when

- The skill's jump bakes with 0 errors and 0 warnings on the test character, and its sheet shows a jump.
- A project from `dotnet new mfgame` contains `.claude/skills/animate/SKILL.md`.
- The gates pass; a PR (stacked on #216) is open with Auto-fix on.

## Open questions

- None for the owner.
