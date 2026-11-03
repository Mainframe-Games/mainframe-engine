---
summary: >-
  G9.10c: active ragdolls. In-solver PoseDrive constraints on internal BoneDrive3D nodes pull simulated physical bones
  towards the animation (local drives plus world holds). Tuned to 30/15 Hz and 2 substeps on the Demo character, which
  now has Active, Push and Let go. G9 is complete and the G9.10 proposal moved into physics-joints.md.
---

# 2026-11-03: Active ragdolls (G9.10c)

Branch `feature/g9-10c-active-ragdolls`. Plan: [Active ragdolls](../plans/2026-11-03-active-ragdolls.md). Decision:
[ADR 0235](../decisions/0235-active-ragdolls-in-solver-pose-drives.md).

## What changed

- **`PoseDrive`** (`Physics/3D/Constraints/PoseDrive.cs`, 192 bytes), a custom Jitter2 constraint:
  - An angular soft 3×3 block towards body 1 × target, with its accumulated impulse capped by `MaxTorque`.
  - Linear soft rows at body 2's centre, capped by `MaxForce`.
- **`BoneDrive3D`** (`Physics/Ragdoll/`): an internal `Joint3D` holding one `PoseDrive`.
  - `Drive(...)` writes the target and strength straight into the constraint, and `Stop()` turns it off.
  - Readbacks: `GetErrorDegrees()` and `GetPositionError()`.
  - It wakes body B whenever it drives or stops.
- **The simulator and bones:**
  - The "Drive" group on both, and on ready a local drive and a hold per bone (internal children, never saved).
  - Each fixed step, targets come from the animated pose, composed parents first.
  - `StopSimulation` stops the drives, and `Build` holds the root body.
  - `PhysicalBone3D.GetDriveErrorDegrees()`/`GetHoldError()` read back while simulated, at any strength.
- **Demo:**
  - The Animation 3D character holds its head as well as its pelvis, and `project.mfproj` runs 2 physics substeps.
  - The panel gets Active / Let go (a 0.3 s fade) and Push (40 N·s to the chest), with strings in Spanish.
  - The scene is regenerated, and `animation_3d.png` re-taken.
- **Tests:**
  - `PoseDriveTests` (8) and `ActiveRagdollTests` (9) in the engine.
  - `ActiveRagdollSceneTests` (5) in the Demo, on its own fixture at 2 substeps.
- **Benchmark:** `JointBenchmarks.Step20PassiveCharacters` and `Step20ActiveCharacters`.
- **Docs:**
  - physics-joints.md: "Active ragdolls", code, tests, and "Changes from the G9.10 proposal". The proposal file is
    removed: its content moved here, per the `move` rule.
  - animation.md, physics.md, demo.md, the README feature row and caption, and the landing page's Features and
    Showcase.
  - milestones.md: G9 ✅.

## Decisions

- **Defaults of 30 Hz drives and 15 Hz holds, and at least 2 substeps.** The proposal's 10 and 4 Hz were too soft. A
  frequency-based drive's stiffness is its body's inertia (or mass) × ω², and a spine drive or pelvis hold carries the
  whole body.
  - The data comes from the Demo character, probed over substeps, frequencies, holds and pushes:
    - 1 substep at 4 Hz: the pelvis sagged 31 cm and the upper body folded about 90°.
    - 1 substep at 30/15 Hz with a head hold: a 33 cm stagger, still 7 cm off after 1.5 s.
    - 2 substeps: 18 cm, then back within 7 mm.
    - 30 solver iterations help less than substeps.
- **The Demo holds the head too.** With only a pelvis hold, the head drifted 22–35 cm (the spine bends 13–22°, and the
  tall stack multiplies it). The proposal allowed "the pelvis, maybe the head".
- **Readbacks at any strength.** Targets keep updating at strength 0, so `GetHoldError()` reports a fall after the hold
  fades out, as the proposal's scenario needs for balance detection.
- **A driven body is kept awake.** `SetActivationState(true)` restarts Jitter2's sleep timer, so a body still in a
  perfect hold no longer sleeps through its drive stopping. The engine test caught it: the stopped box hung in mid-air.
