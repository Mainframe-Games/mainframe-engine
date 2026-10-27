# ADR 0211 — 3D joints on Jitter2, the rest of the 2D joints, breaking and editor gizmos

- **Date:** 2026-10-27
- **Status:** accepted
- **Milestone:** G9 (G9.1–G9.5)
- **Docs:** docs/design/physics-joints.md

## Context

Bodies could not be connected in 3D: no doors, chains, bridges, breakable props or ragdolls. Jitter2 2.9.0 has
primitive constraints (`BallSocket`, `HingeAngle`, `ConeLimit`, `TwistAngle`, `FixedAngle`, `PointOnLine`,
`DistanceLimit`, `AngularMotor`, `LinearMotor`) but no breaking, no "don't collide connected bodies", no angular or
prismatic spring, and it drops every constraint between two bodies that are not dynamic. ADR 0201 shipped two 2D joints
(pin, damped spring) for Buy My Snake Oil; the G9 proposal covers the rest. Its open questions needed answers.

## Decision

- **Godot's node model in both dimensions.** `Joint3D : Node3D` (`NodeA`/`NodeB`, empty = the world via Jitter2's
  `NullBody`; `ExcludeNodesFromCollision`, `Enabled`, `BreakForce`/`BreakTorque`, `Broken`, `Rebuild()`,
  `GetAppliedForce/Torque`, `GetSeparation`, `DrawDebug`). Types: `PinJoint3D` (ball socket), `HingeJoint3D` (Z axis:
  socket, hinge angle, motor), `SliderJoint3D` (X axis: point-on-line, fixed angle or — `AllowTwist` — a hinge angle,
  linear motor), `ConeTwistJoint3D` (X axis: socket, cone limit, twist angle; 180° = that limit off), `DistanceJoint3D`
  (distance limit; rope, rod or spring) and `WeldJoint3D` (socket, fixed angle). 2D adds `SliderJoint2D` (prismatic),
  `GrooveJoint2D` (wheel joint without spring, axis = the joint's +Y), `WeldJoint2D` and `WheelJoint2D` to ADR 0201's
  types without changing their API, plus `Joint2D.Enabled` and `PinJoint2D`'s revolute spring.
- **Primitives, not Jitter2's composite `Joint`s.** Each constraint is created from the record's stored frames and its
  data written directly (`LocalAnchor`, `Axis`, `Q0` = rB·conj(rA)), so re-creation joins the bodies at the authored
  pose and measures limits from it wherever the bodies are now. Frames are captured as soon as both bodies exist —
  even when the joint has to wait for a dynamic body — so a ragdoll's limits are those of the pose it was authored in.
- **Kinematic pairs wait.** A joint with no dynamic body is not created (`IsActive` false); a body's kind change marks
  its joints dirty and retries waiting ones. Removing a body destroys its constraints first, then they wait for it.
- **Collision exclusion** is per body record: a copy-on-write `Excluded` array the broad-phase filter (Jitter2 worker
  threads) reads; turning it on clears contact caches. Exclusion holds while the joint exists and is enabled.
- **Open question 1 (spring parameters): frequency + damping ratio**, as proposed, in both dimensions.
- **Open question 2 (3D angular/linear springs): an engine PD spring before the step**, as proposed, made implicit (the
  velocity the spring reaches after one step, turned into an impulse), so it is stable and mass-independent; the
  effective inertia is about the joint's anchor, not the centre of mass. Frequency is clamped to a quarter of the tick
  rate. `DistanceJoint3D`'s spring uses the same path instead of Jitter2's `SpringConstraint`, which ignores body
  rotation (it is a soft-body particle constraint), so off-centre anchors would be wrong. Deviation from the proposal.
- **Open question 3 (`Generic6DofJoint3D`): left out** (proposal default; G9.10).
- **Open question 4 (solver tuning): library defaults** for 3D (no Softness/Bias exports); 2D keeps ADR 0201's live
  `ConstraintHertz`/`ConstraintDampingRatio`.
- **Open question 5 (break measurement): the last substep's impulses** (Σ|linear| / substep dt for force, Σ|angular| for
  torque; motors excluded on the angular side as proposed), like Box2D's instantaneous check. A breaking 3D joint is
  destroyed (not disabled), matching 2D; `Broken` fires after the body signals, in joint creation order.
- **Open question 6 (moving a joint at run time): ignored**, as Godot; `Rebuild()` re-captures.
- **Debug draw and editor.** Every type draws its shape (amber, red when stretched, grey when pending, disabled or
  broken — so edit-mode joints are visible); `PhysicsServer3D/2D.DebugDrawJoints` draws joints without shapes. The
  editor draws the selected joint with `DrawDebug` in the selection colour plus a line to each named body, warns on bad
  paths, and adds Rebuild and Swap A/B (one undo) inspector buttons. Joint icons reuse the existing atlas (`point`,
  `door`, `arrows-right-left`, `ripple`, `ruler-2`, `lock`, `arrows-up-down`, `circle`) rather than vendoring new Tabler
  SVGs (no network fetch); the proposal's `cone`/`steering-wheel`/`accessible` names were not added.

## Consequences

- Steady-state frames with 320 3D joints (chains, motors, springs, cone-twists, breakable welds, debug draw) allocate
  nothing; a 100-link pinned chain steps in about 57 µs (Release, one thread).
- Jitter2's limits are soft with library defaults: a door swinging into a hinge limit overshoots about 1.5° before
  resting on it.
- Contacts between folding chains come and go chaotically and make Jitter2 allocate for new pairs, so the joint
  allocation gate's parts collide only with the floor.
- Still to come: G9.7 limit handles (after G7.3), G9.10 (`Generic6DofJoint3D`, active ragdolls, `MotorJoint2D`); the
  `physics-joints` render test has a moltenvk golden only (lavapipe to record in CI).
