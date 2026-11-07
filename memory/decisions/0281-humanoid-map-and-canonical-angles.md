# ADR 0281 — G1c animation authoring: the owner's answers, and the humanoid map's canonical angles

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** Gameplay toolkit G1c (part: G1.12)
- **Docs:** docs/design/future/animation-authoring.md, memory/plans/2026-11-07-g1c-humanoid-map.md

## Context

G1c lets Claude Code make clips on rigged humanoids: a humanoid map per model, pose scripts in anatomical angles baked
into ordinary keys, checks, the `mfe` CLI, editor reload and an `animate` skill. The proposal had waited for the owner's
review, with open questions on keeping pose scripts and on what `mfe` covers. The first step, G1.12, also had to fix
the conventions every later step relies on: how anatomical angles become a rig's bone rotations.

## Decision

The owner's answers (2026-11-07):

- **Build G1c as proposed**, G1.12–G1.19, one PR per step. Contact sheets need a window and a display, as render tests
  do; a window-less render path (open question 6) stays open.
- **Pose scripts are not kept** (open question 1): they are input only; the baked keys are the truth, and
  `Animation.BakedHash` tells whether a clip was hand-edited.
- **`mfe` starts with the `anim` group only** (open question 2): `mf-l10n` and `mf-shaders` stay separate tools.
- **Tests use the generated CC0 character** (`Tests/Assets/Models/Character`); other naming families are covered by
  synthetic skeletons. No downloaded model.

The conventions G1.12 sets:

- **Canonical space:** Y up, +Z forward, +X the character's left; the canonical T-pose is every mapped bone's global
  rotation at identity. A rig's character frame comes from its rest (up: hips to head; left: right to left upper legs or
  arms; forward = left × up), so rigs facing any way map the same.
- **Rest correction per bone:** the shortest arc from the bone's rest direction to its canonical direction, applied in
  global space, so an A-pose rig takes the same angles as a T-pose rig.
- **Joint frames:** each bone has a twist, abduct and flex axis; right-side axes are the left ones mirrored
  (`(x, y, z)` → `(x, −y, −z)`), so equal numbers are equal anatomical motion on both sides. A local rotation is
  intrinsic twist, then abduct, then flex (`R(twist)·R(abduct)·R(flex)`); hinges take flex only. The inverse is a
  Tait–Bryan decomposition; at its singularity (abduct ±90°) twist is 0.
- **Named poses are a code table** (`HumanoidPoses`: `tpose`, `stand`; `rest` computed per rig) rather than the
  proposal's `Assets/Animation/poses.json`. `mfe anim poses` prints the table, so the skill and the editor still share one
  list, and the engine needs no content read for it.
- **Detection runs on demand** (`mfe`, the editor), never at import; `.mfmeta` `humanoid` overrides win over names and
  structure.

## Ruled out

- **Retargeting through rest-relative local rotations** (copying local deltas bone by bone): angles would mean different
  motions on rigs whose bone axes differ, which is the problem G1c exists to solve.
- **A JSON poses file in engine content:** a second source to keep in sync with the code that validates it, for three
  entries.
- **Extrinsic (fixed-axis) composition:** the proposal fixed the intrinsic order; changing it would make angles read
  differently from the documented convention.

## Consequences

- Every later G1c step (baker, describe, checks, editor sliders) speaks one set of angles that works across Mixamo,
  Unreal, Blender/Rigify, VRM and unnamed rigs.
- The structural detector is heuristic: unusual rigs may need `.mfmeta` overrides, which the report points at.
- Named poses change only with a code change (and a test), not by editing content.

## Revisit when

- A rig family maps badly often enough that a new name table or detector rule is cheaper than overrides.
- Stylised characters need per-character limits (a `.mfmeta` setting the proposal leaves for later).
- Open question 6 (window-less render) becomes needed for CI or remote sessions.
