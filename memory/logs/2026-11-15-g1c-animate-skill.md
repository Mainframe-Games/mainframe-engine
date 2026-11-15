---
summary: >-
  G1.19: the `animate` Claude Code skill (engine repo and mfgame template) teaches the G1c loop with mfe; an end-to-end
  run on the test character baked a jump that passes every check and edited it; target-then-pin IK added for landings.
  G1c is complete.
---

# 2026-11-15: G1c the `animate` skill (G1.19)

Branch `feature/g1c-animate-skill`, stacked on `feature/g1c-editor-reload` (#216). Plan:
[G1c animate skill](../plans/2026-11-15-g1c-animate-skill.md). As built:
[Animation and skinning → The `mfe` CLI](../../docs/design/animation.md#the-mfe-cli).

## What changed

- **`.claude/skills/animate/SKILL.md`** and the same file in `Templates/MainframeEngine.Templates/content/mfgame/.claude/`:
  finding `mfe` (repo, checkout game, package game), the new-clip and change-a-clip workflows, the pose and edit script
  references, the checks table with fixes, motion notes, and the end-to-end run's jump as the example.
- **Target-then-pin IK:** `{ "pos": [x, y, z], "pin": true }` reaches a character-space target and holds it like a pin,
  the foot or hand put down as at rest unless the key gives its angles (`IkCommand.Pin`); edits reject it.
- **Tests:** parser and edit-parser cases, `PoseBakerTests.ATargetWithPinLandsThereLevelAndHolds`.
- **Docs:** animation.md (the skill; the `ik` field), the proposal (G1.19 built and its changes; status), AGENTS.md.

## End-to-end run

In a scratch project with the test character (`character.glb` as `Assets/Characters/hero.glb`), following the skill:

1. `mfe anim rig`: 31 bones, T-pose, 1.80 m, nothing missing.
2. Round 1 (an in-place jump: eased crouch with arms back, take-off, tuck, landing pinned, absorb, stand): 0 errors,
   1 warning: the end was 17° from `stand` because the landing pinned the feet 2.2 cm behind and 1 cm above where they
   started (`describe` showed both). A key cannot target and pin the same foot, so target-then-pin was added.
3. Round 2 (land on the `t = 0` foot positions with `"pin": true`): 0 errors, 0 warnings; the contact sheet reads as a
   jump (crouch, arms forward on take-off, tuck at the apex, landing on the starting spots, absorb, stand) and the strip's
   contact bands match.
4. An edit script (tuck +15° hips and knees at the apex, arms back −15° in the crouch, mirrored): only those keys changed
   (181 keys before and after), the clip stayed an untouched bake, 0 errors and 0 warnings; `describe` read the new angles.

## Decisions

- None needing an ADR.

## Dead ends

- None beyond round 1 above.

## Verified

- `just build`, Release `-warnaserror`, `just test`, the contact sheet render tests, `just format-check`,
  `just state-guard`, `check.sh`, `just template-smoke` (numbers in the PR).
- `just test-render` in full not rerun: nothing here draws (the baker change is covered by the unit tests and the contact
  sheet render tests, which bake).

## Not done

- `mfe` in the MainframeEngine package (`tools/mfe/`, with what `render` needs): package games run it from an engine
  checkout for now.
- G1d (posing tools in the editor) and G1f (non-humanoid rigs, auto-rigging).

## State changes

- milestones G1c: the status cell (after the entries from 2026-11-07-g1c-humanoid-map, 2026-11-07-g1c-pose-baker and the
  2026-11-15 G1.14–G1.18 logs) → "✅ G1.12–G1.19 (ADRs 0281, 0282): humanoid map, pose scripts and baker, describe and
  edit scripts, checks, `mfe anim` (rig, poses, bake, describe, check, list, render), contact sheets, editor reload and
  instanced preview, the `animate` Claude Code skill; `mfe` in the NuGet package not yet".
- state: Next steps 2: drop "G1c animation authoring (G1.13 baker next)" (from 2026-11-07-g1c-humanoid-map): G1c is done.
