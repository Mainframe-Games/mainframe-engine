# Plan: G1.13 pose script and baker

**Status:** done, see [log](../logs/2026-11-07-g1c-pose-baker.md)
**Docs:** [Animation authoring proposal](../../docs/design/future/animation-authoring.md) (pose script, baking, linking
libraries to models, G1e root motion, task list G1.13), [Animation and skinning](../../docs/design/animation.md)
(humanoid map, `AnimationPlayer`, transform tracks and `Transitions`, root motion),
[ADR 0281](../decisions/0281-humanoid-map-and-canonical-angles.md), [ADR 0210](../decisions/0210-root-motion.md)
**Started:** 2026-11-07

## Goal

A pose script (JSON: a few key poses in canonical angles, IK pins, arcs, a root mode) bakes on a rigged humanoid into an
ordinary `Animation` with sparse cubic keys: one key per script key on every bone that moves, plus refinement keys only
where interpolation lets a pinned foot slide or an arc miss by more than 1 cm. The clip lands in a library `.mfres`
(created or updated, guarded by `Animation.BakedHash` against overwriting hand edits), the model's `.mfmeta` links the
library, and the importer gives every instance of the model those clips, so `Play("jump")` plays the baked clip.

## Not in scope

- Edit scripts and `describe` (G1.14); the checks table (G1.15; the baker only reports what it had to refine or clamp);
  `mfe` (G1.16); contact sheets (G1.17); editor reload (G1.18); the skill (G1.19).
- IK targets in a world space other than the character's (`"space": "world"` is rejected with a message).
- A root-motion bone above the hips (the hips carry the trajectory; `RootMotionBone` = the hips' rig name).

## Design notes (decided here)

- Code in `MainframeEngine/Src/Animation/Authoring/` (namespace `MainframeEngine`).
- **Parsing** with `JsonDocument` by hand, not a source-generated context: every error carries its JSON path
  (`keys[3].joints.LeftKnee: unknown bone (did you mean LeftLowerLeg?)`), all errors are collected, nothing is baked when
  there is one. AOT-safe either way.
- **Ease:** a key's `ease` shapes the segment after it (G1a's `Transitions` semantics): `linear` 1, `in` 2, `out` 0.5,
  `inOut` −2, or a number.
- **Root modes:** `inPlace` (default) keeps the keys' hips offsets and ignores arcs (a physics-driven jump; a note says
  so when a script has arcs); `motion` keeps horizontal offsets and adds the arcs, for `AnimationPlayer` root motion
  with `RootMotionBone` = the hips.
- **Arcs** add `4k·u(1−u)` to the keyed hips height between `from` and `to`; `k = g·T²/8` from gravity, or solved so the
  apex is `height` above take-off. A note when an explicit height is more than 25 % off gravity's.
- **IK** (`TwoBoneIk`, shared later with the editor): on the posed skeleton, the knee/elbow goes in the plane of hip,
  target and the pole (the upper bone's current bend direction: knees forward, elbows back, so hips yaw and authored
  twist turn it); the upper and lower bones turn by the shortest arc (the authored twist is kept); out-of-reach targets
  straighten the limb towards them. A pinned foot or hand keeps the global rotation it had when pinned unless the key
  gives its own angles. Pins hold the effector's skeleton-space position from the pin key to `release`.
- **Keys:** at every script key, every mapped bone whose rotation differs from its rest at some key gets a
  `Rotation3DTrack` (cubic, quaternions kept in one hemisphere), the hips a `Position3DTrack`; eases in `Transitions`.
  Then a 60 Hz pass samples the clip as played and inserts a key into the affected tracks (the IK chain, or the hips)
  at the worst sample where a pin slides or an arc misses by more than 1 cm, until none does or a track has a key per
  sample.
- **`Animation.BakedHash`** (new `[Export] string`): SHA-256 (first 16 hex digits) of the tracks the baker wrote. A
  re-bake over a clip whose current hash differs (hand-edited, or not baked) needs `replace`.
- **Linking:** `ModelImportSettings.Libraries` (`"libraries"`, project paths, written when set); the importer puts those
  libraries **before** the imported one in the model's `AnimationPlayer` (creating a player when the model has none), so
  a baked `jump` wins over an imported `jump` (both are in the default library); the bake result notes the shadowing.
  A change from the proposal, which appended them.

## Steps

- [x] `PoseScript` model + `PoseScriptParser` (keys, named poses, hips, joints, mirror, IK, ease, arcs, root, loop).
  Verified by: unit tests for a full script and for each error kind (path and suggestion in the message).
- [x] `TwoBoneIk`. Verified by: unit tests (reachable target hit within 1 mm, unreachable straightens, pole plane kept,
  zero length guarded).
- [x] `PoseBaker`: resolve keys, IK, arcs, root modes, tracks, hemisphere continuity, refinement. Verified by: unit tests
  on the test character — keys at script times only (until refinement), eases in `Transitions`, held values, mirror,
  named poses, unmoving bones without tracks, a pinned foot sampled at 60 Hz stays within 1 cm, refinement keys only on
  IK chain tracks, an arc's apex height (gravity and explicit), `inPlace` vs `motion`.
- [x] `BakedHash` + `AnimationHash`; library writing (create, replace, guard) and `.mfmeta` `libraries`. Verified by:
  unit tests in a temp project (library created, clip replaced, hand-edited clip refused without `replace`, link
  written once, `--no-link`).
- [x] Importer: linked libraries on the model's player. Verified by: a unit test importing the test character with a
  linked library, `Play("jump")` playing the baked clip through `AnimationPlayer`.
- [x] Docs (animation.md section, asset-pipeline `libraries`, proposal G1.13 built + changes, AGENTS.md), log with State
  changes. Verified by: `check.sh`, `just state-guard`.
- [x] Gates: `just build`, Release `-warnaserror`, `just test`, `just format-check`, `just shaders-check`, `check.sh`.
  `just test-render` only if rendering changes (it should not).

## Done when

- A jump script bakes on the test character into a library `.mfres` with keys only at the script's times plus
  refinement keys on the IK chains, and its pinned feet move less than 1 cm at 60 Hz while pinned.
- The importer links that library and `AnimationPlayer.Play("jump")` poses the skeleton as the baker's sampler does.
- Re-baking over a hand-edited clip fails without `replace` and succeeds with it.
- Every script error test names the JSON path.
- The gates pass; a PR is open (stacked on #209) with Auto-fix on.

## Open questions

- None for the owner.
