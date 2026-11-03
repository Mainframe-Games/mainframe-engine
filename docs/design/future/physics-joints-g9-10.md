# Proposal: G9.10 — Generic6DofJoint3D, active ragdolls and MotorJoint2D

**Status:** accepted (2026-11-03) · **Milestone:** [Gameplay toolkit](../../milestones.md#gameplay-toolkit-) (G9.10, the last part of G9) ·
**Builds on:** [Physics joints and ragdolls](../physics-joints.md) ([ADR 0211](../../../memory/decisions/0211-3d-joints-on-jitter2.md),
[ADR 0212](../../../memory/decisions/0212-ragdolls-and-physical-bones.md)), [Physics](../physics.md)

## Problem

G9 shipped six 3D joints, six 2D joints, rigid and skeleton ragdolls and limit handles, and left three things for
"later, on demand":

- **No 6-DOF joint.** A joint that locks, limits or frees each of the six axes separately (a gimbal limited on two
  axes, a drawer that slides and tilts a little, a vehicle seat on springs, a camera rig) can't be built. Jitter2 2.9
  has no 6-DOF constraint, and its primitives (`BallSocket`, `PointOnLine`, `PointOnPlane`, `HingeAngle`,
  `ConeLimit`…) can't express per-axis limits on two axes at once (a box or elliptical range). The combinations they
  can express mostly duplicate the hinge, slider and cone-twist joints.
- **Ragdolls are only passive.** A `PhysicalBoneSimulator3D` either follows the animation (held kinematic) or goes limp.
  A character can't stagger when hit and keep standing, have an arm knocked aside while the rest of the body animates,
  or fade from animated to limp over half a second.
- **No 2D motor joint.** Box2D's motor joint (drive one body towards an offset from another, or towards the world, with a
  spring and a capped velocity "friction") isn't exposed. Top-down games use it for ground friction, and platformers
  use it for platforms and grabbed objects that follow a target and still collide.

## Goals

- `Generic6DofJoint3D`: each of the three linear and three angular axes is free, limited or locked, with a motor and a
  spring per axis. It's built on two custom Jitter2 constraints the engine owns, so per-axis limits really are per axis.
- Active ragdolls on `PhysicalBoneSimulator3D`:
  - **Local drives** pull each simulated bone towards its animated rotation relative to its parent bone.
  - **World holds** pull chosen bones (the pelvis, maybe the head) towards their animated world pose.
  - Both drives are solved inside Jitter2's solver with frequency, damping ratio and a maximum torque or force.
  - Both have per-bone and simulator-wide strengths, so they can fade in and out.
- `MotorJoint2D` over Box2D's motor joint: a spring towards an offset target and a capped relative velocity.
- Debug draw, inspector groups, icons, limit handles (6-DOF), scene round trips, Demo scenes, unit tests, allocation
  gates and benchmarks, the same as every other G9 joint.

## Non-goals

- Balance controllers, foot placement, get-up animations: games build these on the drives and holds, using
  `StartSimulation`/`StopSimulation` and the error readbacks.
- Active `Ragdoll3D` rigid-part rigs (driving parts towards poses an `AnimationPlayer` gives their nodes). The drive
  constraint would allow it later.
