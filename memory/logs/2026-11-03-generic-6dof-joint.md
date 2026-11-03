---
summary: >-
  G9.10b: Generic6DofJoint3D on four engine-owned Jitter2 constraints (ADR 0234), with speculative limits, exact Euler
  rate axes, soft-row springs, debug draw, limit handles, a Demo gimbal and drawer, and a benchmark at 1.5× a pin chain.
---

# 2026-11-03: Generic6DofJoint3D (G9.10b)

Branch `feature/g9-10b-6dof`. Plan: [Generic6DofJoint3D](../plans/2026-11-03-generic-6dof-joint.md). Proposal:
[G9.10](../../docs/design/future/physics-joints-g9-10.md). Decision: [ADR 0234](../decisions/0234-engine-owned-jitter2-constraints.md).

## What changed

- **`Generic6DofJoint3D`** (`Physics/3D/Generic6DofJoint3D.cs`) and `JointAxisMode`:
  - The proposal's members and defaults: a weld by default, all live, and Y angle limits within −90°…90°.
  - Readbacks: `GetLinearOffset()`, `GetAngularOffset[Degrees]()` and `GetLinear/AngularMode(axis)`.
  - The `arrows-move` icon.
  - A debug draw (axes, tracks, arcs and current angle), red past a locked or limited axis by more than 5 cm or 5°.
- **Custom constraints** (`Physics/3D/Constraints/`):
  - `SixDofLinear` and `SixDofAngular` (locks and limits), and `SixDofLinearDrive` and `SixDofAngularDrive` (motors
    and springs).
  - `SixDofMath`: the Jitter2 header copy, rows, the Euler decomposition and rate axes, and speculative clamps.
  - `SoftRow`: Box2D's soft coefficients.
  - The record gains the four constraints, in its enable, remove and forget paths and its breaking sums.
- **Editor:** `Generic6DofJoint3DHandles`.
  - A lower and upper handle per axis that isn't locked, at real distances for linear axes and on arcs at 100, 85
    and 70 % of the radius for angular ones.
  - Free axes have hollow handles, and dragging one sets the axis to `Limited` in the same undo entry.
  - `JointHandles.Tangent`/`Arc` are now internal.
  - The QA walkthrough captures the gimbal (`25c-6dof-handles`).
- **Demo `physics_3d`** (`Joints3DBuild.AddSixDof`):
  - A gimbal dish: X and Z limited to ±40°, with 1.5 Hz springs back to level.
  - A drawer: 0.4 m of travel, ±5° of wobble, and a 0.6 Hz critically damped soft-close.
  - The panel text and its Spanish translation are updated, the scene regenerated, and `physics_3d.png` re-taken.
