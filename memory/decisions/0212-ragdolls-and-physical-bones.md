# ADR 0212 — Ragdoll3D, PhysicalBone3D and Create physical skeleton

- **Date:** 2026-10-27
- **Status:** accepted
- **Milestone:** G9 (G9.6, G9.8, G9.9)
- **Docs:** docs/design/physics-joints.md#ragdolls

## Context

With joints (ADR 0211) and skeletons (ADR 0207) in place, games need two kinds of ragdoll: rigid-part characters
(capsule mannequins, robots, chains of props) and skinned characters that animate and then fall. A physics pose write on
a parent teleports body children, so ragdoll bodies must be flat siblings. Godot 4.3 splits skeleton ragdolls into
`PhysicalBoneSimulator3D` and `PhysicalBone3D`; the G1c humanoid map that would give knee and elbow limits does not exist
yet.

## Decision

- **Holding kinematic is run-time state, not the saved `Mode`.** `RigidBody3D.HeldKinematic` (internal) overrides the
  body kind; the ragdoll and the simulator set it, so the scene keeps what was authored and editing never rewrites it.
  A held body is not interpolated: it follows its node every frame, and an interpolated (one step late) pose would make
  the next node move start from the lagging pose.
- **`Ragdoll3D`**: its direct `RigidBody3D` children are the parts (also parts added later). Not `Simulating`: held
  kinematic, following their nodes; `StartSimulation` makes them dynamic with the Jitter2 velocity of their last
  kinematic move; `StopSimulation` holds them where they are. Signals `SimulationStarted`/`SimulationStopped`.
- **Open question 7 (nested parts): flat**, as proposed.
- **Open question 8 (ragdoll layers): left to the game.** Instead of layer changes, a ragdoll's parts (and a
  simulator's bones) share an exclusion group (`CollisionObject3D.ExclusionGroup`, internal, read by the broad-phase
  filter): they never collide with each other, joined or not, whatever their layers. `Ragdoll3D.ExcludePartCollisions`
  turns it off.
- **`PhysicalBoneSimulator3D`** (child of a `Skeleton3D`) with `PhysicalBone3D` children (`BoneName`, `BodyOffset` =
  body relative to bone, `JointType` None/Pin/ConeTwist/Hinge/Slider, `JointOffset`, a "Joint" group of limits). On
  ready (not in edit mode) it places each body at its bone's pose × `BodyOffset`, joins it to the physical bone of the
  nearest ancestor bone with an internal joint node (no owner: never saved, not in the editor's tree) and flushes the
  space so the frames are captured at that pose. Held bones follow the animated pose in the fixed step (a kinematic
  move: they push and are hit). After the animation (process priority 1000), simulated bones' poses are written from
  their bodies' rendered poses, parents first, blended with the animated pose by `Influence`.
  `PhysicalBonesStartSimulation(names)` (empty = all) and `PhysicalBonesStopSimulation()`.
- **Create physical skeleton** is `PhysicalBoneSimulator3D.Build(skeleton)`, shared by the editor's Skeleton3D
  inspector button (one `AddNodeAction`, one undo step; hidden while a simulator exists) and the Demo: a capsule per
  bone at least 5 cm long, from the bone to its first child (radius 20 % of the length), and a 45°/30° cone-twist at the
  bone. **Every body keeps the default 1 kg** (Godot's choice): masses from capsule volume made a 0.2 kg pelvis carry
  9 kg thighs, and the joint chain stretched by up to 0.7 m; with equal masses the Demo character's joints hold within
  5 mm. Humanoid limits (hinged knees and elbows from G1c's `HumanoidLimits`) are not applied: generic limits only.

## Consequences

- The Demo's Animation 3D character (31 bones, 20 physical bones) ragdolls from any clip and stands up again; 50
  rigid ragdolls of 11 parts (500 joints) step in about 0.6 ms (Release, one thread), allocating nothing.
- Leaf bones (head tops, finger tips, toes) have no length and get no body.
- Active ragdolls (motors chasing the animation) and partial blending beyond `Influence` remain G9.10.
