# Plan: Active ragdolls (G9.10c)

**Status:** done, see [log](../logs/2026-11-03-active-ragdolls.md)
**Docs:**
- [Proposal: G9.10](../../docs/design/physics-joints.md#changes-from-the-g910-proposal) (accepted 2026-11-03, since moved into physics-joints.md): its "Active ragdolls"
  section, the active-ragdoll scenarios, and task list item G9.10c.
- [Physics joints and ragdolls](../../docs/design/physics-joints.md): "`PhysicalBone3D` and `PhysicalBoneSimulator3D`"
  and "Custom constraints".
- [ADR 0212](../decisions/0212-ragdolls-and-physical-bones.md) (the simulator, held bones, internal joint nodes).
- [ADR 0234](../decisions/0234-engine-owned-jitter2-constraints.md): custom constraints, `SoftRow`, each body's arm to
  its own anchor, and re-checking on Jitter2 upgrades.

**Started:** 2026-11-03

## Goal

`PhysicalBoneSimulator3D` drives simulated bones towards the animation. Two drives do it, both solved in Jitter2's
solver as soft rows:

- **Local drives** pull each bone's body towards its animated rotation relative to its parent bone's body.
- **World holds** pull chosen bodies towards their animated world pose.

The proposal's members and defaults:

- On the simulator: `DriveStrength` (0), `DriveFrequency` (10 Hz), `DriveDampingRatio` (1), `DriveMaxTorque`
  (200 N·m), `HoldStrength` (0), `HoldFrequency` (4 Hz), `HoldDampingRatio` (1), `HoldMaxForce` (2 000 N),
  `HoldMaxTorque` (500 N·m).
- On each bone: `DriveStrength` (1), `HoldStrength` (0), `GetDriveErrorDegrees()`, `GetHoldError()`.
- Strength scales frequency and maximum, and a strength of 0 turns a drive off. With the defaults, existing scenes
  behave exactly as before.
- `Build` sets `HoldStrength = 1` on the root body.
- The Demo's Animation 3D character gets Active and Push.

## What the reading found

These shape the steps.

- **Targets can't come from the skeleton during the fixed step.** While bones are simulated, the simulator's
  `OnProcess` (priority 1000) writes the bodies' poses into the skeleton. Reading `GetBoneGlobalPose` would feed the
  bodies back into their own targets. The animated pose is what the simulator already keeps per bone
  (`_animatedPositions`/`_animatedRotations`, refreshed when the animation re-poses a bone). For bones it doesn't
  write, it is the skeleton's own pose. Targets compose those parents first (bone indices are parent-ordered, as
  `OnProcess` assumes) into an animated-globals array, without allocating.
- **Joint machinery to reuse.** The simulator already makes internal joint nodes (`CreateJoint`: no owner,
  `UseAuthoredFrames`), so a drive can be a `Joint3D` subclass and inherit deferred creation, bodies leaving and
  returning, and "kinematic pairs wait". It must not exclude collisions (the exclusion group already does that) and
  must never break.
- **The generator.** It registers public and internal node types (`TypeModelBuilder`). An internal, unsaved drive node
  is harmless, but the first step checks it doesn't show up in the editor's Add Node list.
- **Jitter2 conventions** (ADR 0234): `Prepare` runs every substep with the whole step's inverse dt, `SoftRow` takes
  that dt, and arms reach each body's own anchor.
- **Test layout.** Engine tests use a small synthetic skeleton (`PhysicalBonesTests`). The humanoid scenarios
  (standing, recovering, fading, a partial arm) need the Demo character, so they are Demo tests.

## Not in scope

