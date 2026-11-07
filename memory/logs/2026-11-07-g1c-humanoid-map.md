---
summary: >-
  G1.12, the first step of G1c animation authoring: HumanoidBone (VRM set), JointAngles/HipsPose/HumanoidPose,
  HumanoidMap.Detect (names, structure, .mfmeta humanoid overrides) and HumanoidRig (canonical angles <-> rig rotations
  on T/A-pose, rotated-frame, any-facing and centimetre rigs); the owner accepted G1c as proposed (ADR 0281).
---

# 2026-11-07: G1c humanoid map (G1.12)

Branch `feature/g1c-humanoid-map`, cut from `origin/main`. Plan: [G1c humanoid map](../plans/2026-11-07-g1c-humanoid-map.md).
As built: [Animation and skinning → Humanoid map](../../docs/design/animation.md#humanoid-map-g1c).

## What changed

- **Owner decisions** (2026-11-07, [ADR 0281](../decisions/0281-humanoid-map-and-canonical-angles.md)): build G1c as
  proposed (G1.12–G1.19, one PR each); pose scripts are not kept; `mfe` starts with `anim` only; tests use the generated
  CC0 character.
- **Engine** (`MainframeEngine/Src/Animation/Humanoid/`): `HumanoidBone` + `HumanoidBones` (parents, sides, mirrors,
  directions, joint axes, required and hinge bones), `JointAngles`, `HipsPose`, `HumanoidPose`, `HumanoidLimits`,
  `HumanoidPoses` (`tpose`, `stand`, `rest`), `HumanoidMap` + `HumanoidMapReport`, `HumanoidDetector` (name tables and
  structure), `HumanoidRig` (character frame, limb rest correction, `ToBoneRotation`/`FromBoneRotation`,
  `ApplyPose`/`ReadPose`/`RestPose`, `RestKind`, `Proportions`, `MetresPerUnit`).
- **`.mfmeta`:** `ModelImportSettings.Humanoid` (`"humanoid"`, written only when set, compared by value);
  `AssetJsonContext` gains `Dictionary<string, string>`.
- **Tests** (`Tests/MainframeEngine.Tests/Animation/Humanoid/`, 54): `SyntheticHumanoid` builds Mixamo, Unreal (twist and
  IK bones), Blender `DEF-`, VRoid and unnamed rigs in T or A pose, identity or bone-along-Y frames, any facing, any unit;
  detection per family and by structure, the test character, overrides; angle round trips on six rig variants; one pose
  gives the same body on every rig; read-back; mirroring; anatomical directions; hips offsets in metres; rest kinds and
  rest poses; proportions.
- **Docs:** animation.md (Humanoid map), asset-pipeline.md (humanoid overrides), the proposal (status, G1.12 built with
  its changes, open questions 1 and 2 answered), AGENTS.md.

## Decisions

- [ADR 0281](../decisions/0281-humanoid-map-and-canonical-angles.md): the owner's answers and the canonical conventions
  (axes, limb-only rest correction, mirrored joint axes, intrinsic twist → abduct → flex, named poses in code).

## Dead ends

- None kept. A mutation check (the hip's flex axis flipped) failed the direction test, so the tests can fail.

## Verified

- `just build`, Release `-warnaserror`, `just test` (engine 3651, editor 1784, video 464 passed), `just format-check`,
  `just shaders-check`, `just state-guard`, `check.sh`.
- `just test-render` not run: nothing here draws or runs per frame.
- Bugs the tests found and fixed: `ModelImportSettings.Default` was built before its empty `Humanoid` dictionary (null);
  "did you mean" suggested `Jaw` for `Tail`.

## Not done

- G1.13–G1.19 (baker, describe and edits, checks, `mfe`, contact sheets, editor reload, the skill).
- No real Mixamo, Unreal or AI-generator file is tested (not redistributable); synthetic skeletons stand in.

## State changes

- milestones G1c: ⬜ → "🚧 G1.12 humanoid map ✅ (ADR 0281, linked from `docs/` as
  `../memory/decisions/0281-humanoid-map-and-canonical-angles.md`: `HumanoidMap`, `HumanoidRig`, canonical angles; the
  owner accepted the proposal 2026-11-07); G1.13–G1.19 ⬜".
- state: Next steps 2 gains "G1c animation authoring (G1.13 baker next)" as the toolkit row in progress.
