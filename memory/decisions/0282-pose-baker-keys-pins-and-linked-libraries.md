# ADR 0282 — Pose baker: sparse cubic keys with refinement, minimal-turn IK, and linked libraries before imported clips

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** Gameplay toolkit G1c (part: G1.13)
- **Docs:** docs/design/animation.md#pose-scripts-and-baking-g1c, docs/design/future/animation-authoring.md,
  memory/plans/2026-11-07-g1c-pose-baker.md

## Context

G1.13 turns pose scripts (key poses in the canonical angles of [ADR 0281](0281-humanoid-map-and-canonical-angles.md))
into ordinary keys a person can then edit. The proposal fixed the outline (sparse keys, refinement where a pin slides,
`BakedHash`, linked libraries) but left details that decide what a baked clip looks like and how it reaches the game.

## Decision

- **Keys:** cubic, at the script's times only, on every mapped bone that leaves its rest and on every IK chain bone; the
  hips get a position track. A 60 Hz pass over the clip as played adds a key to the IK chain (or the hips, for arcs) at
  the worst sample until no pin slides and no arc misses by more than 1 cm. A pin out of reach is noted, not chased (a
  key already sits there, so the pass moves on).
- **Ease** belongs to the segment after its key, as G1a's `Transitions` do, so a baked clip reads the same in the
  Animation panel.
- **IK** turns the upper bone and the hinge each by the shortest arc onto the solved joint positions, with the pole from
  the upper bone's current pose (knees forward, elbows back). That keeps the authored twist without solving for it, and
  the same `TwoBoneIk` serves the editor's handles later. Pins also hold the effector's rotation (a foot stays level).
- **Root modes:** `inPlace` keeps the keys' hips offsets and ignores arcs (a note says so): the game's body does the
  jumping. `motion` adds the arcs and keeps horizontal motion for root motion on the hips.
- **`BakedHash`** (SHA-256 of the transform tracks, 16 hex digits) and the replace guard ship now: `PoseLibrary.Write`
  replaces a clip only when it is an untouched bake, or with `replace`.
- **Linked libraries come before the imported clips** on the model's `AnimationPlayer`. Imported clips live in the
  default library too, and the player takes the first match, so appending (as the proposal had it) would have let the
  test character's imported `jump` hide a baked `jump`. The write notes the shadowing.
- **Parsing** is by hand over `JsonDocument` so every error names its JSON path and all errors come back at once.

## Ruled out

- **Dense baking (a key per frame):** unreadable and uneditable in the panel; the point of G1c is clips people can fix.
- **Solving IK in joint angles:** needs a solver per joint type and clamps; the shortest-arc swing on the posed skeleton
  is exact for two-bone chains and keeps twist for free.
- **Prefixing baked libraries (`baked/jump`):** the proposal wanted `Play("jump")` to play the baked clip.
- **A source-generated JSON context for scripts:** its errors name types, not the script's paths.

## Consequences

- Baked clips have a handful of keys per track plus a few refinement keys on pinned limbs, all ordinary keys.
- A model with linked libraries always gets an `AnimationPlayer`, even without clips of its own.
- Scripts that pin a foot while the hips rise above standing height report the slide instead of failing.

## Revisit when

- Rigs with a root-motion bone above the hips need the trajectory there instead of on the hips.
- World-space IK targets (a hand on a table in the scene) are needed: today targets are character space.
