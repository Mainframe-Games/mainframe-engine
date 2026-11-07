# Plan: G1.12 humanoid map

**Status:** done, see [log](../logs/2026-11-07-g1c-humanoid-map.md)
**Docs:** [Animation authoring proposal](../../docs/design/future/animation-authoring.md) (canonical humanoid space,
humanoid map, testing, task list G1.12, open questions 1 and 2), [Animation and skinning](../../docs/design/animation.md)
(`Skeleton3D`, bone rests), [Asset pipeline](../../docs/design/asset-pipeline.md) (`.mfmeta` model settings),
[ADR 0207](../decisions/0207-skeleton3d-animationplayer-compute-pre-skinning.md)
**Started:** 2026-11-07

## Goal

Any humanoid `Skeleton3D` can be described in the canonical humanoid space: `HumanoidMap.Detect` maps the rig's bones
to `HumanoidBone`s (names first, then structure, `.mfmeta` overrides winning) with a report, and `HumanoidRig` turns
anatomical joint angles into the rig's local bone rotations and back, for T-pose and A-pose rests and rotated bone
frames alike. The owner's G1c decisions (build as proposed, pose scripts not kept, `mfe` starts with `anim` only, the
existing CC0 test character) are recorded. This is the base the baker (G1.13), `describe` (G1.14) and checks (G1.15)
stand on; nothing plays differently.

## Not in scope

- The pose script, baker, IK, checks, `mfe`, editor reload (G1.13–G1.18).
- `mfe anim rig --write-meta` (G1.16 writes the map; this step reads overrides).
- Per-character limits (proposal: later, a `.mfmeta` setting).
- A downloaded CC0 character (owner: the generated one, `Tests/Assets/Models/Character`).
- `.mfmeta` `libraries` (G1.13, with the importer linking them).

## Design notes (decided here)

- Code in `MainframeEngine/Src/Animation/Humanoid/`, namespace `MainframeEngine` like the rest of `Src/Animation`.
- **Canonical space** as the proposal: Y up, +Z forward, +X the character's left, T-pose reference. The rig's character
  frame `C` (canonical → skeleton space) comes from its rest: up from hips to head, left from the right to the left
  upper legs (or arms), forward = left × up.
- **Rest correction.** In the canonical T-pose every mapped bone's global rotation is identity. Per bone the rig's
  T-pose global rotation is `G_T = C·Q_fix·C⁻¹·G_rest`, where `Q_fix` is the shortest arc taking the bone's rest direction
  (to its mapped child, else its rig child, else its parent's direction) to the canonical one. That is how an A-pose arm
  takes the same angles as a T-pose arm. A canonical pose `P` (global, canonical space) gives the rig global
  `G = C·P·C⁻¹·G_T`; unmapped rig bones keep their rest local rotation; locals are `G(parent)⁻¹·G`.
- **Joint frames.** Each bone has a twist, abduct and flex axis in canonical space (left and centre bones written out,
  right bones mirrored: an axis `(x, y, z)` becomes `(x, −y, −z)`), so the same numbers mean the same motion on both
  sides. Local rotation = `R(twist)·R(abduct)·R(flex)` (intrinsic twist, then abduct, then flex, as the proposal says);
  `FromBoneRotation` inverts it with a Tait–Bryan decomposition in the joint basis. Hinges (`LowerLeg`, `LowerArm`) take
  flex only. Hips: offset `x/y/z` (metres, canonical, from the rest position) and `yaw/pitch/roll`.
- **Named poses** are a code table (`HumanoidPoses`: `tpose`, `stand`; `rest` computed from the rig) instead of
  `Assets/Animation/poses.json`: `mfe anim poses` prints them, so the skill and the editor still see one list, and no
  content read is needed. A change from the proposal, recorded in the ADR.
- **Detection.** Names: strip known prefixes (`mixamorig:`, `mixamorig1:`, `DEF-`, `ORG-`, `J_Bip_?_`, `Bip01`), fold
  case and separators, split off a side token, then tables for VRM, Mixamo, Unreal, Blender/Rigify and generic names;
  spine chains are numbered by depth (`Spine`, `Chest`, `UpperChest`). Structure (for bones names miss): the hips are the
  bone with two downward chains and one upward chain; spine to the arm branch, neck and head above; arm and leg chains
  by their joints. Each mapped bone records its source (name, structure, override) and a confidence; the report lists
  unmapped rig bones, missing required bones and side conflicts.
- **Overrides:** `ModelImportSettings.Humanoid` (`"humanoid": { "LeftUpperLeg": "Thigh.L" }`), read in `FromMeta`,
  written in `ToMetaSettings` only when non-empty, compared by value (the template cache compares settings).

## Steps

- [x] ADR (`mf-decide`): the owner's G1c answers and the decisions above. Verified by: the file exists and
  `check.sh` passes.
- [x] `HumanoidBone` (VRM 1.0 set), required/optional/hinge/side/parent tables, `JointAngles`, `HipsPose`,
  `HumanoidLimits`, `HumanoidPoses`. Verified by: unit tests (every bone has a canonical parent and direction; mirrored
  pairs; limits cover every component).
- [x] `HumanoidMap.Detect` + `HumanoidMapReport`: name tables and structure. Verified by: unit tests on synthetic
  skeletons named Mixamo (`mixamorig:`), Unreal, Blender/Rigify, VRM/VRoid, generic, and unnamed (structure only), plus
  the test character; overrides win; unmapped and missing bones reported.
- [x] `HumanoidRig`: character frame, rest correction, joint frames, `ToBoneRotation`/`FromBoneRotation`, hips,
  `RestKind`, `Proportions`, `ApplyPose` onto a `Skeleton3D`. Verified by: unit tests — angle round trips for every
  bone on T-pose, A-pose and rotated-bone-frame rigs; zero angles give the T-pose; mirrored angles give mirrored world
  positions; anatomical directions (hip flex moves the foot forward, knee flex moves it back, shoulder abduct lifts the
  hand, spine flex moves the head forward); the test character's `RestKind` is T and its proportions match its bones.
- [x] `.mfmeta` `humanoid` overrides in `ModelImportSettings`. Verified by: unit tests (read, write, equality, invalid
  entries warn).
- [x] Docs: the proposal's G1.12 marked built with what changed; `asset-pipeline.md` gains `humanoid`; AGENTS.md project
  structure; log with State changes. Verified by: `check.sh`, `just state-guard`.
- [x] Gates: `just build`, Release `-warnaserror`, `just test`, `just format-check`, `just shaders-check` (no shader
  change), `check.sh`. `just test-render` only if something renders differently (nothing should).

## Done when

- `HumanoidMap.Detect` maps all required bones of the test character and of each synthetic naming family, and the
  structure-only skeleton, with a report.
- For every mapped bone on T-, A- and rotated-frame rigs, `FromBoneRotation(ToBoneRotation(a))` returns `a` within
  0.01°, inside `HumanoidLimits`.
- The anatomical direction tests pass on all three rigs.
- A `.mfmeta` `humanoid` override changes the map, and round-trips through `ToMetaSettings`.
- The gates above pass; a PR is open with Auto-fix on.

## Open questions

- None for the owner. The decomposition's singularity is abduct ±90° (the middle axis, e.g. an arm straight up); there
  `FromBoneRotation` puts the whole rotation about the twist/flex pair into flex (twist = 0), noted in the docs.