- `PhysicalBone3D.JointType.SixDof` ([Open questions](#open-questions)).
- Velocity feed-forward on drives (a target angular velocity taken from how the animation changes).
- Moving the hinge, slider and distance PD springs into the solver ([Open questions](#open-questions)).
- Humanoid limits (G1c), cross-platform determinism, replicating joint state: unchanged from G9.
- New NuGet packages: none are needed.

## Design

### Code layout

| File | Contents |
|---|---|
| `Physics/3D/Constraints/SoftRow.cs` | The shared math: soft-constraint coefficients from frequency and damping ratio (Box2D's `b2MakeSoft`: bias rate, mass scale, impulse scale), and 1-D and 3-D soft rows with an impulse clamp |
| `Physics/3D/Constraints/SixDofLinear.cs`, `SixDofAngular.cs` | The two custom Jitter2 constraints of the 6-DOF joint |
| `Physics/3D/Constraints/PoseDrive.cs` | The custom drive constraint: angular 3-D soft row, optional linear 3-D soft row, each clamped by a maximum |
| `Physics/3D/Generic6DofJoint3D.cs` | The node |
| `Physics/3D/PhysicsSpace3D.Joints.cs` | Factories for the new constraints; breaking reads their impulses |
| `Physics/Ragdoll/BoneDrive3D.cs` | Internal joint node (not saved, no owner) the simulator creates per driven bone |
| `Physics/Ragdoll/PhysicalBoneSimulator3D.cs`, `PhysicalBone3D.cs` | Drive and hold exports, targets |
| `Physics/2D/Joints2D.cs`, `PhysicsSpace2D.Joints.cs` | `MotorJoint2D` and its Box2D calls |
| `Editor/Src/Viewport/Handles/JointHandles.cs` | A provider for `Generic6DofJoint3D` |

Every Jitter2 and Box2D call stays in `Src/Physics`.

### Custom Jitter2 constraints

Jitter2 2.9 supports custom constraints: a `Constraint<T>` subclass whose `T` is an unmanaged struct of at most
`Precision.ConstraintSizeFull` = 256 bytes including Jitter2's header (checked by `CheckDataSize<T>`). It registers a
pair of static function pointers (`RegisterFullConstraint(prepare, iterate)`), and `Create()` sets the dispatch id.
Each function gets a `ref ConstraintData` and the step's inverse dt. The engine therefore owns solver code:

- **Static, unsafe, allocation-free and thread-safe.** Jitter2 runs `prepare`/`iterate` on its worker threads, so the
  functions read only their data and the two bodies' data, the way Jitter2's own `HingeAngle` does.
- **Parameters live in the struct.** Live setters on the node write into `Data` between steps, as the existing joints
  write `LocalAnchor`/`Axis`/`Q0`.
- **256 bytes is the budget.** A 6-DOF joint needs anchors, rest rotation, and per axis a mode, limits, motor, spring,
  effective mass and up to three accumulated impulses. That doesn't fit in one struct, so the joint is two constraints,
  `SixDofLinear` (three linear rows) and `SixDofAngular` (three angular rows). The plan's first step lays out both
  structs and checks their sizes in a test.
- **Soft rows.** A spring or drive is Catto's soft constraint: ω = 2πf, bias rate and mass scale from ω, ζ and the
  substep. It's stable at any stiffness and independent of mass, so the same frequency means the same motion on a 1 kg
  hand and a 10 kg torso. Frequency is clamped to a quarter of the tick rate, like the existing PD springs. Locked
  axes and limits use Jitter2's own softness/bias defaults, so a 6-DOF joint feels like the other joints at its
  limits.

### `Generic6DofJoint3D`

A `Joint3D` like the others (`NodeA`/`NodeB`, breaking, `Enabled`, exclusion, frames captured from the authored pose,
`Rebuild()`). The axes are the joint node's local X, Y, Z, measured as body B relative to body A from the authored pose.
Every member is live: changing a mode, limit, motor or spring writes the constraint data and never re-creates the joint.

Per linear axis (X, Y, Z), in a "Linear X"/"Linear Y"/"Linear Z" inspector group:

| Member | Default | Notes |
|---|---|---|
| `LinearXMode` | `Locked` | `JointAxisMode`: `Free`, `Limited`, `Locked` |
| `LinearXLower`, `LinearXUpper` | −1 m, 1 m | Used when `Limited` |
| `LinearXMotorEnabled`, `LinearXMotorTargetVelocity`, `LinearXMotorMaxForce` | off, 0 m/s, 100 N | |
| `LinearXSpringEnabled`, `LinearXSpringFrequency`, `LinearXSpringDampingRatio`, `LinearXSpringTarget` | off, 2 Hz, 0.5, 0 m | In the solver (soft row) |

Per angular axis, in "Angular X/Y/Z" groups: `AngularXMode` (`Locked`), `AngularXLowerDegrees`/`UpperDegrees` (−45°,
45°; radians properties for code), motor (`AngularXMotorTargetVelocity` rad/s, `AngularXMotorMaxTorque` 100 N·m), spring
(`AngularXSpringTargetDegrees`). The defaults are a weld, as in Godot. The motor and spring defaults are the hinge's.

- **Angles** are the relative rotation's XYZ Euler angles with Bullet's (and Godot's) 6-DOF axes. Y is the middle
  angle, so its limits have the `Range` −90°…90° (gimbal lock). X and Z cover −180°…180°.
- **Reading:** `GetLinearOffset()` (metres in A's frame), `GetAngularOffsetDegrees()` (the three angles).
- **Modes, not Godot's flags.** Godot spells "locked" as a limit with lower = upper. An enum reads better in the
  inspector and the debug draw. Lower = upper under `Limited` still works.
- **Breaking** sums the linear rows' impulses (force) and the angular rows' impulses except motors (torque), as ADR
  0211 does.
- **Debug draw:** the frame's axes, a tick pair per limited linear axis, an arc per limited angular axis, the current
  offsets. It uses the usual colours (amber, red when a locked or limited axis is stretched by more than 5 cm or 5°,
  grey when pending).
- **Editor:** an icon from the existing atlas (chosen in the plan, like the other joints), and limit handles: a
  `Lower`/`Upper` pair on the axis for each `Limited` linear axis and on its arc for each `Limited` angular axis.
  Locked and free axes get none. Hollow handles and the order rule (G9.7) apply: dragging a hollow handle sets the
  axis to `Limited` in the same undo entry.

### Active ragdolls

The simulator gains two fades, and each physical bone gains two strengths.

| `PhysicalBoneSimulator3D` ("Drive" group) | Default | Notes |
|---|---|---|
| `DriveStrength` | 0 | 0–1, fades every local drive. 0 keeps today's passive ragdoll |
| `DriveFrequency`, `DriveDampingRatio`, `DriveMaxTorque` | 10 Hz, 1, 200 N·m | |
| `HoldStrength` | 0 | 0–1, fades every world hold |
| `HoldFrequency`, `HoldDampingRatio`, `HoldMaxForce`, `HoldMaxTorque` | 4 Hz, 1, 2 000 N, 500 N·m | |

| `PhysicalBone3D` ("Drive" group) | Default | Notes |
|---|---|---|
| `DriveStrength` | 1 | Scales the simulator's drive for this bone (0: a limp bone in an active body) |
| `HoldStrength` | 0 | This bone's world hold. `Build` sets 1 on the root body (the pelvis) |
| `GetDriveErrorDegrees()`, `GetHoldError()` | | Angle from the drive's target; distance in metres from the hold's target. Games use them to detect lost balance |

- **Strength.** The effective strength s is the simulator's strength × the bone's. The effective frequency is f × s
  and the maximum is max × s. At s = 0 the constraint is disabled and costs nothing. Fading a strength from 1 to 0 over
  half a second (a `Tween`) turns a staggering character into a ragdoll.
- **Local drive:** a `PoseDrive` between the parent physical bone's body and this bone's body (angular rows only). Its
  target is the bone's animated rotation relative to its parent, in the two bodies' frames (with their
  `BodyOffset`s). The root bone has no local drive.
- **World hold:** a `PoseDrive` between the world (Jitter2's `NullBody`) and the body, angular and linear. Its target
  is the bone's animated global pose × `BodyOffset`.
- **Targets** come from the animated pose the simulator already keeps per bone (`_animatedPositions`/
  `_animatedRotations`, ADR 0212). They are written in the fixed step before the solver, using the same skeleton-to-
  world maths that held bodies use today. A paused animation holds its pose.
- **Only simulated bones are driven.** `PhysicalBonesStartSimulation(names)` enables the drives of those bones.
  `PhysicalBonesStopSimulation()` disables them and holds every body as today. A simulated arm under a held (kinematic)
  shoulder is driven against the kinematic body, so the arm alone can react to a hit while the rest animates.
- **Drives are internal joint nodes.** `BoneDrive3D` is an internal `Joint3D` child of the simulator with no owner (not
  saved, not in the editor's tree), like the physical-bone joints today. It reuses the joint records' deferred creation,
  bodies leaving and returning, and the rule that a pair with no dynamic body waits. It never excludes collisions (the
  simulator's exclusion group already does), and it never breaks.
- **`Influence`** is unchanged. An active ragdoll normally uses 1, since the bones show the bodies, which follow the
  animation.

### `MotorJoint2D`

A `Joint2D` over Box2D's motor joint. Frame A is the joint node's pose on body A (or the world). Frame B is the same
pose on body B at creation. The motor drives B's frame towards A's frame moved by the offsets.

| Member | Default | Notes |
|---|---|---|
| `LinearOffset` | (0, 0) px | In frame A's axes. Live (`b2Joint_SetLocalFrameA`) |
| `AngularOffset` | 0 rad | Radians, like `PinJoint2D`. Live |
| `LinearHertz`, `LinearDampingRatio`, `MaxSpringForce` | 4, 1, 10 000 kg·px/s² | Hertz 0 turns the linear spring off (the engine passes a maximum of 0) |
| `AngularHertz`, `AngularDampingRatio`, `MaxSpringTorque` | 4, 1, 1 000 000 kg·px²/s² | Likewise |
| `LinearVelocity`, `AngularVelocity` | 0 px/s, 0 rad/s | The relative velocity the velocity part drives towards |
| `MaxVelocityForce`, `MaxVelocityTorque` | 0, 0 | 0 = off. With velocity 0 this is friction: top-down ground friction is a motor joint to the world |

Box2D's own defaults are all zero (a joint that does nothing). These engine defaults give a node that pulls body B onto
its target as soon as it's added. Debug draw: both frames, the target as a cross, and a line from body B's anchor to the
target. It has no limit handles (it has no limits).

### Demo

- **`physics_3d`:** a gimbal mount (`Generic6DofJoint3D`: angular X and Z limited to ±40° with springs back to centre,
  the rest locked) and a drawer that slides 0.4 m and tilts ±5°.
- **`animation_3d`:** the character's panel gains Active (fades `DriveStrength`/`HoldStrength` in or out over 0.3 s) and
  Push (an impulse to the chest). An active character staggers and recovers. Fading out lets it fall.
- **`physics_2d`:** a platform pulled by a `MotorJoint2D` to the world whose `LinearOffset` a `Tween` moves back and
  forth. Boxes ride it and it can be pushed aside.

## Scenarios

### Generic6DofJoint3D

- Given a 1 kg box joined to the world by a default `Generic6DofJoint3D`, when 2 s pass under gravity, then its anchor
  stays within 1 cm and its rotation within 1° of the authored pose. Test: `SixDof_DefaultLocksEveryAxis`.
- Given `LinearYMode = Free` and the joint's Y along gravity, when 1 s passes, then the box has fallen at least 4.5 m
  along −Y and moved less than 1 cm along X and Z, and it hasn't rotated more than 1°. Test: `SixDof_FreeAxisSlides`.
- Given `LinearYMode = Limited` with −0.5…0.5 m and Y along gravity, when 2 s pass, then the box rests at −0.5 m within
  2 cm. Test: `SixDof_LinearLimitHolds`.
- Given X and Z `Limited` to ±0.3 m and Y `Locked`, when a diagonal impulse of 5 N·s hits the box, then its offset never
  exceeds 0.32 m on X or on Z. Test: `SixDof_TwoAxisLinearLimits`.
- Given angular X `Limited` to ±30°, angular Z `Limited` to ±60° and Y `Locked`, when a torque impulse about X and Z
  hits the box, then `GetAngularOffsetDegrees()` stays within 2° of the limits on each axis. Test:
  `SixDof_AngularLimitsPerAxis`.
- Given angular Z `Free` with its motor at 2 rad/s and 100 N·m, when 1 s passes, then the relative angular velocity
  about Z is within 2 % of 2 rad/s. Test: `SixDof_AngularMotorReachesSpeed`.
- Given linear Y `Free` with a 1 Hz spring, damping ratio 0, and gravity off, when the box is released 0.2 m from the
  target, then it oscillates with a period within 5 % of 1 s. Test: `SixDof_LinearSpringPeriod`.
- Given a running joint, when `LinearYMode` goes from `Locked` to `Free`, then the next step frees the axis, the joint
  isn't re-created (same frames), and `IsBroken` is unchanged. Test: `SixDof_ModeChangeIsLive`.
- Given `BreakForce = 50` N and a 10 kg box hanging from it, when one step runs, then `Broken` is raised once and
  `IsActive` is false. Test: `SixDof_Breaks`.
- Given a saved scene with a configured joint, when it is loaded, then every axis member round-trips. Test:
  `SixDof_SceneRoundTrip`.
- Given the joint's data structs, then each fits Jitter2's 256 bytes. Test: `SixDof_ConstraintDataFits`.
- Given a selected joint in the editor with angular X `Limited` and the rest `Locked`, then it shows two handles (lower
  and upper on the X arc). When the upper handle is dragged and released, then `AngularXUpperDegrees` changes in one
  undo entry. Dragging a hollow handle on a `Free` axis sets it to `Limited` in the same entry. Test:
  `JointHandleTests.SixDof_*`.

### Active ragdolls

- Given an existing scene with a `PhysicalBoneSimulator3D` (strengths at their defaults), when its simulation starts,
  then no drive constraint is enabled and it falls exactly as before. Test: `ActiveRagdoll_DefaultIsPassive` (and
  every existing `PhysicalBonesTests` test passes unchanged).
- Given the Demo character simulating every bone with `DriveStrength = 1` and `HoldStrength = 0`, when 2 s pass, then
  the body falls, but each bone's rotation relative to its parent stays within 10° of its animated pose. Test:
  `ActiveRagdoll_LocalDrivesKeepPose`.
- Given the same character with `HoldStrength = 1` (the pelvis held by `Build`), when 3 s of the idle clip pass, then
  the pelvis stays within 5 cm and the head within 10 cm of their animated positions. Test:
  `ActiveRagdoll_HoldKeepsStanding`.
- Given the standing active character, when a 20 N·s impulse hits the chest, then the chest moves at least 10 cm away,
  and within 1.5 s it is back within 5 cm of its animated pose. Test: `ActiveRagdoll_RecoversFromHit`.
- Given the standing active character, when both strengths go to 0, then within 2 s the pelvis has dropped at least
  0.5 m. Test: `ActiveRagdoll_FadeToLimp`.
- Given only the left arm's bones simulated with drives on and the rest animated, when 2 s of the idle clip pass, then
  the arm stays within 10° of its animated pose. When an impulse hits the hand, then the hand moves at least 10 cm and
  returns. Test: `ActiveRagdoll_PartialArm`.
- Given the arm horizontal with `DriveMaxTorque = 1` N·m, when 2 s pass, then the arm sags more than 30° below its
  target. Test: `ActiveRagdoll_MaxTorqueLimits`.
- Given an active character, when `PhysicalBonesStopSimulation()` runs, then every drive is disabled and the bodies
  follow the animation again. Test: `ActiveRagdoll_StopHoldsAgain`.
- Given an active character, then `GetHoldError()` on the pelvis is under 5 cm while standing. After a push that knocks
  it over with the hold faded out, it is over 30 cm. Test: `ActiveRagdoll_ErrorReadback`.
- Given a scene saved with drive and hold settings, when it is loaded, then they round-trip and no `BoneDrive3D` is
  saved. Test: `ActiveRagdoll_SceneRoundTrip`.

### MotorJoint2D

- Given a 1 kg box joined to the world by a `MotorJoint2D` with `LinearOffset = (200, 0)`, the defaults and gravity
  off, when 2 s pass, then the box's anchor is within 1 px of the authored anchor + (200, 0). Test:
  `MotorJoint2D_SpringReachesOffset`.
- Given `AngularOffset = π/2`, when 2 s pass, then body B has turned 90° within 1°. Test: `MotorJoint2D_AngularOffset`.
- Given a 1 kg box sliding at 500 px/s with gravity off and a motor joint to the world with `LinearHertz = 0` and
  `MaxVelocityForce = 200`, when 1 s passes, then its speed is 300 px/s within 2 %. Test: `MotorJoint2D_VelocityFriction`.
- Given a running joint, when `LinearOffset` changes, then the box moves to the new target and the joint isn't
  re-created. Test: `MotorJoint2D_OffsetIsLive`.
- Given a saved scene with a motor joint, when it is loaded, then every member round-trips. Test:
  `MotorJoint2D_SceneRoundTrip`.

## Testing

- **Allocation gates:** `JointAllocationTests` adds 50 6-DOF joints (motors, springs, limits, breakable) and five active
  Demo characters with drives and holds: 0 B per step. The 2D gate adds a motor joint.
- **Benchmarks** (`JointBenchmarks`): a 100-link 6-DOF chain (target: at most 2× the pinned chain's 57 µs), and 20
  active characters against 20 passive ones (target: at most +50 %). `baseline.json` is re-recorded on the quiet
  machine.
- **Debug draw:** `DebugDrawTests` covers the 6-DOF and motor joints' lines.
- **Render tests:** none new. The `physics-joints` golden stays unchanged (the new Demo props are in the Demo, not the
  render scene).
- **Demo:** `PhysicsJointsSceneTests` (the gimbal returns to centre, the drawer stops at 0.4 m, the 2D platform follows its
  target) and the Animation 3D tests (Active + Push: the character stays standing; fade out: it falls).
- **QA:** `just qa-editor` selects the gimbal and drags an angular limit handle.

## Human checklist

- [ ] The active character's stagger and recovery look like a person, not a spring (Demo, Animation 3D, Push).
- [ ] The fade from active to limp reads as losing balance, not a pop.
- [ ] Dragging the 6-DOF limit handles feels like the hinge's.
- [ ] The 2D platform feels solid to ride and to push.

## Task list

- [x] **G9.10a `MotorJoint2D`:** node, record, Box2D calls (frames, live offsets, "hertz 0 = off"), debug draw, icon,
  tests, the `physics_2d` platform, docs.
- [x] **G9.10b `Generic6DofJoint3D`:** `SoftRow`, `SixDofLinear`/`SixDofAngular` (size test first), the node, live
  setters, breaking, debug draw, icon, limit handles, tests, allocation gate, benchmark, the `physics_3d` props, docs. ADR:
  the engine owns custom Jitter2 constraints (unsafe solver code in `Src/Physics`, re-checked on every Jitter2 upgrade).
- [ ] **G9.10c Active ragdolls:** `PoseDrive` on `SoftRow`, `BoneDrive3D`, simulator and bone exports, targets, `Build`
  holds the root, tests, allocation gate, benchmark, the `animation_3d` Active/Push panel, docs. ADR: active ragdolls are
  in-solver drives with local drives + world holds.
- [ ] **Finish:** move this into [physics-joints.md](../physics-joints.md) with "Changes from the proposal", tick G9 in
  [milestones.md](../../milestones.md), update the README feature row and the landing page, and take the editor
  screenshot of the 6-DOF handles.

## Open questions

- **`PhysicalBone3D.JointType.SixDof`** (Godot has it). It would add about 60 members to every physical bone. Default:
  leave it out until a game needs per-axis bone limits. Owner.
- **The hinge, slider and distance springs.** They stay pre-step PD springs (ADR 0211), while the 6-DOF springs and
  drives are soft rows in the solver. Move them onto `SoftRow` later for one spring model? Default: not in G9.10. Owner.
- **Drive frequency default.** 10 Hz is a guess for a staggering humanoid at 60 Hz ticks (clamped to 15 Hz). Tuned on the
  Demo character during G9.10c. Builder.

## Related

[Physics joints and ragdolls](../physics-joints.md) · [Physics](../physics.md) · [Animation](../animation.md) ·
[ADR 0211](../../../memory/decisions/0211-3d-joints-on-jitter2.md) · [ADR 0212](../../../memory/decisions/0212-ragdolls-and-physical-bones.md) ·
[ADR 0020](../../../memory/decisions/0020-physics-precision-and-determinism.md)
