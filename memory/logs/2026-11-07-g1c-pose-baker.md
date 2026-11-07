---
summary: >-
  G1.13: pose scripts (PoseScriptParser, path-named errors) bake on a HumanoidRig into sparse cubic keys with 60 Hz
  refinement where pins slide or arcs miss (PoseBaker, TwoBoneIk, ClipSampler); Animation.BakedHash guards hand edits;
  PoseLibrary writes library .mfres files and links them in the model's .mfmeta, which the importer puts first (ADR 0282).
---

# 2026-11-07: G1c pose script and baker (G1.13)

Branch `feature/g1c-pose-baker`, stacked on `feature/g1c-humanoid-map` (#209). Plan:
[G1c pose baker](../plans/2026-11-07-g1c-pose-baker.md). As built:
[Animation and skinning → Pose scripts and baking](../../docs/design/animation.md#pose-scripts-and-baking-g1c).

## What changed

- **Engine** (`MainframeEngine/Src/Animation/Authoring/`): `PoseScript` (keys, partial hips and angles, IK commands,
  arcs, root modes), `PoseScriptParser` (`JsonDocument`, every error with its JSON path, "did you mean" bones),
  `TwoBoneIk` (`SolveMid`, `Apply` on a skeleton), `PoseBaker` (key resolution, mirror, arcs from gravity or height, pins,
  targets, track building, refinement), `ClipSampler`, `AnimationHash`, `PoseLibrary` (write, replace guard, link).
- **`Animation.BakedHash`** (`[Export] string`).
- **`HumanoidRig`:** `CurrentDirection` (IK poles) and `ToSkeletonSpace` (IK targets).
- **Import:** `ModelImportSettings.Libraries` (`"libraries"`); the importer loads linked libraries onto the model's
  `AnimationPlayer` before its imported clips, creating a player when the model has none.
- **Tests** (`Tests/MainframeEngine.Tests/Animation/Authoring/`, 48): parser (full script, 23 error cases), IK (reach,
  pole, straighten, zero length, on a skeleton), baker on the test character (key times, eases, untouched bones, held
  values and mirror, pinned feet at 60 Hz, inPlace notes, motion arcs from gravity and from a height, unreachable
  targets and pins, the skeleton's pose restored), libraries in a temp project (create and link once, replace guard,
  non-baker clips, `link: false`, the importer's player playing the baked clip as the sampler poses it).
- **Docs:** animation.md (Pose scripts and baking), asset-pipeline.md (linked libraries), the proposal (G1.13 built and
  its changes), AGENTS.md.

## Decisions

- [ADR 0282](../decisions/0282-pose-baker-keys-pins-and-linked-libraries.md).

## Dead ends

- The first test jump pinned the feet while the hips rose above standing height and landed with the feet ahead of a
  straight-legged stand: physically unreachable, so the feet slid 1.35 cm at a refinement key. The baker was right; the
  script was fixed, and the refinement pass now notes an unreachable pin instead of staying silent.

## Verified

- `just build`, Release `-warnaserror`, `just test`, `just format-check`, `just shaders-check`, `just state-guard`,
  `check.sh` (numbers in the PR).
- `just test-render` not run: nothing here draws; the importer change only adds libraries to an `AnimationPlayer`.

## Not done

- G1.14–G1.19. A real end-to-end jump on a game project waits for `mfe` (G1.16) and the contact sheet (G1.17).

## State changes

- milestones G1c: "G1.13–G1.19 ⬜" → "G1.13 pose script and baker ✅ (ADR 0282); G1.14–G1.19 ⬜" (after the G1.12 entry
  from 2026-11-07-g1c-humanoid-map).
