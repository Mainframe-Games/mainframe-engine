# ADR 0201 — 2D joints on Box2D: PinJoint2D and DampedSpringJoint2D

- **Date:** 2026-10-20
- **Status:** accepted
- **Milestone:** G9 (2D slice)
- **Docs:** docs/design/physics-joints.md

## Context

Buy My Snake Oil's prep bench is 2D soft bodies (mainframe-engine#50). Each one is a lattice of small `RigidBody2D`s held
together by `PinJoint2D`s:
- 444 joints are baked into the scene, and more are created at runtime.
- The game sets joint paths after both bodies are in the tree.
- It breaks petal pins past a distance, and changes softness live while tuning.
- Physics runs at 120 ticks.

The game agreed to re-tune on Box2D (port decision D2) rather than reproduce Godot's softness maths. G9 designs every joint
type in both dimensions. This slice is what the bench needs.

## Decision

- **`Joint2D : Node2D`** carries:
  - `NodeA`/`NodeB` (an empty path is the static world);
  - `DisableCollision` (default true);
  - `ConstraintHertz`/`ConstraintDampingRatio` (Box2D's defaults, 60 Hz and 2);
  - `BreakForce`/`BreakTorque` (0 = never), and the `Broken` signal;
  - `BodyA`/`BodyB`, `IsActive`, `IsBroken`, `GetAppliedForce`/`Torque`, `GetSeparation` and `Rebuild()`.

  Two types for now:
  - `PinJoint2D` is a revolute joint, with Godot's angle-limit and motor names plus a finite `MotorMaxTorque`.
  - `DampedSpringJoint2D` is a distance joint with a spring and a maximum length. It keeps Godot's `Length`/`RestLength`
    and B anchor (`Length` along the joint's +Y), but its spring is `SpringHertz`/`SpringDampingRatio`.
- **Softness is Box2D's own knobs, not Godot's `Softness`/`Bias`** (Brogan, 2026-10-20). No conversion is exact, and
  the bench's tables are being re-measured anyway. A mapped formula would look like Godot's values while behaving
  differently.
- **The joint is created at the space's flush, after the bodies.** That makes path order and tree order irrelevant.
  Frames are captured from the joint's pose on creation and kept while the paths stay the same. Box2D destroys a
  body's joints with the body; the records go pending and are re-created when a body joins again. A shapeless static
  body per space stands in for "the world". Both paths empty is a silent inactive joint, because Snake Oil clears both
  to detach a joint while tuning.
- **Breaking uses Box2D's thresholds** and `b2World_GetJointEvents`, read after the step. The joint is destroyed at
  once and `Broken` is dispatched after the body signals.
- **Units are pixels:** forces kg·px/s², torques kg·px²/s².
- **The motor's maximum torque is finite** (default 100 N·m). In a test, an unbounded motor drove a pin straight
  through its soft angle limit.

## Consequences

- At 120 Hz with 4 substeps, a lattice of 121 bodies and 431 pins settles to under 0.1 px of separation. It recovers
  from a hard yank (2.5 px peak), and steps in about 0.27 ms in a Debug build. Steady state allocates nothing beyond
  Box2D's own step.
- Every Box2D call stays inside `Src/Physics/2D` (the shim rule for pinned physics versions).
- Still to come from G9: 3D joints, the other 2D types, `Enabled`, editor gizmos and handles, ragdolls.
