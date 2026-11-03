# Plan: Generic6DofJoint3D (G9.10b)

**Status:** done, see [log](../logs/2026-11-03-generic-6dof-joint.md)
**Docs:**
- [Proposal: G9.10](../../docs/design/future/physics-joints-g9-10.md), accepted 2026-11-03: its "Custom Jitter2
  constraints" and "`Generic6DofJoint3D`" sections, the 6-DOF scenarios, and task list item G9.10b.
- [Physics joints and ragdolls](../../docs/design/physics-joints.md): the node model, "In the space", springs, breaking,
  debug draw and limit handles.
- [ADR 0211](../decisions/0211-3d-joints-on-jitter2.md) and [ADR 0213](../decisions/0213-viewport-tools-gizmo-resources-handles.md).
- [Physics → Libraries](../../docs/design/physics.md#libraries).
- Jitter2 2.9.0's `HingeAngle.cs` and `Constraint.cs`, the model for a custom constraint.

**Started:** 2026-11-03

## Goal

`Generic6DofJoint3D` is a `Joint3D` whose three linear and three angular axes (the joint node's local X, Y, Z, body B
relative to body A from the authored pose) are each `Free`, `Limited` or `Locked`, with a motor and a spring per axis.
It has the members and defaults in the proposal: a weld by default, every member live, Y angle limits within
−90°…90°, and `GetLinearOffset()`/`GetAngularOffsetDegrees()`.

It is built on custom Jitter2 constraints the engine owns, in `MainframeEngine/Src/Physics/3D/Constraints/`. It
breaks like the other joints, has a debug draw, the `arrows-move` icon and limit handles in the editor, and two Demo
props in `physics_3d`.

## What the reading found

These shape the steps.

- **Data budget.** Jitter2's `ConstraintData` header is 32 bytes: `_internal`, `DispatchId`, `ConstraintId` and the two
  body handles. That leaves **224 bytes** of the 256 (`Precision.ConstraintSizeFull`) for our fields. `HingeAngle`'s
  data alone is about 200 bytes.
- **No helpers to borrow.** `AngularConstraintMath` (rotation log, angular Jacobians) is internal, so the 6-DOF angle
  maths is ours. `MathHelper` and `JSymmetricMatrix` are public.
- **Conventions to match.**
  - Velocity terms are body1 − body2.
  - `Q0` is `conj(q2)·q1`, which `JointRecord3D.RestRotation` already gives.
  - `Prepare` runs once per substep with that substep's inverse dt, then `Iterate` runs the solver iterations.
  - `Prepare` applies warm starting.
  - Softness and bias defaults come from `Constraint.DefaultLinear*`/`DefaultAngular*`, for example angular softness
    0.001 and bias 0.2, and angular limits 0.001 and 0.1.
- **Threads.** Jitter2 calls `Prepare`/`Iterate` from its worker threads through static function pointers, so they read
  only their own data and the two bodies' `RigidBodyData`.

## Not in scope

- G9.10c (`SoftRow` drives for active ragdolls). This plan writes `SoftRow`, and G9.10c reuses it.
- `PhysicalBone3D.JointType.SixDof`, and moving the hinge, slider and distance PD springs into the solver (the
  proposal's open questions, default no).
- A render test. The `physics-joints` golden stays unchanged.
- Re-recording `baseline.json`. That needs the quiet machine, so it's an owner action listed in the log.

## Steps

- [x] **Data layout first.** Write the `SixDofLinear` and `SixDofAngular` data structs (`[StructLayout(Sequential)]`,
  starting with Jitter2's 32-byte header fields), and a test, `SixDofConstraintDataFits`, that asserts each fits with
  `Unsafe.SizeOf<T>() <= Precision.ConstraintSizeFull`.
  - Each axis gets one row. The row holds a mode, lower and upper limits, motor velocity and maximum, spring frequency,
    ratio and target, Jacobian axes, effective mass, bias, and impulses for limit or lock, motor and spring.
  - If a struct doesn't fit in 224 bytes, motors and springs move to a third constraint, `SixDofDrive` (three linear
    and three angular soft or velocity rows). Locks and limits stay in the first two. That change goes into the ADR
    and the log as a change from the proposal.

  Verified by: the size test, with the sizes printed in the log.
- [x] **`SoftRow`** (`Physics/3D/Constraints/SoftRow.cs`): Catto's soft constraint coefficients from frequency, damping
  ratio and the substep h. These are the bias rate, mass scale and impulse scale, as in Box2D's `b2MakeSoft`, with the
  frequency clamped to a quarter of the tick rate. It also has a scalar row solve with an impulse clamp.

  Verified by: unit tests against hand-computed coefficients (`SoftRowTests`): frequency 0 gives a rigid row, the
  clamp applies, and the results don't depend on mass.
- [x] **`SixDofLinear`:** `Prepare` and `Iterate` for three linear rows along frame A's axes, at B's anchor.
  - `Locked` is an equality row with Jitter2's linear softness and bias.
  - `Limited` is one-sided at lower or upper, with the clamp like `HingeAngle`'s.
  - The motor is a velocity row clamped by `MaxForce × h`, and the spring is a `SoftRow` towards its target.
  - Warm starting happens in `Prepare`.
  - Readback: the per-row offsets and the last substep's linear impulse.

  Verified by: the linear scenario tests below.
- [x] **`SixDofAngular`:** the relative rotation `conj(qA·fA)·(qB·fB)` decomposed into XYZ Euler angles, with
  Y in −90°…90°.
  - The row axes follow Bullet's `btGeneric6DofConstraint::calculateAngleInfo` (zlib, cited in a comment): axis 1 is
    B's Z × A's X, axis 0 is axis 1 × B's Z, and axis 2 is A's X × axis 1.
  - Rows as in the linear part, with Jitter2's angular softness and bias.
  - Readback: the three angles, and the angular impulse without the motor rows.

  Verified by: a decomposition test with known rotations (`SixDofAnglesMatchTheirRotation`) and the angular scenario
  tests.
- [x] **`Generic6DofJoint3D`** (`Physics/3D/Generic6DofJoint3D.cs`) and the record:
  - The node:
    - The `JointAxisMode` enum.
    - The six "Linear X…Angular Z" export groups with the proposal's names, defaults and `Range`s.
    - Radian properties for code.
    - `[EditorIcon("arrows-move", Family = EditorIconFamily.Physics)]`.
    - `CreateConstraints` and `ApplyLive`, which write the data and never re-create the joint.
    - `GetLinearOffset()` and `GetAngularOffsetDegrees()`.
  - The record (`PhysicsSpace3D.Joints.cs`): the two constraint fields, wired into `SetEnabled`, `RemoveConstraints`,
    `ForgetConstraints`, `LinearImpulse` and `AngularImpulse`, plus factories that set the frames from the record.

  Verified by: `just build` with 0 warnings, and the scenario tests.
- [x] **Debug draw** (`DrawDebugShape`, `IsStretched`):
  - The frame's three axes.
  - A tick pair per limited linear axis, and an arc per limited angular axis.
  - The current offsets.
  - Red when a locked or limited axis is out by more than 5 cm or 5°.

  Verified by: a `DebugDrawTests` case (a line count, and red only when stretched).
- [x] **Tests** in `Joints3DTests`, `JointBreakTests` and the scene round trips. Names follow the files' sentence
  style, and the log maps them to the proposal's `SixDof_*` names. The scenarios:
  - default locks every axis
  - free axis slides
  - linear limit holds
  - two-axis linear limits
  - angular limits per axis
  - angular motor reaches speed
  - linear spring period
  - mode change is live
  - breaks
  - scene round trip

  Also add 50 6-DOF joints (motors, springs, limits, breakable) to `JointAllocationTests` in single- and multi-threaded
  runs, with a multi-threaded scene that steps deterministically twice (`PhysicsSettings3D.Deterministic`). Verified
  by: `just test`, with every named test passing and both gates at 0 B.
- [x] **Editor:**
  - `Generic6DofJoint3DHandles` in `JointHandles.cs`: lower and upper handles on the axis for each linear axis, and on
    its arc for each angular axis.
  - `Limited` axes have solid handles, and `Free` axes have hollow ones, whose drag sets the axis to `Limited` in the
    same undo entry. `Locked` axes have none. This is how the plan reads the proposal's two sentences: its own
    scenario drags a hollow handle on a `Free` axis.
  - The order rule and snapping apply as in G9.7.
  - `JointEditorTests` gets the picker, inspector and icon. `JointHandleTests` gets each mode's handles, a drag that is
    one undo entry, the hollow handle setting `Limited`, and lower ≤ upper.

  Verified by: `just test`, and a scripted editor QA capture of the selected gimbal with its handles. Add that step to
  `Tests/QA/editor-walkthrough.qa` after the hinge's, then run `just qa-editor` and look at the PNG.
- [x] **Demo** (`Joints3DBuild`, `physics_3d`):
  - A gimbal mount: angular X and Z `Limited` ±40° with springs back to centre, the rest locked.
  - A drawer: linear X `Limited` 0–0.4 m, angular Y `Limited` ±5°, the rest locked.
  - Regenerate the scene with `--write-scenes`, and add Reset if the panel's reset doesn't cover them.
  - `PhysicsJointsSceneTests`: the gimbal knocked off centre returns within 2° in 3 s, and the drawer pushed open stops
    at 0.4 m ± 2 cm.

  Verified by: the Demo tests, and the Demo run and looked at (`--screenshot` at a couple of frames).
- [x] **Benchmark:** add `StepChain100SixDof` to `JointBenchmarks` (100 links, all axes `Locked` except angular X
  `Free`). Verified by: `just bench --filter *Joint*` run locally. The numbers go in the log against `StepChain100Pins`,
  with the proposal's target of at most 2×. The run is on a loaded machine, so it's indicative only, and
  `baseline.json` is not touched.
- [x] **ADR 0234** (`mf-decide`): the engine owns custom Jitter2 constraints. Record:
  - the unsafe static solver code in `Src/Physics/3D/Constraints/`
  - the 224-byte budget, and any split
  - the Jitter2 conventions it depends on
  - re-checking every custom constraint (the size test and the scenario tests) on any Jitter2 upgrade

  Verified by: `check.sh`.
- [x] **Docs:**
  - [physics-joints.md](../../docs/design/physics-joints.md): the 3D types table, a "Custom constraints" section, Code,
    icons, Tests, Differences, and "Still to come" (now active ragdolls).
  - [physics.md](../../docs/design/physics.md): the joints paragraph.
  - [demo.md](../../docs/design/demo.md): the `physics_3d` row.
  - `AGENTS.md`'s Physics bullet (the new `Constraints/` folder).
  - The README feature row and the landing page's Features text ("6-DOF").
  - milestones.md (G9.10b ✅), and the proposal's G9.10b ticked.

  Verified by: `just docs-build`, `check.sh`, and `git grep "weld joints in 3D and"` showing the new wording.
- [x] **Screenshots:**
  - Re-take `docs/images/demo/physics_3d.png` with `just demo-screenshots` and inspect it. Keep only PNGs that changed
    because of this work.
  - Add `docs/images/editor-6dof-handles.png` from the QA capture to physics-joints.md, the README editor table and the
    landing Showcase, per the "features on the site, README and docs" rule.

  Verified by: opening each PNG.
- [x] **Gates:** `just build`, the Release `-warnaserror` build, `just test`, `just test-render` (goldens unchanged),
  `just format-check`, `just shaders-check`, `check.sh`. Verified by: each command's exit status, recorded in the log.
- [ ] **Ship** (`mf-ship`): the log, and `current-state.md` (next: G9.10c). Commit and open the PR on the owner's
  go-ahead.

## Done when

- `Generic6DofJoint3D` exists with the proposal's members and defaults, on engine-owned constraints in
  `Src/Physics/3D/Constraints/`, and no Jitter2 call outside `Src/Physics`.
- The data-size test passes for every custom constraint struct.
- A named test passes for every 6-DOF scenario in the proposal, plus `SoftRowTests` and the Euler decomposition test.
- The 3D joint allocation gate measures 0 B with the 6-DOF joints, single- and multi-threaded.
- `JointHandleTests` covers the 6-DOF provider, and the QA capture shows the gimbal's handles.
- The Demo's `physics_3d` has the gimbal and the drawer, with passing scene tests.
- ADR 0234 exists.
- The benchmark numbers are in the log.
- Every gate in `.agents/workflow.md` passes, with the render goldens unchanged.
- physics-joints.md, physics.md, demo.md, AGENTS.md, the README and the landing page describe the joint, and the
  proposal's G9.10b and the milestone are ticked.

## Open questions

- Will motors and springs fit beside the limits in 224 bytes, or need `SixDofDrive`? The builder answers this in the
  first step.
- How stiff a locked angular axis feels under Jitter2's default softness, compared with `FixedAngle`. This is a feel
  question for the owner on the Demo gimbal, once built.