- **The Demo push is 40 N·s, not 20,** to clear the 10 cm stagger the scenario asks for at these stiffnesses.
- **The one-arm check holds the forearm and hand within 10°,** not the whole arm. The upper arm sits about 29° off at
  its generic cone limit (Create physical skeleton's 45°/30° at the ready pose); humanoid limits are G1c.
- **Test names** follow the files' sentence style:

  | Proposal | Test |
  |---|---|
  | `ActiveRagdoll_DefaultIsPassive` | `ByDefaultASimulatedSkeletonIsPassiveAndFalls` (and every `PhysicalBonesTests` test unchanged) |
  | `ActiveRagdoll_LocalDrivesKeepPose` | `LocalDrivesKeepTheArmStraightAsItFalls` |
  | `ActiveRagdoll_HoldKeepsStanding` | Demo `AnActiveCharacterStandsWithItsPelvisAndHeadOnTheAnimation` |
  | `ActiveRagdoll_RecoversFromHit` | Demo `APushedActiveCharacterStaggersAndRecovers` |
  | `ActiveRagdoll_FadeToLimp` | Demo `LettingGoFadesTheDrivesOutAndTheCharacterFalls` |
  | `ActiveRagdoll_PartialArm` | Demo `OneArmCanBeActiveAloneAndReactsToAHit` |
  | `ActiveRagdoll_MaxTorqueLimits` | `AHeldRootAndLocalDrivesKeepTheArmOutAndAWeakDriveSags`, `PoseDriveTests.ADrivesMaxTorqueLimitsWhatItCanLift` |
  | `ActiveRagdoll_StopHoldsAgain` | `StoppingTheSimulationTurnsTheDrivesOffAndHoldsTheBodiesAgain` |
  | `ActiveRagdoll_ErrorReadback` | `TheHoldErrorReadsHowFarABodyIsFromItsAnimatedPoseEvenWithTheHoldOff` |
  | `ActiveRagdoll_SceneRoundTrip` | `DriveSettingsRoundTripThroughScenesAndTheDrivesAreNeverSaved` |
  | (plan) `PoseDriveDataFits` | `PoseDrivesDataFitsJitter2sConstraintData` |
  | (plan) fixed-step animation, no feedback | `DriveTargetsComeFromTheAnimationNotFromThePoseTheBodiesWroteBack` |

## Dead ends

- **Effective inertia about the joint's anchor**, like the PD springs. In the solver it over-corrects every iteration
  and diverges, and the character exploded in its first frame (ADR 0235, Ruled out).
- **A "convention bug" chase on the one-arm case.** The Demo panel's new fade ran every frame and quietly faded
  strengths set from outside back to 0, so the probes' drives were off. It now fades only after Active or Let go. A
  local drive against a turned body A was tested along the way and kept (`ALocalDriveTurnsBodyBToItsTarget…`).
- **A mid-stagger screenshot** needs Push scripted into the Demo's screenshot mode. It wasn't added: the Demo tests
  cover the behaviour, and `animation_3d.png` shows the new buttons.

## Verified

- Benchmark (`just bench`, busy machine): `Step20PassiveCharacters` 464.6 µs, `Step20ActiveCharacters` 267.3 µs,
  both 0 B. The active ones are cheaper because passive ones collapse into piles of contacts. Target: at most +50 %.
- Engine: 23 active-ragdoll, drive and physical-bone tests. Demo: 118 tests, 5 of them new.
- `animation_3d.png` was inspected.
- Gates:
  - `just build` and the Release `-warnaserror` build: 0 warnings.
  - `just test`: engine 3 211 passed (2 skipped), editor 1 809 (1 skipped), video 460.
  - `just test-render`: 222 passed, with the goldens unchanged.
  - `format-check`, `shaders-check`, `docs-build`, `l10n-check` and `check.sh`: ok. Demo: 118 passed.
- `just test-linux` (x64 in Docker, which caught #153's chaos bug): every physics, ragdoll, drive and 6-DOF test
  passes.
  - One unrelated test fails, `CrashProcessTests.WithTheDumpVariablesANativeFaultLeavesADump`. It fails every time,
    re-run alone too.
  - .NET's `createdump` captures a native crash under Rosetta x86 emulation, and nothing on this branch touches crash
    reporting.
  - The same unchanged test passed on GitHub's native x64 Ubuntu runner for #153 and #154.

## Not verified

- Whether the stagger and recovery look like a person (the proposal's human checklist): owner, on the Demo, Animation
  3D, Active then Push.
- Active ragdolls on other clips (run, swing): the generic limits fight fast poses. Humanoid limits (G1c) come first.

## Next

- G1c (animation authoring, humanoid limits) would make active ragdolls follow any clip. Otherwise the toolkit list:
  G12 (proposal awaits review), G11, G13 2D paths, G14, G16, G17, G3b.
