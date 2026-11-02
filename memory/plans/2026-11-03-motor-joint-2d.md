# Plan: MotorJoint2D (G9.10a)

**Status:** done, see [log](../logs/2026-11-03-motor-joint-2d.md)
**Docs:** [Proposal: G9.10](../../docs/design/future/physics-joints-g9-10.md#motorjoint2d) (accepted 2026-11-03; its
`MotorJoint2D` section, scenarios and task list item G9.10a), [Physics joints and ragdolls](../../docs/design/physics-joints.md)
(2D types, "In the space", debug draw), [ADR 0201](../decisions/0201-2d-joints-on-box2d.md),
[ADR 0211](../decisions/0211-3d-joints-on-jitter2.md), [Physics → Libraries](../../docs/design/physics.md#libraries)
**Started:** 2026-11-03

## Goal

`MotorJoint2D` is a `Joint2D` over Box2D's motor joint, with the members and defaults in the proposal:

- `LinearOffset` (px) and `AngularOffset` (rad), live through `b2Joint_SetLocalFrameA` (the authored frame A moved by
  the offsets).
- The spring: `LinearHertz`/`LinearDampingRatio`/`MaxSpringForce` (4, 1, 10 000 kg·px/s²) and
  `AngularHertz`/`AngularDampingRatio`/`MaxSpringTorque` (4, 1, 1 000 000 kg·px²/s²). Hertz 0 turns that spring off:
  the engine passes a maximum of 0.
- The velocity part: `LinearVelocity`/`AngularVelocity` (0), with `MaxVelocityForce`/`MaxVelocityTorque` (0 = off).

Everything else is inherited from `Joint2D`: paths, `Enabled`, `DisableCollision`, breaking, `Rebuild()`, readbacks.
It draws its frames, the target as a cross, and a line from body B's anchor to the target. The editor shows it with
the `magnet` icon (already in `icons.txt`). The Demo's `physics_2d` scene gets a platform pulled by a motor joint whose
offset a `Tween` moves. No Box2D call leaves `Src/Physics`.

## Not in scope

- G9.10b (`Generic6DofJoint3D`) and G9.10c (active ragdolls): their own plans.
- Limit handles: the motor joint has no limits.
- A render test: the `physics-joints` render scene is 3D, and its golden stays unchanged.

## Steps

- [x] Check Box2D.NET 3.1.654's motor joint semantics before relying on them. The functions are
  `b2DefaultMotorJointDef`, `b2CreateMotorJoint`, the `b2MotorJoint_*` setters and `b2Joint_SetLocalFrameA`. Confirm
  that the spring drives frame B onto frame A, that `maxSpringForce` 0 means no spring force, and that velocity and
  forces are in metres. Verified by: a scratch test in `Physics2DJointTests` (kept as `MotorJoint2D_SpringReachesOffset`)
  that moves a box onto an offset target. Any surprise is written into this plan's Open questions before going on.
- [x] `MotorJoint2D` in `Physics/2D/Joints2D.cs`, after `WheelJoint2D`:
  - The exports with `Live` setters and `Range`s, and XML docs that give the units.
  - `CreateJoint` and `ApplyLive`, with unit conversion through `pixelsPerMeter` (px → m; force ÷ ppm; torque ÷ ppm²)
    and the "hertz 0 = off" rule.
  - The live frame A comes from the record's authored `FrameA` × the offsets. The record keeps the authored frame, so
    `Rebuild()` and re-creation still start from it.
  - `[EditorIcon("magnet", Family = EditorIconFamily.Physics)]`.

  Verified by: `just build` with 0 warnings, and the scenario tests below.
- [x] Debug draw (`DrawDebugShape`): the target as a cross at frame A × the offsets, and a line from body B's anchor to
  the target. Red when the anchor is more than 5 px from the target, as the other joints use when stretched. Verified
  by: a `DebugDrawTests` case for a `MotorJoint2D` (its lines are drawn under `DebugDrawJoints`).
- [x] Tests in `Physics2DJointTests`, each named after its proposal scenario:
  - `MotorJoint2D_SpringReachesOffset`, `MotorJoint2D_AngularOffset`, `MotorJoint2D_VelocityFriction`,
    `MotorJoint2D_OffsetIsLive`, `MotorJoint2D_SceneRoundTrip`.
  - `MotorJoint2D_HertzZeroTurnsSpringOff`: with hertz 0, a box away from its target stays where it is.
  - `MotorJoint2D_EnabledAndBreak`: `Enabled` false destroys and re-creates the joint, and `BreakForce` breaks it.

  Add a motor joint to the 2D joint allocation gate in `PhysicsAllocationTests`. Verified by: `just test`, with every
  named test passing and the gate at 0 B.
- [x] Editor: the `MotorJoint2D` inspector lists the groups and members, the scene tree row shows `magnet`, and the node
  picker lists only bodies. Check whether `JointEditorTests` has a per-type list (icons, pickers) and add the motor joint
  to it. Verified by: `just test` (editor tests). Then `just editor` on the Demo project: add a `MotorJoint2D`, look
  at its inspector and icon, and take a screenshot for the log.
- [x] Demo: `Joints2DBuild.AddPlatform`, called from `Physics2DScene`:
  - A 1 kg-class box platform joined to the world by a `MotorJoint2D` (gravity on, so the spring holds it up).
  - Code or a `Tween` moves its `LinearOffset` ±150 px back and forth, with two crates resting on it.
  - Reset in `Physics2DPanel` puts it back.
  - Regenerate `physics_2d.mscene` with `--write-scenes`.
  - Add `PhysicsJointsSceneTests.Platform_FollowsTarget`: the platform stays within 10 px of its target for 3 s, and a
    crate stays on it.

  Verified by: the Demo tests, and `just demo --scene Assets/Scenes/physics_2d.mscene` run and looked at.
- [x] Docs:
  - [physics-joints.md](../../docs/design/physics-joints.md): the 2D types table, Code, Tests, the icons list, and
    "Still to come" (now `Generic6DofJoint3D` and active ragdolls).
  - [demo.md](../../docs/design/demo.md): the `physics_2d` row.
  - The README feature row ("seven 2D joints") and the `physics_2d` Showcase caption.
  - The landing page wherever it lists the 2D joints.
  - Tick G9.10a in the proposal's task list.

  Verified by: `just docs-build`, `check.sh`, and `git grep "six 2D joints"` finding nothing.
- [x] Screenshots: `just demo-screenshots`, then inspect `docs/images/demo/physics_2d.png` (the platform is visible) and
  commit only the PNGs that changed meaningfully. Verified by: opening the PNG.
- [x] Gates: `just build`, the Release `-warnaserror` build, `just test`, `just test-render` (the goldens unchanged),
  `just format-check`, `just shaders-check`, `check.sh`. Verified by: each command's exit status, recorded in the log.
- [ ] Ship (`mf-ship`): the log in `memory/logs/`, and `current-state.md`. The state file still says A6 is "in review";
  fix that, and update Next steps to G9.10b. Then commit and open the PR, both on your go-ahead.

## Done when

- `MotorJoint2D` exists with exactly the proposal's members and defaults, and no Box2D call outside `Src/Physics`.
- The seven `MotorJoint2D_*` tests in this plan and `Platform_FollowsTarget` pass by name, and the 2D joint allocation
  gate still measures 0 B.
- `DebugDrawTests` has a motor joint case, and it passes.
- The Demo's `physics_2d` scene has the moving platform, and `physics_2d.png` shows it.
- Every gate in `.agents/workflow.md` passes, and the render-test goldens are unchanged.
- physics-joints.md, demo.md, the README and the landing page list the motor joint, and the proposal's G9.10a is ticked.

## Open questions

- None open. What the first step found (2026-11-03):
  - The spring pulls frame B onto frame A, and a maximum of 0 means no spring force. Forces and velocities are in
    metres, and the friction from `MaxVelocityForce` is exactly F/m (a probe's extra slowing was the bodies' default
    0.1 `LinearDamp`).
  - `LinearVelocity` is in **world axes**: it is neither body A's axes nor the joint frame's, which the proposal didn't
    say. The member's doc and physics-joints.md say so. Test: `AMotorJointsVelocityIsInWorldAxes`.
  - Tests use this file's sentence-style names, not the proposal's `MotorJoint2D_*` names. The log maps one to the
    other.
