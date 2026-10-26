# ADR 0210 — Root motion: AnimationPlayer extraction and CharacterBody3D.ApplyRootMotion

- **Date:** 2026-10-27
- **Status:** accepted
- **Milestone:** G1e (task G1.20), resolves keyframe animation open question 3
- **Docs:** docs/design/animation.md#root-motion, docs/design/physics.md, docs/design/future/animation-authoring.md

## Context

Clips that travel (a walk that covers ground, a leap, a turn on the spot) slide or snap back when a character's body
moves on its own: the feet skate unless the body moves exactly as the clip's hips do. Godot extracts the motion of one
track (`root_motion_track`) and leaves it to the game to apply; Unity bakes it into the root and moves the transform.
The engine has `AnimationPlayer` (ADR 0207) and a kinematic `CharacterBody3D` whose `MoveAndSlide` reads `Velocity`.
Rigs differ: Unreal-style rigs have a `root` bone above the hips, Mixamo/Blender ones make the hips the top bone.

## Decision

- **Godot's model on the player.** `RootMotionTrack` (a `NodePath` to the `Skeleton3D`, relative to `RootNode`),
  `RootMotionBone` (empty: the skeleton's first root bone) and `RootMotionVertical` (default false). The bone's pose is
  read relative to its parent (a top bone: skeleton space).
- **What is root motion.** The bone's horizontal position and its yaw (the twist about the parent's +Y, the same for
  either swing-twist order) relative to its rest; vertical position too with `RootMotionVertical`. The pose keeps
  everything else: the hips stay at their rest horizontally and face their rest yaw, a crouch or a jump arc still moves
  them up and down.
- **Per update, in character space.** `GetRootMotionPosition()`/`GetRootMotionRotation()` are the motion of the last
  update (each update replaces them; nothing accumulates across updates, so it is read once per update). The movement
  is in the skeleton's space, turned from the clip's facing into the character's facing at the start of the update, so a
  body that moves by it and then turns by the rotation retraces the clip's path (a turning walk closes its circle).
- **Computed from clip time, per stretch.** The player already splits an update into stretches without a wrap (for
  events); each stretch adds `value(to) − value(from)`. A loop wrap therefore adds the clip's whole displacement; a
  stretch covering the whole clip uses the clip's whole turn (the sum of key-to-key turns, so a 360° clip loops by
  360°, not 0). Ping-pong and backwards play subtract naturally. Nothing depends on the previous frame's sample, so
  `Play`, `Seek` and switching clips never produce a jump.
- **Crossfades** lerp the two clips' motion by the blend weight (a pose captured by a `Play` during a crossfade does not
  move). A clip without root tracks contributes zero, so blending into it fades the motion out.
- **`CharacterBody3D.ApplyRootMotion(player)`** turns the motion by the skeleton's global basis (scale included, e.g. an
  FBX armature's 0.01) into world space, sets `Velocity = motion / PhysicsDeltaTime` (only the horizontal part without
  `RootMotionVertical`, so gravity the game adds still applies) and turns the body by the yaw about the skeleton's up
  axis now. The game calls it before `MoveAndSlide`. The player should use the `Physics` callback mode (one motion per
  step); the editor warns otherwise, and when `RootMotionTrack` is not a skeleton or names no bone.
- **The test character** gains `jump` (a 1.2 m forward leap) and `circle` (a looping walk turning a quarter circle per
  loop) with the trajectory in the hips.

## Consequences

- No new state per frame beyond two floats per playback; resolution happens when a setting changes (or `Play` retries a
  failed lookup), track lookup when a clip binds. Steady root motion with crossfades and `MoveAndSlide` allocates
  nothing (`RootMotionCharacterTests`); results depend only on clip time and the fixed step, so they are deterministic
  and run under `HeadlessHost`.
- A body that processes before its player (the usual parent-child order) moves by the previous step's motion: one step
  of latency, no motion lost.
- Per-update turns must stay under half a circle (the stretch's yaw difference is wrapped); whole-clip stretches use the
  clip's total turn.
- Hips-as-root rigs lose the hips' horizontal sway to the body (as in Godot); a rig with a `root` bone keeps it.
- Not done: the G1c pose baker's `root: "motion"` mode (the baker does not exist yet), root motion from node (non-bone)
  tracks, and accumulators (Godot's `get_root_motion_position_accumulator`).