- Balance controllers, get-up animations, active `Ragdoll3D` rigs, velocity feed-forward, and
  `PhysicalBone3D.JointType.SixDof` (the proposal's non-goals and open questions).
- A render test. Goldens stay unchanged.

## Steps

- [x] **`PoseDrive` data first** (`Physics/3D/Constraints/PoseDrive.cs`):
  - The header; the target rotation (body 2 in body 1's frame) and target position (body 2's centre, in body 1's
    frame); flags (angular on, linear on); frequency, damping ratio and maximum for each part; and the runtime fields.
  - Runtime: the angular 3×3 effective mass and impulse, the per-axis linear effective mass and impulse, biases, and
    mass scales.
  - Test: `PoseDriveDataFits` (≤ `Precision.ConstraintSizeFull`). The rough sum is 192 bytes. If it doesn't fit, split
    it into `PoseDriveAngular` and `PoseDriveLinear` and record that.

  Verified by: the size test.
- [x] **`PoseDrive` solver:**
  - Angular: the rotation error between body 2 and body 1 × target, as an axis-angle vector (the quaternion log). A
    3×3 soft block on ωB − ωA uses the `SoftRow` coefficients, and the accumulated impulse's length is capped at
    `MaxTorque / idt`.
  - Linear: three world-axis rows at body 2's centre (no arms) towards the target, each capped so the impulse vector's
    length stays within `MaxForce / idt`.
  - Flags of 0 return at once. Warm starting happens in `Prepare`.

  Verified by: `PoseDriveTests`:
  - a body driven to a target rotation settles there
  - a hold pulls a body to a target position against gravity
  - `MaxTorque` 1 N·m can't lift a 1 kg, 0.5 m arm, which sags more than 30°
  - frequency matters, mass doesn't (the same motion on 1 kg and 10 kg)
- [x] **`BoneDrive3D`** (`Physics/Ragdoll/BoneDrive3D.cs`): an internal `Joint3D` that creates one `PoseDrive`.
  - `ExcludeNodesFromCollision = false`, and it never breaks.
  - Setters for strength, frequency, damping, maxima, flags and target, written to the data in place.
  - `GetErrorDegrees()` and `GetError()` read back from the bodies' physics poses against the stored target.
  - The record (`PhysicsSpace3D.Joints.cs`) gains the `PoseDrive` in its enable, remove and forget paths. It is not
    counted in breaking sums.

  Verified by: `just build` with 0 warnings, and the simulator tests below.
- [x] **Simulator and bone members:**
  - The exports: the "Drive" group on both classes, with the proposal's names, defaults and `Range`s.
  - On ready (and in `CreateJoint`'s place for re-creation), each bone with a parent body gets a local drive, and every
    bone gets a hold drive. All are internal children with no owner.
  - In `OnPhysicsProcess`, before the held-bones loop, the animated globals are built parents first.
  - Each simulated bone's drive targets are written then:
    - local: conj(parent's animated body rotation) × the bone's
    - hold: the bone's animated body pose
  - Effective strength is simulator × bone. Frequency × s and maximum × s are passed, and s = 0 sets the flags to 0.
  - `PhysicalBonesStartSimulation`/`StopSimulation` turn drives on and off with the bones.
  - `Build` sets the root body's `HoldStrength` to 1.

  Verified by: the scenario tests.
- [x] **Engine tests** (`ActiveRagdollTests`, a four-bone synthetic arm, sentence-style names mapped in the log). The
  proposal's scenarios that don't need a humanoid:
  - default is passive (no drive constraint on, and the bodies fall as in `SimulatedBodiesFallAndTheBonesFollowThem`)
  - local drives keep the pose (each bone within 10° of its animated rotation relative to its parent, after 2 s)
  - max torque limits
  - stop holds again
  - error readback
  - scene round trip (no drive node saved)
  - a fixed-step-animated clip is tracked without feedback

  Also add the simulator with drives to `SimulatingAPhysicalSkeletonDoesNotAllocate` (0 B). Every existing
  `PhysicalBonesTests` test must pass unchanged. Verified by: `just test`.
- [x] **Demo tests** (`PhysicsJointsSceneTests` on the Animation 3D character): hold keeps standing (pelvis within 5 cm
  and head within 10 cm for 3 s of idle), recovers from a hit (a 20 N·s chest impulse moves it at least 10 cm and it is
  back within 5 cm in 1.5 s), fade to limp (the pelvis drops at least 0.5 m within 2 s), and the partial arm. Frequency
  defaults may be tuned here (the proposal's open question). Verified by: the Demo tests.
- [x] **Demo panel** (`Animation3DPanel`, `.rml`): Active fades both strengths in and out over 0.3 s (a `Tween`), and
  Push applies the chest impulse. The strings go through extraction and Spanish translation. Verified by: the Demo run
  and a screenshot of the character mid-stagger (`--screenshot` at a few frames, with Push scripted from a test-only
  flag if needed).
- [x] **Benchmark:** `JointBenchmarks.Step20ActiveCharacters` against `Step20PassiveCharacters`, on the joint
  benchmark's rigid 11-part ragdoll shape rebuilt as a skeleton (or the Demo-free synthetic rig). Target: at most +50 %.
  Verified by: `just bench '*JointBenchmarks*'`, now that it builds again (#154), with the numbers in the log.
- [x] **ADR 0235** (`mf-decide`): active ragdolls are in-solver pose drives on internal joint nodes, with local drives
  plus world holds and targets from the stored animated pose. Verified by: `check.sh`.
- [x] **Docs:**
  - Move the proposal into [physics-joints.md](../../docs/design/physics-joints.md), with a "Changes from the
    proposal" section covering G9.10a–c, and repoint its links. This is the `move` shipping rule; G9 is then complete.
  - Tick G9 in milestones.md.
  - [animation.md](../../docs/design/animation.md) (Influence and drives), [demo.md](../../docs/design/demo.md), the
    README feature row, and the landing page.

  Verified by: `just docs-build`, `check.sh`, and `git grep "physics-joints-g9-10"` showing only historical logs and
  plans.
- [x] **Screenshots:** re-take `animation_3d.png` with `just demo-screenshots`, inspect it, and keep only changed PNGs
  that this work caused. Verified by: opening the PNG.
- [x] **Gates:** `just build`, the Release `-warnaserror` build, `just test`, `just test-render` (goldens unchanged),
  `just format-check`, `just shaders-check`, `check.sh`, and the Demo tests. Run the Linux unit tests in Docker
  (`just test-linux`) before pushing, because #153's chaos bug only showed on x64. Verified by: each exit status,
  recorded in the log.
- [x] **Ship** (`mf-ship`): the log, and `current-state.md` (G9 done; next from the toolkit list). Commit and open the
  PR on the owner's go-ahead.

## Done when

- The simulator and bone members exist with the proposal's names and defaults, and an existing scene with a simulator
  behaves exactly as before (its tests unchanged and passing).
- `PoseDrive` fits Jitter2's constraint data, and its tests pass.
- Every active-ragdoll scenario in the proposal has a passing named test, in the engine or the Demo.
- The physical-skeleton allocation test measures 0 B with drives and holds on.
- The Demo character staggers when pushed and recovers, and falls when Active is turned off, and `animation_3d.png`
  shows the panel.
- The benchmark numbers are in the log.
- ADR 0235 exists.
- The proposal has moved into physics-joints.md, and G9 is ticked in milestones.md.
- Every gate passes, including `just test-linux`.

## Open questions

- The drive frequency default (10 Hz) and the hold defaults. Tuned on the Demo character in the Demo-test step, and
  any change recorded. Builder.
- Whether the stagger and recovery look human (the proposal's human checklist). Owner, on the Demo.