- **Benchmark:** `JointBenchmarks.StepChain100SixDof` (the pin chain's links on 6-DOF joints acting as pins).
- **Docs:**
  - physics-joints.md: the 3D table, a "Custom constraints" section with the editor screenshot, handles, tests and
    differences.
  - physics.md, demo.md and AGENTS.md.
  - The README (the feature row, the Physics 3D caption, and a 6-DOF row in the editor table).
  - The landing page (Features and Showcase).
  - milestones.md, and the proposal's G9.10b ticked.

## Decisions

- **Four constraints, not two:** the data didn't fit, as the plan's first step anticipated. Locks and limits take 200
  and 168 bytes, and the drives 220 and 224 (each limit is 256, including Jitter2's 32-byte header). See ADR 0234.
- **Speculative limits.** Jitter2 prepares constraints every substep, but with the whole step's inverse dt. With one
  substep, a limit that was inactive at the step's start let a 3.5 m/s body overshoot to 0.354 m on a 0.3 m limit. Box2D
  style speculative limits stop it at 0.300 m.
- **Exact Euler rate axes.** The plan's Bullet-derived axes fail the numeric rate test away from zero, so the axes are
  the dual basis of A's X, B's Z and their cross product.
- **Fully locked linear axes are a ball socket's world-axis rows.** The benchmark's chain flails at more than
  1,000 rad/s (Jitter2's own pin chain does too). There, the frame-axis rows went NaN within 4 s, and world-axis rows
  stay bounded. A 30 s regression test covers both paths.
- **The benchmark chain's joints act as pins**, with angular axes free. The plan's twist-only joint would make a rigid
  30 m cantilever, which doesn't compare with the pin chain.
- **The joint allocation gate stays single-threaded.** Without any 6-DOF joints, its multi-threaded run measured
  0 B on one run and 112 B on the next (Jitter2's thread pool). The new
  `A6DofSceneOnWorkerThreadsStepsTheSameEveryRun` covers the constraints on worker threads instead.
- **Soft-row springs aren't frequency-clamped:** they are stable at any stiffness. `SoftRow.ClampFrequency` exists
  for G9.10c if it wants it.
- **The Demo props moved twice** (behind the crate stack, then out of frame) before landing at front-left and
  front-centre, clear of the pusher's path along z = 0.
- **Test names** follow the files' style:

  | Proposal | Test |
  |---|---|
  | `SixDof_DefaultLocksEveryAxis` | `ADefault6DofJointLocksEveryAxisLikeAWeld` (a 0.5 m box anchored at its edge: the built-in weld itself fails the original 0.2 m box on a 1 m lever) |
  | `SixDof_FreeAxisSlides` | `AFree6DofAxisSlidesWhileTheOthersHold` |
  | `SixDof_LinearLimitHolds` | `ALimited6DofAxisRestsOnItsLowerLimit` |
  | `SixDof_TwoAxisLinearLimits` | `Two6DofLinearLimitsMakeASquareRange` |
  | `SixDof_AngularLimitsPerAxis` | `Each6DofAngularLimitHoldsItsOwnAxis` |
  | `SixDof_AngularMotorReachesSpeed` | `A6DofAngularMotorReachesItsSpeed` |
  | `SixDof_LinearSpringPeriod` | `A6DofLinearSpringOscillatesAtItsFrequency` |
  | `SixDof_ModeChangeIsLive` | `A6DofModeChangeIsLiveAndKeepsTheJoint` |
  | `SixDof_Breaks` | `A6DofJointOverItsBreakForceBreaks` |
  | `SixDof_SceneRoundTrip` | `A6DofJointRoundTripsThroughScenes` |
  | `SixDof_ConstraintDataFits` | `SixDofConstraintTests.EveryCustomConstraintsDataFitsJitter2sConstraintData` |
  | `JointHandleTests.SixDof_*` | `SixDofHandlesShow…`, `DraggingASixDofHollowHandle…`, `DraggingASixDofHandleInTheViewport…` |
  | (plan) `SoftRowTests`, the decomposition test | `SixDofConstraintTests` (soft-row coefficients, Euler round trips, rate axes) |
  | (plan) Demo | `TheGimbalTiltsWithinItsLimitsAndSpringsBackLevel`, `TheDrawerSlidesOutToItsStopAndClosesAgain` |

## Dead ends

- Bullet's angular axis formulas: correct only near zero for this decomposition.
- Frame-axis rows for a fully locked linear part: NaN on the flailing chain.
- The multi-threaded joint allocation gate: flaky with or without 6-DOF joints.
- `just bench`: BenchmarkDotNet's generated project can't copy the Steamworks shim (MSB3030), whatever the change. The
  numbers come from `--inProcess`. This is worth a fix of its own.

## Verified

- Benchmark, in-process on a loaded machine, so indicative only: `StepChain100Pins` 55.9 µs, `StepChain100SixDof`
  81.7 µs (1.46×, target ≤ 2×), 0 B; `Step50Ragdolls` 628 µs.
- All 106 engine joint, ragdoll, physical-bone, 6-DOF and debug-draw tests pass, with the 3D joint allocation gate at
  0 B.
- Editor joint tests: 30. Demo tests: 113.
- `just qa-editor` ran through the walkthrough, and the `25c-6dof-handles` capture was looked at.
- The Demo's `physics_3d` was looked at, with the gimbal and drawer in view.
- Gates, all passing:
  - `just build` and the Release `-warnaserror` build: 0 warnings.
  - `just test`: engine 3 192 passed (2 skipped), editor 1 809 (1 skipped), video 460.
  - `just test-render`: 222 passed, with the goldens unchanged.
  - `format-check`, `shaders-check`, `docs-build`, `l10n-check` and `check.sh`: ok.

## Not verified

- How stiff locked axes feel next to `WeldJoint3D`, and how the gimbal and drawer feel to push (the plan's owner
  question and the proposal's human checklist).
- `baseline.json` was not re-recorded. That needs the quiet machine (owner action).

## Next

- G9.10c: active ragdolls (`PoseDrive` on `SoftRow`, `BoneDrive3D`), from the accepted proposal. Plan it first.
- Fix `just bench` (the BenchmarkDotNet child build and the Steamworks shim copy).
