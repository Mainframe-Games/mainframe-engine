# Physics joints and ragdolls

The current state of G9 ([milestones](../milestones.md#gameplay-toolkit-)): joint nodes over Jitter2 (3D) and Box2D
(2D) with limits, motors, springs and breaking; their debug draw and editor gizmo; rigid-part ragdolls
(`Ragdoll3D`); skeleton ragdolls (`PhysicalBone3D`, `PhysicalBoneSimulator3D`) and the editor's Create physical
skeleton. Decisions: [ADR 0201](../../memory/decisions/0201-2d-joints-on-box2d.md) (the first 2D joints),
[ADR 0211](../../memory/decisions/0211-3d-joints-on-jitter2.md) (3D joints, the other 2D joints, breaking, editor) and
[ADR 0212](../../memory/decisions/0212-ragdolls-and-physical-bones.md) (ragdolls). Still to come: limit handles in the
viewport (G9.7, after [G7.3](future/editor-viewport-tools.md)) and G9.10 (`Generic6DofJoint3D`, active ragdolls,
`MotorJoint2D`). Bodies, spaces and signals are in [Physics](physics.md).

![The Demo's rope bridge with a hinge selected in the editor: its axis, limit arc and angle, and its inspector with
Rebuild and Swap A/B](../images/editor-joints.png)

## Code

| File | Contents |
|---|---|
| [`Physics/3D/Joint3D.cs`](../../MainframeEngine/Src/Physics/3D/Joint3D.cs), [`Joints3D.cs`](../../MainframeEngine/Src/Physics/3D/Joints3D.cs) | `Joint3D` and the six 3D types |
| [`Physics/3D/PhysicsSpace3D.Joints.cs`](../../MainframeEngine/Src/Physics/3D/PhysicsSpace3D.Joints.cs) | `JointRecord3D`, creation and re-creation, constraint factories, exclusion, springs, breaking, debug draw |
| [`Physics/2D/Joints2D.cs`](../../MainframeEngine/Src/Physics/2D/Joints2D.cs), [`PhysicsSpace2D.Joints.cs`](../../MainframeEngine/Src/Physics/2D/PhysicsSpace2D.Joints.cs) | `Joint2D`, the six 2D types, `JointRecord2D` |
| [`Physics/Ragdoll/`](../../MainframeEngine/Src/Physics/Ragdoll/) | `Ragdoll3D`, `PhysicalBone3D`, `PhysicalBoneSimulator3D` |
| [`Editor/Src/Inspector/JointInspector.cs`](../../MainframeEngine.Editor/Src/Inspector/JointInspector.cs), [`Skeleton3DInspector.cs`](../../MainframeEngine.Editor/Src/Inspector/Skeleton3DInspector.cs) | Rebuild, Swap A/B; Create physical skeleton |

Every Jitter2 and Box2D call stays in `Src/Physics` (the pinned-version rule in [Physics → Libraries](physics.md#libraries)).

## The node model

Godot's: a joint is a node whose global pose gives the anchor and axes; `NodeA`/`NodeB` are paths to physics bodies
(the inspector's picker lists only `PhysicsBody3D`/`PhysicsBody2D`); an empty path is the static world (Jitter2's
`NullBody`, a shapeless static Box2D body per space). Both bases have:

| Member | Notes |
|---|---|
| `NodeA`, `NodeB` | Changing one re-creates the joint (re-capturing its frames) |
| `ExcludeNodesFromCollision` (3D) / `DisableCollision` (2D) | Default true: the joined bodies don't collide while the joint exists and is enabled |
| `Enabled` | 3D: the constraints are disabled and kept. 2D: the Box2D joint is destroyed and re-created (no enable flag). A node under `ProcessMode.Disabled` counts as off |
| `BreakForce`, `BreakTorque` | 0 = never. N and N·m in 3D, kg·px/s² and kg·px²/s² in 2D |
| `Broken` (signal), `IsBroken`, `Rebuild()` | `Rebuild()` re-resolves paths, re-captures frames from the current poses and clears `IsBroken` |
| `BodyA`, `BodyB`, `IsActive` | `IsActive`: created in the space (bodies present, not broken; in 3D, one body dynamic) |
| `GetAppliedForce()`, `GetAppliedTorque()`, `GetSeparation()` | The last step's force and torque (the breaking values once broken); the anchors' distance |
| `DrawDebug(lines, color)` | Draws the joint's shape; the editor draws the selected joint with it |

2D keeps ADR 0201's `ConstraintHertz`/`ConstraintDampingRatio` (Box2D's softness, live). 3D uses Jitter2's default
softness and bias (no exports).

### 3D types

Angles are exported in degrees with a radians property for code, like `CharacterBody3D.FloorMaxAngleDegrees`.

| Type | Jitter2 constraints | Axis | Members (all live unless noted) |
|---|---|---|---|
| `PinJoint3D` | `BallSocket` | — | — |
| `HingeJoint3D` | `BallSocket`, `HingeAngle`, `AngularMotor` | local Z | `UseLimit`, `LowerAngleDegrees` (−90), `UpperAngleDegrees` (90); `MotorEnabled`, `MotorTargetVelocity` (rad/s), `MotorMaxTorque` (100 N·m); `SpringEnabled`, `SpringFrequency` (2 Hz), `SpringDampingRatio` (0.5), `SpringTargetAngleDegrees`; `GetAngle()` |
| `SliderJoint3D` | `PointOnLine`, `FixedAngle` (or `HingeAngle` with `AllowTwist`), `LinearMotor` | local X | `AllowTwist` (re-creates); `UseLimit`, `LowerDistance` (−1 m), `UpperDistance` (1 m); motor (m/s, `MotorMaxForce` 100 N); spring (`SpringTargetPosition`); `GetPosition()` |
| `ConeTwistJoint3D` | `BallSocket`, `ConeLimit`, `TwistAngle` | local X | `SwingSpanDegrees` (45), `TwistSpanDegrees` (180); 180° switches that limit off; `GetSwingAngle()`, `GetTwistAngle()` |
| `DistanceJoint3D` | `DistanceLimit` | origin → `AnchorB` | `AnchorB` (local, default (0, −1, 0); re-creates); `MinLength` (0 = slack), `MaxLength` (0 = the authored distance for a rope, no maximum for a spring); `SpringFrequency` (0 = no spring), `SpringDampingRatio`, `RestLength` (0 = authored); `GetLength()` |
| `WeldJoint3D` | `BallSocket`, `FixedAngle` | — | — (usually with `BreakForce`) |

Angles are body B relative to A, right-handed about the axis, measured from the authored pose. Motors always exist:
off is a maximum force of 0, so toggling one never re-creates the joint.

**Springs** are frequency + damping ratio everywhere. Jitter2 has no angular or prismatic spring, so hinge, slider and
distance springs are an engine PD spring applied before each step: the velocity an implicit Euler step of
x'' = −ω²x − 2ζωx' would reach, turned into equal and opposite torques (forces at the anchors), with the effective
inertia about the joint's anchor. It is stable for any stiffness and independent of the masses; the frequency is
clamped to a quarter of the tick rate. Jitter2's `SpringConstraint` is not used: it ignores body rotation.

### 2D types

ADR 0201's `PinJoint2D` (revolute: radians limits, motor, now also `SpringEnabled`, `SpringHertz`,
`SpringDampingRatio`, `SpringTargetAngle`, `GetAngle()`) and `DampedSpringJoint2D` (distance joint: `Length`,
`RestLength`, `SpringHertz`, `SpringDampingRatio`) are unchanged, joined by:

| Type | Box2D | Members (pixels; all live unless noted) |
|---|---|---|
| `SliderJoint2D` | prismatic, along the joint's X | `UseLimit`, `LowerTranslation`, `UpperTranslation`; motor (px/s, `MotorMaxForce`); spring (`SpringHertz`, `SpringDampingRatio`, `SpringTargetTranslation`); `GetTranslation()` |
| `GrooveJoint2D` | wheel joint, spring off, limit on; axis = the joint's +Y | `Length` (50), `InitialOffset` (25): body A holds the groove, B's anchor starts `InitialOffset` along it (both re-create) |
| `WeldJoint2D` | weld | `LinearHertz`, `AngularHertz` (0 = rigid), `LinearDampingRatio`, `AngularDampingRatio` |
| `WheelJoint2D` | wheel, along the joint's X (turn it −90° for a car's suspension) | `SpringEnabled` (true), `SpringHertz` (1), `SpringDampingRatio` (0.7); `UseLimit`, `LowerTranslation`, `UpperTranslation`; motor (rad/s, `MotorMaxTorque`) |

Box2D's prismatic and wheel axis is frame A's X, so the groove's frames are turned a quarter turn (`FrameAngle`).

## In the space

- **Records and deferred creation.** A joint registers a record with its viewport's space on entering the tree. The
  space creates it at its flush (before a step or a query), after the bodies' shapes and masses, so tree order and the
  order paths are set in don't matter. Paths resolve with `GetNodeOrNull` from the joint. A path to a non-body or to
  another world's body, or both paths on one body, logs one error; both paths empty is a silent inactive joint.
- **Frames** are the joint's pose (and a type's second anchor) in each body's local space, captured as soon as both
  bodies are present and kept while the paths stay the same. 3D constraints are initialised and then their data is set
  from the frames (`LocalAnchor1/2`, axes, `Q0` = the authored relative rotation), so a re-created joint joins its
  bodies as authored and measures its limits from the authored pose wherever the bodies are. Moving a joint node after
  creation does nothing (Godot); `Rebuild()` re-captures.
