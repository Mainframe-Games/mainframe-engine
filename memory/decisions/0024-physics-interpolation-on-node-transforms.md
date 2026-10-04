# ADR 0024 — Physics interpolation on node transforms

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M6

## Context

Physics steps at 60 Hz; frames run at any rate. The proposal kept an internal "physics transform" and interpolated
for rendering, but the renderer reads `Node3D.ModelMatrix` of each visual, and visuals are usually *children* of the
body. A renderer-side interpolated matrix would have to be propagated to every descendant (a second transform
hierarchy).

## Decision

Interpolate the **node transform** (as Unity's Rigidbody interpolation does with `Transform`):

- New `IFixedStepServer` hooks: `BeforeFixedSteps()` (frames that step) and `AfterFixedSteps(fraction)` (every
  unpaused frame, before process), default no-ops; `SceneTree.Tick` calls them.
- Before the steps, moving bodies (dynamic, kinematic, character) are put back at their physics pose; after each step
  the pose is pulled into the node; after the steps the node gets `lerp/slerp(previous, current, fraction)`. So
  `OnPhysicsProcess` sees the physics pose and everything else (process, rendering, children) the render pose.
- Server writes are flagged so they don't count as user moves; user moves are detected through new `private protected`
  Node3D/Node2D hooks (`OnGlobalTransformInvalidated`, `OnLocalTransformChanged`) without per-step scans.
- Per body opt-out: `PhysicsInterpolation = false`. `Teleport` restarts interpolation.

## Consequences

- No renderer changes; visual children, debug draw and cameras following a body are smooth for free.
- Code reading a body's position in `OnProcess` gets the render pose (up to one step behind the simulation).
- Every moving, non-resting body costs one or two transform writes per frame (resting/sleeping bodies none).
