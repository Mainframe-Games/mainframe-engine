---
summary: >-
  G9.10 proposed and accepted (6-DOF joint, active ragdolls, motor joint); G9.10a built: MotorJoint2D over Box2D's
  motor joint, with tests, debug draw, editor icon and a lift in the Demo's Physics 2D scene.
---

# 2026-11-03: MotorJoint2D (G9.10a)

Branch `feature/g9-10-joints`. Proposal: [G9.10](../../docs/design/future/physics-joints-g9-10.md) (accepted). Plan:
[MotorJoint2D](../plans/2026-11-03-motor-joint-2d.md).

## What changed

- **Proposal** `docs/design/future/physics-joints-g9-10.md`. G9.10 had never been designed (the G9 proposal left it
  "later, on demand"). The owner's three answers:
  - `Generic6DofJoint3D` gets custom Jitter2 constraints, two of them because of Jitter2's 256-byte constraint data.
  - Active ragdolls use in-solver drives.
  - Active ragdolls get local drives plus world holds.

  It's in three phases: G9.10a, G9.10b and G9.10c. The milestones G9 row links it.
- **`MotorJoint2D`** (`Physics/2D/Joints2D.cs`):
  - The members and defaults are the proposal's. `LinearOffset`/`AngularOffset` move the authored frame A live
    (`b2Joint_SetLocalFrameA`), and the record keeps the authored frames.
  - Hertz 0 passes a maximum of 0 (that spring is off). Units convert through pixels per metre.
  - Its icon is `magnet` (Physics family).
  - `Joint2D.GetAnchorA(frame)` (internal) lets the debug draw put anchor A at the motor joint's target. The target
    gets a cross and its +X, and the line to B's anchor turns red over 5 px.
- **Demo:** `Joints2DBuild.AddLift` and `Lift2D` in `physics_2d`. A platform held by a motor joint to the world rises
  220 px and comes back down every 5 s. It eases from rest at the bottom, with its velocity fed to the joint, and
  carries two crates. Reset puts it back.
  - The panel text and its Spanish translation mention the lift (`.po`/`.mo`, qps regenerated).
  - `physics_2d.mscene` is regenerated, and `docs/images/demo/physics_2d.png` is re-taken.
- **Docs:**
  - physics-joints.md: the 2D types table, a `MotorJoint2D` paragraph, icons, tests, and the differences from the
    proposal.
  - physics.md, demo.md, the README feature row and Showcase caption, and the landing page's Features and Showcase.
  - The proposal's G9.10a is ticked.

## Decisions

- **`LinearVelocity` is in world axes**, as Box2D does it. It is neither body A's axes nor the frame's: a probe with a
  rotated joint and a rotated body A both gave world X. The proposal didn't say, so the doc and the member's comment
  now do.
- **The Demo lift feeds its target's velocity as well as its offset, and starts at the bottom, from rest.** The spring
  alone trailed the moving target by about 10 px, and starting mid-travel at full speed did too. The proposal's Demo
  said a `Tween` moves the offset; `Lift2D.OnPhysicsProcess` does it instead, because it sets both values from one
  phase.
- **Test names follow `Physics2DJointTests`' sentence style**, not the proposal's `MotorJoint2D_*` names:

  | Proposal or plan | Test |
  |---|---|
  | `MotorJoint2D_SpringReachesOffset` | `AMotorJointsSpringPullsItsBodyOntoTheOffsetTarget` (and `AMotorJointsOffsetIsInTheAxesOfItsFrameOnBodyA`) |
  | `MotorJoint2D_AngularOffset` | `AMotorJointsAngularOffsetTurnsItsBody` |
  | `MotorJoint2D_VelocityFriction` | `AMotorJointWithoutASpringActsAsFriction` |
  | (world axes, found here) | `AMotorJointsVelocityIsInWorldAxes` |
  | `MotorJoint2D_HertzZeroTurnsSpringOff` | `AMotorJointWithHertzZeroHasNoSpring` |
  | `MotorJoint2D_OffsetIsLive` | `AMotorJointsOffsetChangesLive` |
  | `MotorJoint2D_EnabledAndBreak` | `AMotorJointIsDisabledAndBrokenLikeTheOthers` |
  | `MotorJoint2D_SceneRoundTrip` | `MotorJointsRoundTripThroughScenes` |
  | `Platform_FollowsTarget` | `PhysicsJointsSceneTests.TheLiftFollowsItsTargetAndCarriesItsCrates` |
  | debug draw | `DebugDrawTests.AMotorJointDrawsItsTargetAndTurnsRedFarFromIt` |

- No ADR: the motor joint follows ADR 0201/0211's 2D joint model. G9.10b and G9.10c each record one.

## Dead ends

- The friction probe first decelerated at 238 px/s² instead of 200. The cause was the bodies' default 0.1
  `LinearDamp`, not Box2D: with damping 0 the friction is exactly F/m.

## Verified

- Every gate in `.agents/workflow.md` passed:
  - `just build` and the Release `-warnaserror` build: 0 warnings.
  - `just test`: engine 3 169 passed (2 skipped), editor 1 805 (1 skipped), video 460.
  - `just test-render`: 222 passed, with the goldens unchanged.
  - `format-check`, `shaders-check` and `check.sh`: ok.
- `just l10n-check`, `just docs-build`.
- Demo tests: 104 passed.
- The Demo itself: `physics_2d` at frames 90 and 240, with the platform near the top and on its way down, crates
  aboard.
- The editor (a scripted QA capture): `Lift/Motor` selected, with the `MotorJoint2D` header and `magnet` icon, Rebuild
  and Swap A/B, and the joint's draw on the lift.

## Not verified

- How riding and pushing the lift feels on real hardware (the proposal's human checklist).
- The inspector's Spring and Velocity groups were not looked at below the fold. The registry, round-trip and editor
  tests cover the members.

## Next

- G9.10b: `Generic6DofJoint3D` on custom Jitter2 constraints. Plan it first, starting with the constraint-size test.