- **Bodies leaving, kinematic bodies.** A body leaving the space (freed, removed, disabled) destroys its joints first;
  they wait and come back when it returns. Jitter2 keeps no constraint between two non-dynamic bodies: such a joint is
  not created (`IsActive` false) until one body is dynamic again, and a body's kind change re-creates its joints.
  Box2D keeps joints across type changes.
- **Collision exclusion** (3D): each body record holds a copy-on-write `Excluded` array the broad-phase filter reads on
  Jitter2's worker threads, replaced between steps; turning exclusion on clears the pair's contacts. Ragdolls use
  exclusion groups instead (below). 2D sets Box2D's `collideConnected`.
- **Breaking.** 3D: after the step, joints with a threshold (a `_breakable` list) sum their last substep's linear
  impulses (socket, point-on-line, distance, linear motor) and angular ones (hinge, fixed, cone, twist) and divide by the
  substep length. 2D: Box2D's thresholds and `b2World_GetJointEvents`. A breaking joint is destroyed, `IsBroken` set, and
  `Broken` raised after the body signals, in joint creation order; handlers may free nodes (freed joints are skipped).
- **Edit mode** creates records like bodies but never steps, so joints draw grey at their nodes.

## Debug draw and editor

`PhysicsServer3D/2D.DebugDrawEnabled` draws shapes and joints; `DebugDrawJoints` draws joints only (the Demo panels'
Joints toggle). Each type draws its anchors (a line between them when they separate) and its shape: a hinge's axis,
limit arc and current angle; a slider's track, limit ticks and position; a cone-twist's swing cone and twist arc; a
spring's zigzag; a weld's box; the 2D equivalents. Amber, red when stretched (more than 5 cm/5 px, or past a distance
joint's maximum), grey when pending, disabled or broken. Lines follow the bodies' interpolated poses.

The editor ([Editor](editor.md)):

- **Selected joint:** `DrawDebug` in the selection colour on top, and a line to each body its paths name (a red marker
  for a path that names none).
- **Warnings** on the scene tree row: both paths empty, a path to nothing, to a non-body, or both to one body.
- **Inspector:** Rebuild (`refresh`) and Swap A/B (`arrows-right-left`, one undo step) icon buttons above the rows.
- **Icons** (Physics family): `link` (bases), `point` (pins), `door` (hinge), `arrows-right-left` (sliders), `ripple`
  (cone-twist), `ruler-2` (distance), `lock` (welds), `arrows-up-down` (groove), `circle` (wheel), `bone` (ragdolls,
  physical bones), `sitemap` (simulator).
- **Dev overlay:** the Physics panel shows joints (active) and joints broken per second.

## Ragdolls

### `Ragdoll3D`

A node whose **direct** `RigidBody3D` children are the parts, joined by ordinary joint nodes under it. Parts are flat
siblings because a parent's physics pose write teleports body children. Visuals hang under each part.

| Member | Notes |
|---|---|
| `Simulating` | Off: the parts are held kinematic and follow their nodes (an `AnimationPlayer` or code moves them; they push what they touch). Saved |
| `ExcludePartCollisions` | Default true: the parts never collide with each other, joined or not |
| `StartSimulation()` | Parts dynamic, each with the velocity of its last kinematic move; `SimulationStarted` |
| `StopSimulation()` | Parts held kinematic where they are; `SimulationStopped` |
| `ApplyImpulse(part, impulse, position)`, `GetParts(list)` | |

Holding is run-time state (`RigidBody3D.HeldKinematic`, internal): the parts' saved `Mode` is untouched. Held bodies are
not interpolated, so moving their nodes every frame becomes exact kinematic velocities. Joints between two held parts
wait and come back, at their authored frames, when the simulation starts. Exclusion is a group id shared by the parts
(`CollisionObject3D.ExclusionGroup`), checked by the broad-phase filter.

### `PhysicalBone3D` and `PhysicalBoneSimulator3D`

Godot 4.3's structure: a `PhysicalBoneSimulator3D` is a child of a `Skeleton3D` and its `PhysicalBone3D` children
(rigid bodies) drive bones.

| `PhysicalBone3D` | Notes |
|---|---|
| `BoneName` | The bone it drives |
| `BodyOffset` | The body relative to the bone |
| `JointType` | `None`, `Pin`, `ConeTwist` (default), `Hinge`, `Slider`: the joint to the physical bone of the nearest ancestor bone |
| `JointOffset` | The joint frame relative to the body |
| "Joint" group | `SwingSpanDegrees` (45), `TwistSpanDegrees` (30), `LowerAngleDegrees`/`UpperAngleDegrees`, `LowerDistance`/`UpperDistance`: live |

| `PhysicalBoneSimulator3D` | Notes |
|---|---|
| `Influence` | 0–1: how much of a simulated bone's pose comes from its body (the rest from the animation) |
| `PhysicalBonesStartSimulation(names)` | Empty: every bone. Each keeps its last kinematic velocity |
| `PhysicalBonesStopSimulation()`, `IsSimulating`, `PhysicalBones` | |
| `Build(skeleton)` | A physical skeleton for the skeleton (see below), not added |

On ready (not in the editor's edit mode) the simulator puts each body at its bone's pose × `BodyOffset`, creates each
joint as an internal child node (no owner: not saved, not in the editor's tree) and flushes the space, so joint limits
are measured from the pose the skeleton had then. Held bodies follow the animated bones in the fixed step (kinematic
moves: they push and are hit by rays). Simulated bones' poses are written in `OnProcess` at process priority 1000 —
after `AnimationPlayer`, before skinning — from their bodies' rendered poses, parents first, blended by `Influence`.
The bodies of one simulator never collide with each other.

![Create physical skeleton on the Demo's character: capsules on its limbs](../images/editor-physical-skeleton.png)

**Create physical skeleton** is the Skeleton3D inspector's `bone` button (hidden once the skeleton has a simulator): it
adds `PhysicalBoneSimulator3D.Build(skeleton)` as one undo step. `Build` makes a body per bone at least 5 cm long: a
capsule from the bone to its first child, radius 20 % of the length, centred halfway with its axis along the bone, and
a 45°/30° cone-twist at the bone with its twist axis along the bone. Every body keeps the default 1 kg: equal masses
keep a chain of joints stiff (ADR 0212). The limits are generic; hinged knees and elbows from a humanoid map wait for
G1c's `HumanoidMap`/`HumanoidLimits` ([Animation authoring](future/animation-authoring.md)).

## Networking and determinism

Joints are server-side state like bodies: the server simulates and replicates poses through the game's `[Replicated]`
members; clients may run cosmetic ragdolls. With `PhysicsSettings3D.Deterministic` and one thread, joint scenes
reproduce on one machine and build (the `physics-joints` render test relies on it). Physics is single precision and not
a cross-platform lockstep ([ADR 0020](../../memory/decisions/0020-physics-precision-and-determinism.md)).

## Performance and allocation

Joint records, constraint objects and exclusion arrays are allocated when joints are created; per step, springs,
breaking, readback and debug draw iterate pre-sized lists. A 100-link pinned chain steps in about 57 µs and 50 rigid
ragdolls of 11 parts (500 joints) in about 0.6 ms (Release, one thread, M-series Mac; `JointBenchmarks`), allocating
nothing.

## Tests

- **Unit** ([`Tests/MainframeEngine.Tests/Physics/`](../../Tests/MainframeEngine.Tests/Physics/)): `Joints3DTests`
  (pendulum period within 2 %, anchors within 1 cm under load, hinge limits, motor speed and torque, slider range and
  `AllowTwist`, cone and twist spans, rope slack, spring period, PD springs, a welded cantilever; joints before their
  bodies, bodies leaving and returning, disabled joints and bodies, kinematic pairs, invalid paths logged once,
  collision exclusion; scene round trips), `JointBreakTests`, `Physics2DJointTests` (every 2D type, `Enabled`, pin
  springs), `Ragdoll3DTests`, `PhysicalBonesTests`, `DebugDrawTests` (`DebugDrawJoints`), and allocation gates
  (`JointAllocationTests`: 320 3D joints; the 2D gate has one joint of each type).
- **Editor** (`JointEditorTests`): warnings, body-only pickers, Swap A/B undo, Rebuild, Create physical skeleton as one
  undo step. `just qa-editor` selects the bridge hinge and creates the character's physical skeleton.
- **Render test** `physics-joints`: a hinged bridge, a cone-twist mannequin and a broken weld with the joint debug
  draw, frame 90 (moltenvk golden; lavapipe still to record).
- **Demo** (`PhysicsJointsSceneTests`): the bridge holds, the sign's weld breaks and resets, the mannequin falls and
  stands, the chain hangs, the car drives and turns, the skinned character ragdolls with its joints holding.

## Differences from the plan

- **Constraints, not Jitter2's composite `Joint`s**, so frames and rest rotations can be written directly.
- **The distance spring is the engine's PD spring**, not Jitter2's `SpringConstraint` (it ignores body rotation).
- **PD springs are implicit**, with the inertia about the anchor; the frequency clamp is kept.
- **A breaking 3D joint is destroyed** (as in 2D), not disabled.
- **Frames are captured as soon as both bodies exist**, not at the first creation, so limits of joints between
  kinematic bodies are measured from the authored pose.
- **Ragdoll parts and physical bones share an exclusion group** instead of pairwise exclusion; `Ragdoll3D` holds parts
  through internal state rather than writing their `Mode`.
- **Physical-bone joints are internal child nodes** of the simulator (unowned), reusing the joint machinery.
- **`Build` gives every body 1 kg** instead of a mass from its volume.
- **No new icon SVGs**: joint icons reuse the editor atlas (no `cone`, `steering-wheel`, `accessible`).
- **The 2D pin keeps ADR 0201's radians names** (`AngularLimitLower`…) rather than `LowerAngleDegrees`.
- **The showcase render-allocation scene has no joint chain**: it would change its goldens; the unit gate covers joints.
- **Not yet:** limit handles (G9.7), `Generic6DofJoint3D`, active ragdolls and `MotorJoint2D` (G9.10), humanoid limits.

## Related

[Physics](physics.md) · [Animation](animation.md) · [Editor](editor.md) · [Demo](demo.md) · [Testing](testing.md) ·
[Scene graph and nodes](scene-graph-and-nodes.md) · [Networking](networking.md)
