# ADR 0234 — The engine owns custom Jitter2 constraints (Generic6DofJoint3D)

- **Date:** 2026-11-03
- **Status:** accepted
- **Docs:** docs/design/physics-joints.md#custom-constraints, docs/design/future/physics-joints-g9-10.md

## Context

`Generic6DofJoint3D` (G9.10b) needs per-axis locks, limits, motors and springs on all six axes. Jitter2 2.9.0 has no
6-DOF constraint, and its primitives can't express per-axis limits on two axes at once (ADR 0211 left the joint out for
this reason). The owner chose a custom constraint over composing primitives (proposal question 1). Jitter2 supports
custom constraints. A `Constraint<T>` subclass has unmanaged data `T`, at most `Precision.ConstraintSizeFull` (256)
bytes including Jitter2's 32-byte header, and registers a pair of static function pointers with
`RegisterFullConstraint(prepare, iterate)`. The engine then owns solver code that runs on Jitter2's worker threads.

## Decision

- **Custom constraints live in `MainframeEngine/Src/Physics/3D/Constraints/`.** They are `internal` and `unsafe`.
  `Prepare` and `Iterate` are static, allocation-free, and read only their own data and the two bodies'
  `RigidBodyData`. Each data struct starts with `JitterConstraintHeader`, a copy of Jitter2 2.9.0's
  `ConstraintData` header layout.
- **Four constraints per 6-DOF joint, not the proposal's two.** Locks and limits, plus motors and springs, did not fit
  in 224 bytes of data per linear or angular part. So the joint uses four constraints:
  - `SixDofLinear` (linear locks and limits)
  - `SixDofAngular` (angular locks and limits)
  - `SixDofLinearDrive` (linear motors and springs)
  - `SixDofAngularDrive` (angular motors and springs)

  A drive with no motor or spring on returns at once, so it costs a call. The linear drive rebuilds its axes each
  iteration from body 1's orientation, because it has no room to keep them. Per-axis state is bit-packed (two bits per
  axis).
- **One row per axis, solved like Jitter2's own constraints.**
  - Locks and limits use Jitter2's default softness and bias (`Constraint.DefaultLinear*`/`DefaultAngular*`).
  - Linear rows run along frame A's axes, at B's anchor. When all three linear axes are locked, they run along world
    axes at each body's own anchor instead, as Jitter2's `BallSocket` does. With frame-axis rows, the benchmark's
    100-link chain flailing at more than 1,000 rad/s went NaN within 4 s. With world-axis rows it stays as bounded as
    Jitter2's pin chain (`ALong6DofChainFlailingFor30SecondsStaysFinite`).
  - Angular rows are the relative rotation's XYZ Euler angles (`Rx·Ry·Rz`, as Bullet's `matrixToEulerXYZ`), so Y is
    limited to ±90°. Each angle's row axis is the dual basis of A's X, B's Z and their cross product, so its rate is
    exactly `axis·(ωB − ωA)`. A test checks this numerically.
  - Bullet's own axis formulas were tried first. They match this decomposition only near zero, so they were not used.
- **Limits are speculative** (Box2D's). Jitter2 calls `Prepare` every substep, but passes the whole step's inverse dt,
  and with one substep a limit inactive at the step's start let a fast body pass it by a whole step's motion. So a
  limited axis is always solved against its nearer limit. Inside the range, its bias lets the body close the gap but not
  cross it.
- **Springs and motors are rows in the solver.** A spring is a soft row (`SoftRow`: Catto's coefficients, as Box2D's
  `b2MakeSoft`, with the impulse scale `1 − massScale`). Its coefficients come from the `h` Jitter2 passes, which is
  the step dt. A motor is a velocity row capped at its force over that dt. The hinge, slider and distance joints keep
  their pre-step PD springs (ADR 0211; proposal open question 2).
- **Breaking** sums the linear rows and linear drive impulses as force, and the angular rows and angular springs as
  torque. Angular motors don't count, as ADR 0211 says.
- **Re-check on every Jitter2 upgrade.** `SixDofConstraintTests.EveryCustomConstraintsDataFitsJitter2sConstraintData`
  checks the header size and each struct's size. The 6-DOF scenario tests and
  `A6DofSceneOnWorkerThreadsStepsTheSameEveryRun` check behaviour and threading. Jitter2 stays pinned exactly
  (`Directory.Packages.props`).

## Ruled out

- **Composing Jitter2 primitives:** it can't express two-axis linear limits or per-axis angular limits.
- **One constraint per part with everything recomputed in `Iterate`:** it would fit, but the Euler decomposition
  (three `atan2`/`asin`) would then run every solver iteration.
- **Coupled 3×3 blocks for locked axes, as `BallSocket` and `FixedAngle` use:** they need more storage, and 1-D rows
  hold the scenarios. A 1 kg box on a lever its own size stays within 1 cm and 1°.

## Consequences

- The engine can add other joints and drives the same way. G9.10c's active-ragdoll drives reuse `SoftRow` and the row
  helpers in `SixDofMath`.
- A Jitter2 minor version can change the `ConstraintData` header, the dispatch API or the `Prepare` timing. The size
  test and scenario tests catch layout and behaviour changes, but the code has to be re-read against Jitter2's
  `HingeAngle`/`World.Step` source on any upgrade.
- 6-DOF springs are solver soft rows while the hinge, slider and distance springs are PD springs. They behave alike,
  but are tuned and documented separately.

## Revisit when

- Jitter2 ships its own 6-DOF constraint, or its constraint data grows.
- A game needs stiffer locked axes than 1-D rows give (then try coupled blocks for fully locked parts).
