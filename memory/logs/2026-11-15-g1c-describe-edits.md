---
summary: >-
  G1.14: clips read back in canonical terms (ClipDescriber: hips, joint angles, effector positions and contact, eases,
  partial keys) and edit scripts change them on top of their keys (PoseEditor: absolute/relative/scaled values at a key,
  over a range or at an insert, mirror, ease, arc rescaling, IK pins re-solving the planted span); BakedHash keeps hand
  edits marked; PoseLibrary.Read/WriteEdit.
---

# 2026-11-15: G1c describe and edit scripts (G1.14)

Branch `feature/g1c-describe-edits`. Plan: [G1c describe and edits](../plans/2026-11-15-g1c-describe-edits.md). As built:
[Animation and skinning → Describe and edit scripts](../../docs/design/animation.md#describe-and-edit-scripts-g1c).

## What changed

- **Engine** (`MainframeEngine/Src/Animation/Authoring/`): `ClipDescriber` + `ClipDescription` (key times, canonical
  pose per time, effectors in character space with height and contact, eases, partial keys, `ToText`), `PoseEditScript`
  (edit values, angles, hips, edits), `PoseScriptParser.ParseEdit`/`IsEditScript` (the parser is now partial; the ease
  reader is shared), `PoseEditor` (targets, solving on the pre-edit clip, writing keys and new tracks, eases, pin spans
  and refinement, arc scaling, `BakedHash` rules), `TrackKeys` (set or insert a key; the baker's refinement uses it),
  `PoseLibrary.Read` and `WriteEdit`.
- **`HumanoidRig.ToCharacterSpace`** (the inverse of `ToSkeletonSpace`); `PoseBaker.Chain` is internal for the editor.
- **Tests** (`Tests/MainframeEngine.Tests/Animation/Authoring/`, 45 new, 93 in the folder): `ClipDescriberTests`,
  `PoseEditScriptParserTests`, `PoseEditorTests`, three more `PoseLibraryTests`.
- **Docs:** animation.md (Describe and edit scripts, Testing), the proposal (G1.14 built and its changes), AGENTS.md.

## Decisions

- None needing an ADR: the choices are the plan's design notes and the proposal's "Changes" entry for G1.14.

## Dead ends

- Pins re-solved only between an edited key and its neighbours left a refinement key from the bake (inside the planted
  span, untouched by the edit) holding the old leg pose: the foot slid 1.8 cm once the hips curve changed around it.
  The pin span now runs across every adjacent key time where the foot was planted, and the chain's keys inside it are
  re-solved too.
- The shell's first `dotnet` (`/usr/local/share/dotnet`) had only SDK 11, not the pinned 10.0.403, so builds failed to
  start; Homebrew's `dotnet` has 10.0.403. A machine setup issue, not the repo's.

## Verified

- `just build`, Release `-warnaserror`, `just test`, `just format-check`, `just shaders-check`, `just state-guard`,
  `check.sh` (numbers in the PR).
- `just test-render` not run: nothing here draws.

## Not done

- G1.15–G1.19: checks, `mfe` (with `--json` for `describe`), contact sheets, editor reload, the `animate` skill.

## State changes

- milestones G1c: "G1.13 pose script and baker ✅ (ADR 0282); G1.14–G1.19 ⬜" → "G1.13 pose script and baker ✅ (ADR
  0282); G1.14 describe and edit scripts ✅; G1.15–G1.19 ⬜" (after the G1.12 and G1.13 entries).
