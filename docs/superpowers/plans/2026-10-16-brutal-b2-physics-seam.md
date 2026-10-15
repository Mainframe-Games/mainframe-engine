# BRUTAL B2: Physics Seam Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move every Jitter2 call out of the engine's 3D physics code into `Jitter2Backend` behind an internal abstract
`PhysicsBackend3D` (one per `PhysicsSpace3D`), with neutral shape descriptions, a selectable `physics.3d.backend`
(Jitter2 only; Jolt arrives in B3) and per-backend settings groups, with **no behaviour change**: the same Jitter2 calls,
in the same order, with the same values.

**Architecture:** `PhysicsSpace3D` keeps all engine behaviour (records, dirty tracking and flush, membership lists,
sequence numbers, interpolation, user moves, the layer rule `Collides`, signal diffing/order/dispatch/removal exits,
damping conversion, gravity-scale semantics, the concave-on-dynamic error, disabled-node removal, debug draw and the
`MoveAndSlide` primitives' engine logic) and talks to the library only through `PhysicsBackend3D` with `BackendBody`
handles, `ShapeDescription3D`/`BodyDescription3D`/`BodyProperties3D` in and `BackendHit3D`/`BackendContact3D`/
`BackendShapeRef` out, into lists the space owns. Built expand → switch → contract: the backend is written and tested
next to the old code (Tasks 6–8), the space switches in one task guarded by a bit-exact trace recorded on the
unmodified code (Tasks 2, 9), then the old Jitter2 code is deleted and a source rule test locks it (Task 10).

**Tech Stack:** C# 13 / .NET 10, Jitter2 2.9.0 (pinned), xUnit v3, BenchmarkDotNet, RmlUi (editor dialog), `just`.

**Spec:** docs/design/future/brutal-backend.md (B2) · ADR 0192

## Global Constraints

- Branch: `brutal/b2-physics-seam`, cut from `main` **after** the BRUTAL spec (`docs/design/future/brutal-backend.md`, `memory/decisions/0192-brutal-backend-tech.md`, the `physics.md`/`milestones.md`/`physics-joints.md`/`rendering-backend-abstraction.md` edits and `docs/images/brutal-splash-sequence.svg`) is on `main`, merged through its own docs-only PR (with ADR 0192 and these plans; `main` is PR-only). If `docs/design/future/brutal-backend.md` is missing on `main`, stop and ask the user.
- No behaviour change: every Jitter2 call happens in the same order with the same arguments as on `main`; the bit-exact trace of Task 2 must match after every later task.
- Zero managed allocations in steady state: `PhysicsAllocationTests.SteadyState3DPhysicsDoesNotAllocate(false)` = 0 B, `(true)` = 0 B outside Jitter2's step; `Physics2DAllocationTests` unchanged.
- The seam is an internal abstract class, main-thread only, called between steps, no closures, no boxing of handles, no per-call allocation; every list it fills is owned and cleared by the space.
- Build: 0 warnings (warnings are errors), Debug and `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`.
- Jitter2 stays `2.9.0` in `Directory.Packages.props`; no package is added, removed or re-versioned (BRUTAL packages are B1's).
- Library code location: Jitter2 only in `MainframeEngine/Src/Physics/3D/Jitter/`, Box2D.NET only in `MainframeEngine/Src/Physics/2D/`, `Brutal.JoltApi` only in `MainframeEngine/Src/Physics/3D/Jolt/` (empty in B2).
- Public API: nothing removed or changed. Additions only: `PhysicsBackendKind`, `PhysicsSettings3D.Backend`/`.Jitter2`/`.Jolt`, `Jitter2PhysicsSettings`, `JoltPhysicsSettings`, `PhysicsSpace3D.BackendKind`. `PhysicsSettings3D.SolverIterations`/`RelaxationIterations` stay as forwarders to `Jitter2`.
- Nodes (`CollisionObject3D`, `RigidBody3D`, `CharacterBody3D`, `Area3D`, `CollisionShape3D`, `RayCast3D`), scenes, `Terrain3D` and game code do not change.
- `project.mfproj`: `ProjectSettingsFormat.Current` goes up by exactly one with exactly one new `ProjectMigration`; shipped steps are never edited. On `main` at `25eb45e6` `Current` is 3 (this plan writes 3 → 4); if B1 merged first and `Current` is 4, use 4 → 5 everywhere this plan says 3 → 4.
- Selecting `physics.3d.backend: "jolt"` in B2 logs once per server `[Physics] Jolt is not available on <rid> (the Jolt backend arrives in B3); using Jitter2.` and creates Jitter2 spaces.
- Physics test command after every code step: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics"`.
- Trace gate command (from Task 2 on): `MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"` (it must report 1 passed, not skipped).
- Commit after each task, locally only (no push; the user pushes once at the end). Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Final gates (Task 12): `just build`; `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`; `just test`; `just test-render` (display awake: `caffeinate -u -t 600 &`); `just format-check`; `just shaders-check`; `just template-smoke`; `just bench '*PhysicsBenchmarks*'` on a quiet machine (within `baseline.json`'s 10 %); the trace gate.
- Accepted difference (the only one): when one body's rebuild has both a backend-rejected shape (degenerate hull, zero scale) and a concave shape on a dynamic body, the two `Log.Error` lines may swap order.

## Review Focus

1. **Shape caches keyed wrong, so an edit does not rebuild.** Expected: any property set that changes geometry bumps `Shape3D.ChangeCount` (also reassigning the same array, as `Terrain3D` does with `shape.Faces = BuildFaces(chunk, shape.Faces)`), bodies rebuild at the next flush, and the Jitter2 query-shape cache (per space, keyed by resource with a weak table, validated by `ChangeCount`) serves the new shape. Tests: `PhysicsSeamTests.EditingASharedShapeRebuildsItsBodiesInEverySpace` (Task 1), `ShapeDescription3DTests.ChangeCountFollowsEveryGeometryEdit` (Task 7), `Jitter2BackendTests.QueryShapesAreCachedUntilTheResourceChangesAndDoNotAllocate` (Task 8).
2. **Handles crossing spaces (resources and nodes shared by two spaces).** Handle indices are per backend, so body 1 of a sub-viewport's space is not body 1 of the main space. Expected: `exclude:` of a node in another space excludes nothing; `SettleBody` ignores a collider of another space; a resource used in two spaces rebuilds in both. Tests: `PhysicsSeamTests.ExcludingABodyOfAnotherSpaceExcludesNothingHere` and `EditingASharedShapeRebuildsItsBodiesInEverySpace` (Task 1).
3. **A body changing kind mid-step (inside a signal handler).** Expected: `Mode` set in a `BodyEntered` handler applies from the next step (kinematic: moves by its velocity, ignores gravity, its gravity-scale force stops because the backend's gravity list follows `SetProperties`), and switching back restores dynamic behaviour. Test: `PhysicsSeamTests.ChangingABodysModeInsideASignalHandlerAppliesAtTheNextStep` (Task 1).
4. **Queries and handle reuse after a removal during a signal handler.** Expected: a body freed inside a handler is gone from every query issued later in that handler; a body created right after reuses the freed backend index but never inherits the old body's overlaps, exits or enters. Tests: `PhysicsSeamTests.QueriesInASignalHandlerNoLongerSeeABodyFreedEarlierInIt` and `ABodyAddedRightAfterAnotherIsFreedDoesNotInheritItsSignals` (Task 1).
5. **Sub-viewport space disposal order.** Expected: when a sub-viewport leaves the tree its bodies leave first (records removed, `Space` null), the server releases the space at its next `Process`, `PhysicsSpace3D.Dispose` detaches records before disposing the backend, the released world's `DirectSpaceState` finds nothing, and a new sub-viewport gets a working space. Tests: `PhysicsSeamTests.FreeingASubViewportReleasesItsSpaceAndItsQueriesFindNothing` (Task 1), `Jitter2BackendTests.DisposeIsIdempotent` (Task 8).

---

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `MainframeEngine/Src/Physics/3D/Backend/PhysicsBackend3D.cs` | create | the seam: internal abstract class, `Create(kind, settings)` |
| `MainframeEngine/Src/Physics/3D/Backend/ShapeDescription3D.cs` | create | `ShapeKind3D`, `ShapeDescription3D` |
| `MainframeEngine/Src/Physics/3D/Backend/BackendResults3D.cs` | create | `BackendBody`, `BodyDescription3D`, `BodyProperties3D`, `BackendHit3D`, `BackendContact3D`, `BackendShapeRef`, `ShapeError3D` |
| `MainframeEngine/Src/Physics/3D/Jitter/Jitter2Backend.cs` | create | `JitterBody` + `Jitter2Backend`: every Jitter2 call of the 3D server |
| `MainframeEngine/Src/Physics/3D/Jitter/JitterLogMute.cs` | create (moved) | `JitterLog` (the Jitter2 → `Log` listener, from `PhysicsServer3D.cs:20-37`) and `JitterLogMute` (from `PhysicsSpace3D.cs:1250-1267`) |
| `MainframeEngine/Src/Physics/3D/Jitter/JitterHull.cs` | create (moved) | convex hull tessellation for debug draw (from `Shapes3D.cs:235-246`) |
| `MainframeEngine/Src/Physics/3D/Jitter/JitterMath.cs` | `git mv` from `3D/JitterMath.cs` | `System.Numerics` ↔ Jitter2 conversions |
| `MainframeEngine/Src/Physics/3D/Jitter/SphereContactFilter.cs` | `git mv` from `3D/SphereContactFilter.cs` | ADR 0134 sphere contacts |
| `MainframeEngine/Src/Physics/3D/PhysicsSpace3D.cs` | modify | `BodyRecord3D` with a `BackendBody` handle; engine logic over the backend; `Collides`, `BakeShapeTransform`, `AngularVelocityBetween`, `MaterialOf` |
| `MainframeEngine/Src/Physics/3D/PhysicsDirectSpaceState3D.cs` | modify | queries through the backend, results mapped to nodes |
| `MainframeEngine/Src/Physics/3D/Shapes3D.cs` | modify | `ChangeCount`, `Describe`/`DescribeConvex`/`DescribeQuery`; no Jitter2 types |
| `MainframeEngine/Src/Physics/3D/PhysicsServer3D.cs` | modify | backend resolution (Jolt → Jitter2 with one warning), no Jitter2 listener |
| `MainframeEngine/Src/Physics/PhysicsSettings.cs` | modify | `PhysicsBackendKind`, `Backend`, `Jitter2PhysicsSettings`, `JoltPhysicsSettings`, forwarders |
| `MainframeEngine/Src/Project/ProjectSettingsFormat.cs` | modify | `physics.3d.backend`/`jitter2`/`jolt` read and write; migration 3 → 4 |
| `MainframeEngine.Editor/Src/Projects/ProjectSettingsModel.cs` | modify | `ProjectSetting.Visible`; backend choice and the two groups |
| `MainframeEngine.Editor/Src/UI/ProjectSettingsDialog.cs` | modify | hide settings whose `Visible` is false |
| `Templates/MainframeEngine.Templates/content/mfgame/project.mfproj` | modify | `"format": 4` |
| `Tests/MainframeEngine.Tests/Physics/PhysicsSeamTests.cs` | create | characterization + review-focus tests |
| `Tests/MainframeEngine.Tests/Physics/PhysicsTraceTests.cs` | create | the bit-exact trace gate |
| `Tests/MainframeEngine.Tests/Physics/Traces/jitter2.<rid>.trace` | create (recorded) | the trace of the unmodified code |
| `Tests/MainframeEngine.Tests/Physics/PhysicsLayerRuleTests.cs` | create | `Collides` |
| `Tests/MainframeEngine.Tests/Physics/ShapeDescription3DTests.cs` | create | one test per shape kind, scale baking, `ChangeCount` |
| `Tests/MainframeEngine.Tests/Physics/Jitter2BackendTests.cs` | create | the backend alone |
| `Tests/MainframeEngine.Tests/Physics/BackendSourceRulesTests.cs` | create | library namespaces stay in their folders |
| `Tests/MainframeEngine.Tests/Physics/PhysicsBackendSelectionTests.cs` | create | default backend, Jolt fallback |
| `Tests/MainframeEngine.Tests/Physics/PhysicsSettingsTests.cs` | modify | new settings validation |
| `Tests/MainframeEngine.Tests/Project/ProjectSettingsTests.cs` | modify | format literals, new keys, migration |
| `Tests/MainframeEngine.Editor.Tests/Projects/ProjectWorkflowTests.cs` | modify | new keys, group visibility |
| `CLAUDE.md`, `docs/design/physics.md`, `docs/design/project-and-gamehost.md`, `docs/design/future/brutal-backend.md`, `docs/milestones.md`, `memory/context/progress-log.md` | modify | docs |

### The contract (B3's plan consumes this exactly)

`MainframeEngine/Src/Physics/3D/Backend/BackendResults3D.cs`:

```csharp
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A body in a <see cref="PhysicsBackend3D"/>: an index into the backend's table (indices start at 1 and are reused after
/// <see cref="PhysicsBackend3D.DestroyBody"/>). <c>default</c> is <see cref="None"/>. Handles are per backend: never use one
/// with another space's backend.
/// </summary>
internal readonly record struct BackendBody(int Index)
{
    public static BackendBody None => default;

    public bool IsNone => Index == 0;
}

/// <summary>What a backend needs to create a body (shapes come separately, through <see cref="PhysicsBackend3D.SetShapes"/>).</summary>
internal readonly record struct BodyDescription3D(
    PhysicsBodyKind Kind,
    Vector3 Position,
    Quaternion Rotation,
    uint Layer,
    uint Mask,
    BodyProperties3D Properties,
    float Friction,
    float Bounce);

/// <summary>
/// Simulation properties of a body. <see cref="Simulated"/> is true only for a dynamic <see cref="RigidBody3D"/>; every
/// other body gets <c>default</c> (no gravity, no axis locks: kinematic moves must reach their target).
/// </summary>
/// <param name="LinearDampingPerStep">Velocity removed per step, 0..1 (the engine converts the node's per-second damping).</param>
/// <param name="AngularDampingPerStep">Angular velocity removed per step, 0..1.</param>
/// <param name="GravityScale">Multiplier on the space's gravity (Godot semantics; 0 floats).</param>
internal readonly record struct BodyProperties3D(
    bool Simulated,
    float LinearDampingPerStep,
    float AngularDampingPerStep,
    float GravityScale,
    AxisLock AxisLocks,
    bool Ccd,
    bool CanSleep);

/// <summary>
/// A query or sweep hit. <see cref="ShapeIndex"/> is the index of the hit shape's description in the last
/// <see cref="PhysicsBackend3D.SetShapes"/> of <see cref="Body"/> (−1 when unknown). <see cref="Normal"/> is the hit
/// surface's normal facing the query (zero when the query starts inside); <see cref="Position"/> is on the hit body.
/// </summary>
internal struct BackendHit3D
{
    public BackendBody Body;
    public int ShapeIndex;
    public Vector3 Position;
    public Vector3 Normal;
    public float Fraction;
}

/// <summary>
/// The signed distance between one shape of a body and one nearby shape (<see cref="PhysicsBackend3D.Penetration"/>):
/// negative when they overlap. <see cref="Normal"/> points from the body's shape towards the other shape (any length; zero
/// when unknown); <see cref="Point"/> is the closest point on the other shape.
/// </summary>
internal readonly record struct BackendContact3D(float Distance, Vector3 Normal, Vector3 Point);

/// <summary>One shape of another body found near a body (<see cref="PhysicsBackend3D.CollectNearbyShapes"/>); <see cref="Shape"/> is backend-defined.</summary>
internal readonly record struct BackendShapeRef(BackendBody Body, int Shape);

/// <summary>A shape the backend could not build (<see cref="ShapeIndex"/> into the descriptions), or −1: the mass properties.</summary>
internal readonly record struct ShapeError3D(int ShapeIndex, string Message);
```

`MainframeEngine/Src/Physics/3D/Backend/ShapeDescription3D.cs`:

```csharp
using System.Numerics;

namespace MainframeEngine;

/// <summary>The kinds of <see cref="ShapeDescription3D"/>, one per <see cref="Shape3D"/> resource type.</summary>
internal enum ShapeKind3D : byte
{
    Box,
    Sphere,
    Capsule,
    Cylinder,
    ConvexHull,
    ConcaveMesh,
    HeightMap,
}

/// <summary>
/// A library-neutral shape: what <see cref="Shape3D.Describe"/> hands to a <see cref="PhysicsBackend3D"/>. Sizes are the
/// resource's own; <see cref="Linear"/> (rotation × scale, the body's scale baked in) and <see cref="Translation"/> place it
/// in its body's frame. <see cref="Points"/> is the resource's array (never copied): the hull points, or 3 vertices per
/// triangle for <see cref="ShapeKind3D.ConcaveMesh"/> and <see cref="ShapeKind3D.HeightMap"/> (the height map's triangle
/// list; <see cref="Resource"/> has its samples). Backends cache native shapes by (<see cref="Resource"/>,
/// <see cref="ChangeCount"/>, baked scale).
/// </summary>
internal readonly record struct ShapeDescription3D
{
    public ShapeKind3D Kind { get; init; }

    public Shape3D Resource { get; init; }

    /// <summary><see cref="Shape3D.ChangeCount"/> when described.</summary>
    public int ChangeCount { get; init; }

    /// <summary>Box: full extents.</summary>
    public Vector3 Size { get; init; }

    /// <summary>Sphere, capsule, cylinder.</summary>
    public float Radius { get; init; }

    /// <summary>Capsule (caps included) and cylinder: total height.</summary>
    public float Height { get; init; }

    public Vector3[]? Points { get; init; }

    public Basis Linear { get; init; }

    public Vector3 Translation { get; init; }

    /// <summary>The capsule's cylindrical part (as <see cref="CapsuleShape3D.MidHeight"/>).</summary>
    public float MidHeight => MathF.Max(0, Height - 2 * Radius);
}
```

`MainframeEngine/Src/Physics/3D/Backend/PhysicsBackend3D.cs`:

```csharp
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The 3D physics library behind one <see cref="PhysicsSpace3D"/> (ADR 0192, B2). The space owns the engine's behaviour
/// (records, dirty tracking, interpolation, user moves, the layer rule, signals, debug draw, MoveAndSlide); a backend
/// owns the library's world, bodies, shapes and queries. Abstract class, not an interface: sealed implementations, no
/// boxed handles. Every member is main-thread only, allocation-free once warm and called only between steps; the
/// lists passed in are owned by the space (members append to them; the space clears them).
/// </summary>
internal abstract class PhysicsBackend3D : IDisposable
{
    /// <summary>The backend for a new space; <paramref name="kind"/> is what the server resolved (B2: Jitter2 only).</summary>
    public static PhysicsBackend3D Create(PhysicsBackendKind kind, PhysicsSettings3D settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return kind switch
        {
            PhysicsBackendKind.Jitter2 => new Jitter2Backend(settings),
            _ => throw new NotSupportedException($"The {kind} 3D physics backend is not part of this engine build yet."),
        };
    }

    // ---- world ---------------------------------------------------------------------------------------------------

    public abstract PhysicsBackendKind Kind { get; }

    /// <summary>Gravity in m/s².</summary>
    public abstract Vector3 Gravity { get; set; }

    /// <summary>Bodies currently awake.</summary>
    public abstract int ActiveBodyCount { get; }

    /// <summary>Bytes the library allocated on this thread inside its steps while <see cref="PhysicsSpace3D.MeasureLibraryAllocations"/> was set.</summary>
    public long LibraryAllocatedBytes { get; protected set; }

    /// <summary>One fixed step of <paramref name="dt"/> seconds (sub-steps are the backend's).</summary>
    public abstract void Step(float dt);

    public abstract void Dispose();

    // ---- bodies --------------------------------------------------------------------------------------------------

    /// <summary>A body without shapes (the space adds them with <see cref="SetShapes"/> at its next flush). Areas detect, never collide.</summary>
    public abstract BackendBody CreateBody(in BodyDescription3D desc);

    /// <summary>Removes the body now; its handle may be reused by the next <see cref="CreateBody"/>.</summary>
    public abstract void DestroyBody(BackendBody body);

    /// <summary>Static, kinematic, dynamic or character (never to or from <see cref="PhysicsBodyKind.Area"/>).</summary>
    public abstract void SetKind(BackendBody body, PhysicsBodyKind kind);

    /// <summary>
    /// Replaces the body's shapes. <paramref name="dynamicMass"/> is the mass (kg) of a dynamic body, inertia from the shapes;
    /// 0 for every other body. Shapes that cannot be built are skipped and reported in <paramref name="errors"/> (by
    /// description index; −1 when the mass properties failed and a unit inertia was used). Wakes a non-static body.
    /// </summary>
    public abstract void SetShapes(BackendBody body, ReadOnlySpan<ShapeDescription3D> shapes, float dynamicMass, List<ShapeError3D> errors);

    /// <summary>Layer and mask (what <see cref="PhysicsSpace3D.Collides"/> reads; area-ness comes from the kind).</summary>
    public abstract void SetFilter(BackendBody body, uint layer, uint mask);

    public abstract void SetProperties(BackendBody body, in BodyProperties3D props);

    public abstract void SetMaterial(BackendBody body, float friction, float bounce);

    // ---- state ---------------------------------------------------------------------------------------------------

    public abstract void GetPose(BackendBody body, out Vector3 position, out Quaternion rotation);

    /// <summary>Moves the body there (static and area moves, dynamic user moves); does not wake it.</summary>
    public abstract void SetPose(BackendBody body, Vector3 position, Quaternion rotation);

    /// <summary>Moves the body there, keeping its velocity, and wakes it unless it is static.</summary>
    public abstract void Teleport(BackendBody body, Vector3 position, Quaternion rotation);

    /// <summary>Gives a kinematic or character body the velocity that reaches the pose in one step of <paramref name="dt"/>.</summary>
    public abstract void MoveKinematic(BackendBody body, Vector3 position, Quaternion rotation, float dt);

    public abstract Vector3 GetLinearVelocity(BackendBody body);

    public abstract void SetLinearVelocity(BackendBody body, Vector3 velocity);

    public abstract Vector3 GetAngularVelocity(BackendBody body);

    public abstract void SetAngularVelocity(BackendBody body, Vector3 velocity);

    public abstract void ApplyForce(BackendBody body, Vector3 force, Vector3? position);

    public abstract void ApplyImpulse(BackendBody body, Vector3 impulse, Vector3? position);

    /// <summary>Adds the torque for the next step and wakes the body.</summary>
    public abstract void ApplyTorque(BackendBody body, Vector3 torque);

    public abstract bool IsSleeping(BackendBody body);

    public abstract void Wake(BackendBody body);

    /// <summary>Wakes every non-static body touching this one (before it leaves or changes its filter).</summary>
    public abstract void WakeTouching(BackendBody body);

    /// <summary>Drops the body's resting contacts so a new filter applies now.</summary>
    public abstract void ClearContacts(BackendBody body);

    // ---- scans (after Step) --------------------------------------------------------------------------------------

    /// <summary>Appends the bodies touching <paramref name="body"/> with intact contact points (may repeat a body).</summary>
    public abstract void CollectTouching(BackendBody body, List<BackendBody> others);

    /// <summary>Appends the bodies the area overlaps and detects (each once); false when the area has no shapes.</summary>
    public abstract bool CollectOverlaps(BackendBody area, List<BackendBody> others);

    // ---- queries (areas are never reported) ----------------------------------------------------------------------

    /// <summary>The closest body along <paramref name="motion"/> whose layer is in <paramref name="mask"/>; with <paramref name="hitFromInside"/> false, convex shapes containing <paramref name="from"/> are skipped.</summary>
    public abstract bool RayCast(Vector3 from, Vector3 motion, uint mask, BackendBody exclude, bool hitFromInside, out BackendHit3D hit);

    /// <summary>Sweeps a convex shape (at scale 1) along <paramref name="motion"/>; the first body it touches.</summary>
    public abstract bool ShapeCast(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, Vector3 motion, uint mask, BackendBody exclude,
        out BackendHit3D hit);

    /// <summary>Appends the bodies overlapping a convex shape (at scale 1), each once.</summary>
    public abstract void IntersectShape(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, uint mask, BackendBody exclude,
        List<BackendBody> output);

    /// <summary>Appends the bodies containing <paramref name="point"/>, each once.</summary>
    public abstract void IntersectPoint(Vector3 point, uint mask, BackendBody exclude, List<BackendBody> output);

    // ---- character primitives (MoveAndSlide; the body's own mask, the body itself excluded) ----------------------

    /// <summary>Sweeps every shape of the body from a pose along <paramref name="motion"/>; the closest hit wins.</summary>
    public abstract bool CastBody(BackendBody body, Vector3 position, Quaternion rotation, Vector3 motion, out BackendHit3D hit);

    /// <summary>Shapes of the body as built (the <c>shape</c> argument of <see cref="Penetration"/> runs over 0..count−1).</summary>
    public abstract int GetShapeCount(BackendBody body);

    /// <summary>Appends the other bodies' shapes whose bounds come within <paramref name="expand"/> of the body's shapes at the pose.</summary>
    public abstract void CollectNearbyShapes(BackendBody body, Vector3 position, Quaternion rotation, float expand, List<BackendShapeRef> output);

    /// <summary>Signed distance from shape <paramref name="shape"/> of the body at the pose to <paramref name="other"/> (a hint: nothing beyond <paramref name="maxDistance"/> matters).</summary>
    public abstract bool Penetration(BackendBody body, int shape, Vector3 position, Quaternion rotation, BackendShapeRef other, float maxDistance,
        out BackendContact3D contact);

    /// <summary>The smallest signed distance between the body at the pose and <paramref name="other"/>, and the normal from the body towards it.</summary>
    public abstract bool Distance(BackendBody body, Vector3 position, Quaternion rotation, BackendBody other, out float distance, out Vector3 normal);
}
```

---

### Task 1: Branch and characterization tests

**Files:**
- Create: `Tests/MainframeEngine.Tests/Physics/PhysicsSeamTests.cs`

**Interfaces:**
- Consumes: `PhysicsHarness3D` (`Tests/MainframeEngine.Tests/Physics/PhysicsHarness.cs:6-75`), `MemoryLogSink` (`MainframeEngine/Src/Debugging/LogSinks.cs:58`), `Log.AddSink`/`RemoveSink` (`MainframeEngine/Src/Debugging/Log.cs:69,82`), public node API only.
- Produces: `public sealed class PhysicsSeamTests` (10 facts), green on the unmodified code.

- [ ] **Step 1: Create the branch**

```bash
cd /Users/brogan/mainframe/mainframe-engine
git status --short   # the BRUTAL spec files must not be listed (see Global Constraints)
git switch main && git pull --ff-only
git switch -c brutal/b2-physics-seam
```

- [ ] **Step 2: Write the characterization tests**

```csharp
using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// Behaviour the B2 backend seam must keep (ADR 0192): shared resources and handles across spaces, kind changes and
/// removals inside signal handlers, sub-viewport disposal, shape owners of compound and concave bodies, overlap order,
/// runtime area masks and the shape error log. Written against the Jitter2 code before the seam existed.
/// </summary>
public sealed class PhysicsSeamTests : IDisposable
{
    private static readonly Vector3 Down = new(0, -20, 0);
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private static StaticBody3D StaticWith(string name, Shape3D shape, Vector3 position = default)
    {
        var body = new StaticBody3D { Name = name, Position = position };
        body.AddChild(new CollisionShape3D { Shape = shape });
        return body;
    }

    [Fact]
    public void EditingASharedShapeRebuildsItsBodiesInEverySpace()
    {
        var shared = new BoxShape3D { Size = new Vector3(2) };
        _h.Add(StaticWith("Main", shared));
        var viewport = _h.Add(new SceneViewport { Name = "Sub" });
        viewport.AddChild(StaticWith("SubBody", shared));
        _h.Run(1);
        var sub = viewport.World3D.DirectSpaceState;

        Assert.True(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out var hit));
        Assert.Equal(1f, hit.Position.Y, 3);
        Assert.True(sub.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out hit));
        Assert.Equal(1f, hit.Position.Y, 3);

        shared.Size = new Vector3(4); // queries flush the rebuild: no step needed
        Assert.True(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out hit));
        Assert.Equal(2f, hit.Position.Y, 3);
        Assert.True(sub.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out hit));
        Assert.Equal(2f, hit.Position.Y, 3);

        // One query shape used in both spaces sees its own edits in each.
        var probe = new SphereShape3D { Radius = 0.5f };
        var from = Transform3D.FromTrs(new Vector3(0, 10, 0), Quaternion.Identity, Vector3.One);
        Assert.True(_h.Query.ShapeCast(probe, from, Down, out var cast));
        Assert.True(sub.ShapeCast(probe, from, Down, out var subCast));
        Assert.Equal(cast.Fraction, subCast.Fraction, 4);
        var before = cast.Fraction;
        probe.Radius = 1f;
        Assert.True(_h.Query.ShapeCast(probe, from, Down, out cast));
        Assert.True(sub.ShapeCast(probe, from, Down, out subCast));
        Assert.True(cast.Fraction < before - 0.02f, $"{cast.Fraction} vs {before}");
        Assert.Equal(cast.Fraction, subCast.Fraction, 4);
    }

    [Fact]
    public void ExcludingABodyOfAnotherSpaceExcludesNothingHere()
    {
        var floor = _h.AddFloor();
        var viewport = _h.Add(new SceneViewport { Name = "Sub" });
        var other = StaticWith("Other", new BoxShape3D());
        viewport.AddChild(other); // the first (and only) body of the sub-viewport's space
        _h.Run(1);

        Assert.True(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out var hit, exclude: other));
        Assert.Same(floor, hit.Collider);
        var results = new List<CollisionObject3D>();
        _h.Query.IntersectPoint(new Vector3(0, -0.25f, 0), results, exclude: other);
        Assert.Equal([floor], results);
        var from = Transform3D.FromTrs(new Vector3(0, 5, 0), Quaternion.Identity, Vector3.One);
        Assert.True(_h.Query.ShapeCast(new SphereShape3D(), from, Down, out var cast, exclude: other));
        Assert.Same(floor, cast.Collider);
    }

    [Fact]
    public void ChangingABodysModeInsideASignalHandlerAppliesAtTheNextStep()
    {
        _h.AddFloor();
        var box = _h.AddBox(new Vector3(0, 0.6f, 0));
        box.ContactMonitor = true;
        box.GravityScale = 0.5f; // Jitter2 applies this one as a force: it must stop with the switch
        box.PhysicsInterpolation = false;
        var switched = false;
        box.BodyEntered += _ =>
        {
            if (switched)
                return;
            switched = true;
            box.Mode = RigidBodyMode.Kinematic;
            box.LinearVelocity = new Vector3(1, 0, 0);
        };
        _h.Run(30);
        Assert.True(switched);

        var start = box.Position;
        _h.Run(60);
        Assert.Equal(start.X + 1f, box.Position.X, 2); // moves by its velocity
        Assert.Equal(start.Y, box.Position.Y, 4);       // no gravity, no gravity-scale force
        Assert.False(box.Sleeping);

        box.Mode = RigidBodyMode.Dynamic;
        box.LinearVelocity = Vector3.Zero;
        box.Teleport(Transform3D.FromTrs(new Vector3(5, 3, 0), Quaternion.Identity, Vector3.One));
        _h.Run(30);
        Assert.True(box.Position.Y < 3f, $"dynamic again, falls: {box.Position}");
    }

    [Fact]
    public void QueriesInASignalHandlerNoLongerSeeABodyFreedEarlierInIt()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(4, 4, 4));
        var target = _h.AddStaticBox(new Vector3(10, 0, 0), Vector3.One, name: "Target");
        var box = _h.AddBox(Vector3.Zero, 0.5f);
        box.GravityScale = 0;
        bool? rayAfterFree = null;
        var results = new List<CollisionObject3D>();
        area.BodyEntered += _ =>
        {
            target.Free();
            rayAfterFree = _h.Query.RayCast(new Vector3(10, 5, 0), new Vector3(10, -5, 0), out _);
            _h.Query.IntersectPoint(new Vector3(10, 0, 0), results);
        };
        _h.Run(1);

        Assert.False(rayAfterFree);
        Assert.Empty(results);
    }

    [Fact]
    public void ABodyAddedRightAfterAnotherIsFreedDoesNotInheritItsSignals()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(4, 4, 4));
        var log = new List<string>();
        area.BodyEntered += n => log.Add("enter " + n.Name);
        area.BodyExited += n => log.Add("exit " + n.Name);
        var first = _h.AddBox(Vector3.Zero, 0.5f, "First");
        first.GravityScale = 0;
        _h.Run(2);

        first.Free();
        var second = _h.AddBox(new Vector3(20, 0, 0), 0.5f, "Second"); // takes the freed body's place in the library
        second.GravityScale = 0;
        _h.Run(2);
        Assert.Equal(["enter First", "exit First"], log);
        Assert.False(area.OverlapsBody(second));
        Assert.False(_h.Query.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out _));
        Assert.True(_h.Query.RayCast(new Vector3(20, 5, 0), new Vector3(20, -5, 0), out var hit));
        Assert.Same(second, hit.Collider);

        second.Teleport(Transform3D.FromTrs(Vector3.Zero, Quaternion.Identity, Vector3.One));
        _h.Run(1);
        Assert.Equal(["enter First", "exit First", "enter Second"], log);
    }

    [Fact]
    public void FreeingASubViewportReleasesItsSpaceAndItsQueriesFindNothing()
    {
        var floor = _h.AddFloor();
        var viewport = _h.Add(new SceneViewport { Name = "Sub" });
        var area = new Area3D { Name = "SubArea" };
        area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4) } });
        var box = new RigidBody3D { Name = "SubBox", GravityScale = 0, ContactMonitor = true };
        box.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
        viewport.AddChild(area);
        viewport.AddChild(box);
        _h.Run(2);
        var world = viewport.World3D;
        Assert.True(area.OverlapsBody(box));
        Assert.Equal(2, _h.Server.Spaces.Count);

        viewport.Free();
        Assert.Null(box.Space);
        Assert.Null(area.Space);
        _h.Run(1); // the server releases the space in Process
        Assert.Single(_h.Server.Spaces);
        Assert.Null(world.PhysicsSpace);
        Assert.False(world.DirectSpaceState.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out _));

        var again = _h.Add(new SceneViewport { Name = "Again" });
        again.AddChild(StaticWith("AgainBody", new BoxShape3D()));
        _h.Run(1);
        Assert.Equal(2, _h.Server.Spaces.Count);
        Assert.True(again.World3D.DirectSpaceState.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out var hit));
        Assert.Equal("AgainBody", hit.Collider.Name);
        Assert.True(_h.Query.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out hit));
        Assert.Same(floor, hit.Collider);
    }

    [Fact]
    public void RayHitsReportTheOwningShapeOfCompoundAndConcaveBodies()
    {
        var body = new StaticBody3D { Name = "Compound" };
        var box = new CollisionShape3D { Name = "Box", Shape = new BoxShape3D(), Position = new Vector3(-3, 0, 0) };
        var mesh = new CollisionShape3D
        {
            Name = "Mesh",
            Position = new Vector3(3, 0, 0),
            Shape = new ConcavePolygonShape3D { Faces = [new(-2, 0, -2), new(-2, 0, 2), new(2, 0, -2), new(2, 0, -2), new(-2, 0, 2), new(2, 0, 2)] },
        };
        body.AddChild(box);
        body.AddChild(mesh);
        _h.Add(body);

        Assert.True(_h.Query.RayCast(new Vector3(-3, 5, 0), new Vector3(-3, -5, 0), out var hit));
        Assert.Same(box, hit.Shape);
        Assert.True(_h.Query.RayCast(new Vector3(3.5f, 5, 0.5f), new Vector3(3.5f, -5, 0.5f), out hit));
        Assert.Same(mesh, hit.Shape);

        box.Disabled = true; // rebuild: the mesh is now the first shape
        Assert.False(_h.Query.RayCast(new Vector3(-3, 5, 0), new Vector3(-3, -5, 0), out _));
        Assert.True(_h.Query.RayCast(new Vector3(2.5f, 5, -0.5f), new Vector3(2.5f, -5, -0.5f), out hit));
        Assert.Same(mesh, hit.Shape);
    }

    [Fact]
    public void AnAreaListsOverlappingBodiesInItsSetOrder()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(10, 4, 10));
        RigidBody3D Box(string name, Vector3 at)
        {
            var b = _h.AddBox(at, 0.5f, name);
            b.GravityScale = 0;
            _h.Run(1);
            return b;
        }

        Box("A", new Vector3(3, 0, 0));
        var b = Box("B", new Vector3(-3, 0, 0));
        Box("C", new Vector3(0, 0, 3));
        b.Free();
        Box("D", new Vector3(0, 0, -3));

        var bodies = new List<Node3D>();
        area.GetOverlappingBodies(bodies);
        // The per-area set is a Dictionary: D takes the slot B freed, so it enumerates before C.
        Assert.Equal(["A", "D", "C"], bodies.Select(n => n.Name));
    }

    [Fact]
    public void ChangingAnAreasMaskAppliesAtTheNextStep()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(4, 4, 4));
        var box = _h.AddBox(Vector3.Zero, 0.5f);
        box.GravityScale = 0;
        box.CollisionLayer = CollisionLayers.Layer(3);
        _h.Run(1);
        Assert.False(area.OverlapsBody(box));

        area.CollisionMask = CollisionLayers.Layer(3);
        _h.Run(1);
        Assert.True(area.OverlapsBody(box));

        area.CollisionMask = CollisionLayers.Layer(1);
        _h.Run(1);
        Assert.False(area.OverlapsBody(box));
    }

    [Fact]
    public void InvalidShapesAndConcaveShapesOnDynamicBodiesAreLogged()
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Error };
        Log.AddSink(sink);
        try
        {
            var flat = new StaticBody3D { Name = "Flat" };
            flat.AddChild(new CollisionShape3D { Name = "Hull", Shape = new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] } });
            flat.AddChild(new CollisionShape3D { Name = "Coplanar", Shape = new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, new Vector3(1, 0, 1)] } });
            _h.Add(flat);
            var dynamic = _h.AddBox(new Vector3(5, 5, 0), name: "Dyn");
            dynamic.AddChild(new CollisionShape3D { Name = "Mesh", Shape = new ConcavePolygonShape3D { Faces = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] } });
            _h.Run(1);

            var messages = sink.Snapshot().Select(e => e.Message).ToArray();
            Assert.Contains(messages, m => m.Contains("'Flat/Hull': ConvexPolygonShape3D is invalid (ConvexPolygonShape3D needs at least 4 points.); ignored.", StringComparison.Ordinal));
            Assert.Contains(messages, m => m.Contains("'Flat/Coplanar': ConvexPolygonShape3D is invalid (", StringComparison.Ordinal));
            Assert.Contains(messages, m => m.Contains("'Dyn': ConcavePolygonShape3D is concave and only works on static or kinematic bodies; ignored.", StringComparison.Ordinal));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }
}
```

- [ ] **Step 3: Run them on the unmodified code**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsSeamTests"`
Expected: 10 passed. These describe today's behaviour: if one fails, the expectation is wrong, not the code. Fix the
assertion to what the unmodified code does (for `AnAreaListsOverlappingBodiesInItsSetOrder`, the order printed in the
failure), note the reason in its comment, and re-run until green. Do not change engine code in this task.

- [ ] **Step 4: Run the physics suite**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Tests/MainframeEngine.Tests/Physics/PhysicsSeamTests.cs
git commit -F- <<'EOF'
Characterize 3D physics behaviour the B2 backend seam must keep

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 2: Bit-exact Jitter2 trace

**Files:**
- Create: `Tests/MainframeEngine.Tests/Physics/PhysicsTraceTests.cs`
- Create (recorded): `Tests/MainframeEngine.Tests/Physics/Traces/jitter2.<rid>.trace` (e.g. `jitter2.osx-arm64.trace`)

**Interfaces:**
- Consumes: `PhysicsHarness3D`, `WalkerCharacter3D` (`PhysicsHarness.cs:78-94`), public node API.
- Produces: `public sealed class PhysicsTraceTests` with `Jitter2SceneMatchesTheRecordedTrace()`, `internal static string Record()`, `internal static string RepoRoot()`.

- [ ] **Step 1: Write the trace test**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// The B2/B3 refactor gate (ADR 0192): a bit-exact trace of a Jitter2 scene (every body's pose, velocities and sleep
/// state, contact/area/sleep signals, the character's floor state and four queries, for 240 steps) compared with a trace
/// recorded before the backend seam existed. Jitter2 reproduces only on one machine and build (ADR 0020), so the file is
/// per RID with the runtime and configuration in its header, and the test runs only on request, alone in its process
/// (Jitter2's shape ids are process-wide):
/// <c>MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"</c>.
/// <c>MF_PHYSICS_TRACE=record</c> rewrites the file (only from code whose behaviour is the reference). Every step's text
/// feeds a running SHA-256 written per step; every 10th step's text is written in full (set <see cref="WriteEvery"/> to
/// 1 locally to see the first differing value).
/// </summary>
public sealed class PhysicsTraceTests
{
    private const int Steps = 240;
    private const int WriteEvery = 10;

    [Fact]
    public void Jitter2SceneMatchesTheRecordedTrace()
    {
        var mode = Environment.GetEnvironmentVariable("MF_PHYSICS_TRACE");
        if (mode is not ("1" or "record"))
            Assert.Skip("The bit-exact physics trace runs alone, with MF_PHYSICS_TRACE=1 (see the class remarks).");
        var path = Path.Combine(RepoRoot(), "Tests", "MainframeEngine.Tests", "Physics", "Traces", $"jitter2.{RuntimeInformation.RuntimeIdentifier}.trace");
        var actual = Record();
        if (mode == "record")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        if (!File.Exists(path))
            Assert.Skip($"No trace at {path}: record one with MF_PHYSICS_TRACE=record on the reference code.");
        var expected = File.ReadAllText(path);
        var recordedOn = expected[..expected.IndexOf('\n', StringComparison.Ordinal)];
        if (recordedOn != Header())
            Assert.Skip($"The trace was recorded on '{recordedOn}'; this run is '{Header()}'. Re-record it from the reference code.");
        if (expected == actual)
            return;
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
            i++;
        Assert.Fail($"The physics trace differs from {path} at line {i + 1}:\n  expected: {(i < e.Length ? e[i] : "<end>")}\n  actual:   {(i < a.Length ? a[i] : "<end>")}");
    }

    private static string Header()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        return $"# jitter2 trace · {RuntimeInformation.RuntimeIdentifier} · {RuntimeInformation.FrameworkDescription} · {configuration}";
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MainframeEngine.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (MainframeEngine.slnx) not found above the test output.");
    }

    internal static string Record()
    {
        using var h = new PhysicsHarness3D(new PhysicsSettings3D { MultiThreaded = false, Deterministic = true });
        var signals = new StringBuilder();
        h.AddFloor(size: 60);
        h.AddStaticBox(new Vector3(6, 1, 0), new Vector3(4, 0.2f, 4), new Vector3(0, 0, 20), "Ramp");
        var wall = h.AddStaticBox(new Vector3(-6, 1.5f, 0), new Vector3(0.5f, 3, 8), name: "Wall");
        var bumps = new StaticBody3D { Name = "Bumps", Position = new Vector3(-3, 0, 6) };
        bumps.AddChild(new CollisionShape3D
        {
            Scale = new Vector3(2, 1, 2),
            Shape = new HeightMapShape3D
            {
                MapWidth = 5,
                MapDepth = 5,
                MapData = [0, 0.1f, 0.2f, 0.1f, 0, 0.1f, 0.3f, 0.4f, 0.3f, 0.1f, 0.2f, 0.4f, 0.6f, 0.4f, 0.2f, 0.1f, 0.3f, 0.4f, 0.3f, 0.1f, 0, 0.1f, 0.2f, 0.1f, 0],
            },
        });
        h.Add(bumps);

        var bodies = new List<RigidBody3D>();
        for (var i = 0; i < 4; i++)
            bodies.Add(h.AddBox(new Vector3(0.05f * i, 0.5f + i * 1.01f, 0), name: $"Stack{i}"));
        var ball = new RigidBody3D { Name = "Ball", Position = new Vector3(2, 3, 0), ContactMonitor = true, PhysicsMaterial = new PhysicsMaterial { Bounce = 0.4f } };
        ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.3f } });
        bodies.Add(h.Add(ball));
        var floaty = h.AddBox(new Vector3(-2, 4, 0), 0.5f, "Floaty");
        floaty.GravityScale = 0.25f;
        floaty.LinearDamp = 2f;
        floaty.AngularDamp = 1f;
        bodies.Add(floaty);
        var locked = h.AddBox(new Vector3(6, 3, 0), 0.6f, "Locked");
        locked.AxisLocks = AxisLock.LinearZ | AxisLock.AngularX;
        bodies.Add(locked);
        var capsule = new RigidBody3D { Name = "Capsule", Position = new Vector3(-2, 3, 6), Mass = 3, AngularVelocity = new Vector3(0, 0, 2), Ccd = true };
        capsule.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.2f }, RotationDegrees = new Vector3(0, 0, 90) });
        bodies.Add(h.Add(capsule));
        var hull = new RigidBody3D { Name = "Hull", Position = new Vector3(-4, 3, 7), CanSleep = false };
        hull.AddChild(new CollisionShape3D
        {
            Scale = new Vector3(1.5f, 1, 1),
            Shape = new ConvexPolygonShape3D { Points = [new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0, 0.5f, -0.5f), new(0, 0, 0.5f)] },
        });
        bodies.Add(h.Add(hull));
        var pair = new RigidBody3D { Name = "Pair", Position = new Vector3(3, 2, -3) };
        pair.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.3f, Height = 0.8f }, Position = new Vector3(-0.4f, 0, 0) });
        pair.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.5f) }, Position = new Vector3(0.4f, 0, 0) });
        bodies.Add(h.Add(pair));
        var platform = new RigidBody3D { Name = "Platform", Mode = RigidBodyMode.Kinematic, Position = new Vector3(0, 0.25f, -6) };
        platform.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3, 0.5f, 3) } });
        bodies.Add(h.Add(platform));
        bodies.Add(h.AddBox(new Vector3(0, 1.01f, -6), 0.5f, "Rider"));
        var walker = new WalkerCharacter3D { Name = "Walker", Position = new Vector3(-3, 1, 0), Walk = new Vector3(-1.5f, 0, 0.8f) };
        walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
        h.Add(walker);
        var area = h.AddArea(new Vector3(0, 1, 0), new Vector3(3, 2, 3));

        area.BodyEntered += n => signals.Append("  area+ ").Append(n.Name).Append('\n');
        area.BodyExited += n => signals.Append("  area- ").Append(n.Name).Append('\n');
        ball.BodyEntered += n => signals.Append("  ball+ ").Append(n.Name).Append('\n');
        ball.BodyExited += n => signals.Append("  ball- ").Append(n.Name).Append('\n');
        foreach (var body in bodies)
        {
            var b = body;
            b.SleepingStateChanged += () => signals.Append("  sleep ").Append(b.Name).Append(b.Sleeping ? " on\n" : " off\n");
        }

        var probe = new SphereShape3D { Radius = 0.25f };
        var probeFrom = Transform3D.FromTrs(new Vector3(2, 6, 0), Quaternion.Identity, Vector3.One);
        var probeAt = Transform3D.FromTrs(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One);
        var overlaps = new List<CollisionObject3D>();
        var inArea = new List<Node3D>();
        var trace = new StringBuilder(128 * 1024).Append(Header()).Append('\n');
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var step = 0; step < Steps; step++)
        {
            platform.Position = new Vector3(MathF.Sin(step * 0.05f) * 2, 0.25f, -6); // a user move every step: a kinematic target
            if (step == 60)
                ball.ApplyImpulse(new Vector3(1.5f, 0, 0));
            if (step == 90)
                floaty.ApplyTorque(new Vector3(0, 3, 0));
            if (step == 120)
                wall.CollisionLayer = CollisionLayers.Layer(2); // a filter change under resting contacts
            if (step == 150)
                bodies[3].Teleport(Transform3D.FromTrs(new Vector3(1, 4, 1), Quaternion.CreateFromYawPitchRoll(0.3f, 0, 0), Vector3.One));
            if (step == 180)
                floaty.Mode = RigidBodyMode.Kinematic;
            if (step == 200)
                floaty.Mode = RigidBodyMode.Dynamic;
            h.Run(1);

            var block = new StringBuilder(4096).Append(signals);
            signals.Clear();
            foreach (var body in bodies)
            {
                body.GlobalTransform.Decompose(out var p, out var r, out _);
                block.Append("  ").Append(body.Name).Append(" p=").Append(Hex(p)).Append(" r=").Append(Hex(r)).Append(" v=").Append(Hex(body.LinearVelocity))
                    .Append(" w=").Append(Hex(body.AngularVelocity)).Append(" s=").Append(body.Sleeping ? '1' : '0').Append('\n');
            }

            walker.GlobalTransform.Decompose(out var wp, out _, out _);
            block.Append("  Walker p=").Append(Hex(wp)).Append(" floor=").Append(walker.IsOnFloor()).Append(" wall=").Append(walker.IsOnWall())
                .Append(" n=").Append(Hex(walker.GetFloorNormal())).Append(" v=").Append(Hex(walker.GetRealVelocity()))
                .Append(" hits=").Append(walker.GetSlideCollisionCount()).Append('\n');
            var q = h.Query;
            block.Append("  ray ").Append(q.RayCast(new Vector3(0.3f, 10, 0.3f), new Vector3(0.3f, -10, 0.3f), out var ray)
                ? $"{ray.Collider.Name} {ray.Shape?.Name} {Hex(ray.Fraction)} {Hex(ray.Normal)} {Hex(ray.Position)}"
                : "-").Append('\n');
            block.Append("  cast ").Append(q.ShapeCast(probe, probeFrom, new Vector3(0, -10, 0), out var cast)
                ? $"{cast.Collider.Name} {Hex(cast.Fraction)} {Hex(cast.Normal)} {Hex(cast.Position)}"
                : "-").Append('\n');
            overlaps.Clear();
            q.IntersectShape(probe, probeAt, overlaps);
            q.IntersectPoint(new Vector3(0.1f, 0.1f, 0.1f), overlaps);
            block.Append("  overlap ").AppendJoin(',', overlaps.Select(o => o.Name)).Append('\n');
            inArea.Clear();
            area.GetOverlappingBodies(inArea);
            block.Append("  area ").AppendJoin(',', inArea.Select(o => o.Name)).Append('\n');

            var text = block.ToString();
            hash.AppendData(Encoding.UTF8.GetBytes(text));
            trace.Append("step ").Append(step).Append(' ').Append(Convert.ToHexStringLower(hash.GetCurrentHash())).Append('\n');
            if (step % WriteEvery == WriteEvery - 1)
                trace.Append(text);
        }

        return trace.ToString();
    }

    private static string Hex(float f) => BitConverter.SingleToUInt32Bits(f).ToString("x8");

    private static string Hex(Vector3 v) => $"{Hex(v.X)}/{Hex(v.Y)}/{Hex(v.Z)}";

    private static string Hex(Quaternion q) => $"{Hex(q.X)}/{Hex(q.Y)}/{Hex(q.Z)}/{Hex(q.W)}";
}
```

- [ ] **Step 2: Record the reference on the unmodified code**

Run: `MF_PHYSICS_TRACE=record dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"`
Expected: 1 passed; `Tests/MainframeEngine.Tests/Physics/Traces/jitter2.<rid>.trace` exists (≈ 80 KB, 1 header line,
240 `step` lines, 24 blocks).

- [ ] **Step 3: Prove it reproduces**

Run three times: `MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"`
Expected: 1 passed each time (not skipped). Then run `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics"`: the trace test is skipped, everything else passes.

- [ ] **Step 4: Commit**

```bash
git add Tests/MainframeEngine.Tests/Physics/PhysicsTraceTests.cs Tests/MainframeEngine.Tests/Physics/Traces/
git commit -F- <<'EOF'
Add a bit-exact Jitter2 trace as the B2 refactor gate

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 3: Backend settings

**Files:**
- Modify: `MainframeEngine/Src/Physics/PhysicsSettings.cs:1-59`
- Modify: `Tests/MainframeEngine.Tests/Physics/PhysicsSettingsTests.cs:6-19`

**Interfaces:**
- Produces: `public enum PhysicsBackendKind { Jitter2, Jolt }`; `PhysicsSettings3D.Backend`, `PhysicsSettings3D.Jitter2` (`Jitter2PhysicsSettings`), `PhysicsSettings3D.Jolt` (`JoltPhysicsSettings`); forwarders `PhysicsSettings3D.SolverIterations`/`RelaxationIterations`.

- [ ] **Step 1: Write the failing test** (append to `PhysicsSettingsTests`)

```csharp
    [Fact]
    public void BackendSettingsHaveTheSpecDefaultsAndGuardTheirInputs()
    {
        var s = new PhysicsSettings3D();
        Assert.Equal(PhysicsBackendKind.Jitter2, s.Backend);
        Assert.Equal(6, s.Jitter2.SolverIterations);
        Assert.Equal(4, s.Jitter2.RelaxationIterations);
        Assert.Equal((10, 2, 65536, 65536, 10240, 16, 4096),
            (s.Jolt.VelocitySteps, s.Jolt.PositionSteps, s.Jolt.MaxBodies, s.Jolt.MaxBodyPairs, s.Jolt.MaxContactConstraints, s.Jolt.TempAllocatorMB,
                s.Jolt.ContactEventCapacity));

        s.SolverIterations = 9; // the pre-B2 properties forward to the Jitter2 group
        s.RelaxationIterations = 2;
        Assert.Equal(9, s.Jitter2.SolverIterations);
        Assert.Equal(2, s.Jitter2.RelaxationIterations);

        Assert.Throws<ArgumentOutOfRangeException>(() => s.Backend = (PhysicsBackendKind)7);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Jitter2.SolverIterations = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Jitter2.RelaxationIterations = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Jolt.VelocitySteps = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Jolt.PositionSteps = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Jolt.MaxBodies = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Jolt.MaxBodyPairs = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Jolt.MaxContactConstraints = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Jolt.TempAllocatorMB = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Jolt.ContactEventCapacity = 0);
    }
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsSettingsTests"` → build error (`PhysicsBackendKind` missing).

- [ ] **Step 3: Implement.** In `PhysicsSettings.cs` replace lines 5-59 (the `PhysicsSettings3D` class) with:

```csharp
/// <summary>The 3D physics library a space runs on (ADR 0192). Jolt arrives in B3; until then selecting it logs once and runs Jitter2.</summary>
public enum PhysicsBackendKind
{
    /// <summary>Jitter2 2.9 (pure C#, every platform).</summary>
    Jitter2,

    /// <summary>Jolt 5.5 through BRUTAL (B3).</summary>
    Jolt,
}

/// <summary>
/// Project settings of the 3D physics server (<see cref="PhysicsServer3D"/>): the shared keys plus one group per backend
/// (<see cref="Jitter2"/>, <see cref="Jolt"/>). Read when a physics space is created (<see cref="MultiThreaded"/> at every
/// step); set them before nodes enter the tree (<see cref="EngineOptions.Physics3D"/>).
/// </summary>
public sealed class PhysicsSettings3D
{
    private readonly Jitter2PhysicsSettings _jitter2 = new();

    /// <summary>The library new spaces run on (default Jitter2; <c>physics.3d.backend</c>).</summary>
    public PhysicsBackendKind Backend
    {
        get;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a physics backend.");
            field = value;
        }
    }

    /// <summary>Gravity in m/s² (default 9.81 down, −Y).</summary>
    public Vector3 Gravity { get; set; } = new(0, -9.81f, 0);

    /// <summary>Sub-steps per fixed step (1 = no sub-stepping).</summary>
    public int SubstepCount
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 1;

    /// <summary>Lets resting bodies sleep (deactivate) until something wakes them.</summary>
    public bool AllowDeactivation { get; set; } = true;

    /// <summary>Steps on the library's worker threads (faster for large worlds).</summary>
    public bool MultiThreaded { get; set; } = true;

    /// <summary>
    /// A deterministic solve instead of the (faster) regular one, so the same build on the same machine produces identical
    /// results run to run — for golden-image tests and replays. It is not a cross-platform lockstep guarantee (ADR 0020).
    /// Read when a space is created.
    /// </summary>
    public bool Deterministic { get; set; }

    /// <summary>What only Jitter2 understands (<c>physics.3d.jitter2</c>).</summary>
    public Jitter2PhysicsSettings Jitter2 => _jitter2;

    /// <summary>What only Jolt understands (<c>physics.3d.jolt</c>; used from B3).</summary>
    public JoltPhysicsSettings Jolt { get; } = new();

    /// <summary>Same as <see cref="Jitter2PhysicsSettings.SolverIterations"/> (kept from before the backend groups).</summary>
    public int SolverIterations
    {
        get => _jitter2.SolverIterations;
        set => _jitter2.SolverIterations = value;
    }

    /// <summary>Same as <see cref="Jitter2PhysicsSettings.RelaxationIterations"/> (kept from before the backend groups).</summary>
    public int RelaxationIterations
    {
        get => _jitter2.RelaxationIterations;
        set => _jitter2.RelaxationIterations = value;
    }
}

/// <summary>Jitter2-only 3D settings (<c>physics.3d.jitter2</c>).</summary>
public sealed class Jitter2PhysicsSettings
{
    /// <summary>Constraint solver iterations per sub-step (Jitter2 default 6).</summary>
    public int SolverIterations
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 6;

    /// <summary>Velocity relaxation iterations per sub-step (Jitter2 default 4).</summary>
    public int RelaxationIterations
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 4;
}

/// <summary>Jolt-only 3D settings (<c>physics.3d.jolt</c>): parsed and saved in B2, used by the Jolt backend from B3.</summary>
public sealed class JoltPhysicsSettings
{
    /// <summary>Velocity solver steps per sub-step (Jolt default 10).</summary>
    public int VelocitySteps { get; set => field = AtLeastOne(value); } = 10;

    /// <summary>Position solver steps per sub-step (Jolt default 2).</summary>
    public int PositionSteps { get; set => field = AtLeastOne(value); } = 2;

    /// <summary>Bodies one space can hold.</summary>
    public int MaxBodies { get; set => field = AtLeastOne(value); } = 65536;

    /// <summary>Broad-phase pairs per step.</summary>
    public int MaxBodyPairs { get; set => field = AtLeastOne(value); } = 65536;

    /// <summary>Contact constraints per step.</summary>
    public int MaxContactConstraints { get; set => field = AtLeastOne(value); } = 10240;

    /// <summary>Per-space temporary allocator, in MB.</summary>
    public int TempAllocatorMB { get; set => field = AtLeastOne(value); } = 16;

    /// <summary>Contact events recorded per step for monitors and areas (doubled on overflow).</summary>
    public int ContactEventCapacity { get; set => field = AtLeastOne(value); } = 4096;

    private static int AtLeastOne(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        return value;
    }
}
```

The forwarders read the field `_jitter2` (not the `Jitter2` property) so the source rule of Task 10 never sees a
`Jitter2.` qualified name outside the Jitter folder.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsSettingsTests|FullyQualifiedName~MainframeEngine.Tests.Project"` → all pass (the project tests still read and write `solverIterations` at the top level through the forwarders).

- [ ] **Step 5: Physics suite, then commit**

```bash
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics"
git add MainframeEngine/Src/Physics/PhysicsSettings.cs Tests/MainframeEngine.Tests/Physics/PhysicsSettingsTests.cs
git commit -F- <<'EOF'
Add the 3D physics backend choice and per-backend settings groups

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 4: `project.mfproj` keys and the migration

**Files:**
- Modify: `MainframeEngine/Src/Project/ProjectSettingsFormat.cs:20` (`Current`), `:23-44` (migrations), `:194-262` (`WritePhysics`), `:553-585` (`ReadPhysics`)
- Modify: `Templates/MainframeEngine.Templates/content/mfgame/project.mfproj:2`
- Modify: `Tests/MainframeEngine.Tests/Project/ProjectSettingsTests.cs:56-64` (`Everything`), `:136`, `:215`, `:272`, `:289`

**Interfaces:**
- Consumes: `PhysicsSettings3D.Backend`/`Jitter2`/`Jolt` (Task 3); `Reader.EnumValue<T>` (`ProjectSettingsFormat.cs:542-550`), `Reader.Int`, `Reader.Object`, `Reader.Known`.
- Produces: `ProjectSettingsFormat.Current == 4`; migration `new ProjectMigration(3, …)`; keys `physics.3d.backend`, `physics.3d.jitter2.{solverIterations,relaxationIterations}`, `physics.3d.jolt.{velocitySteps,positionSteps,maxBodies,maxBodyPairs,maxContactConstraints,tempAllocatorMB,contactEventCapacity}`.

- [ ] **Step 1: Write the failing tests** (append to `ProjectSettingsTests`)

```csharp
    [Fact]
    public void ThePreviousFormatMovesTheJitter2SolverKeysUnderJitter2()
    {
        var from = ProjectSettingsFormat.Current - 1;
        var s = Parse($$"""{ "format": {{from}}, "name": "Old", "physics": { "3d": { "substeps": 2, "solverIterations": 9, "relaxationIterations": 2 } } }""");
        Assert.Equal(PhysicsBackendKind.Jitter2, s.Physics.Physics3D.Backend);
        Assert.Equal(2, s.Physics.Physics3D.SubstepCount);
        Assert.Equal(9, s.Physics.Physics3D.Jitter2.SolverIterations);
        Assert.Equal(2, s.Physics.Physics3D.Jitter2.RelaxationIterations);

        var p3 = JsonNode.Parse(Text(s))!["physics"]!["3d"]!;
        Assert.Null(p3["solverIterations"]);
        Assert.Null(p3["relaxationIterations"]);
        Assert.Equal(9, (int)p3["jitter2"]!["solverIterations"]!);
        Assert.Equal(2, (int)p3["jitter2"]!["relaxationIterations"]!);

        // A file that already had a jitter2 group keeps its values; one without physics gains nothing.
        var both = Parse($$"""{ "format": {{from}}, "physics": { "3d": { "solverIterations": 9, "jitter2": { "solverIterations": 7 } } } }""");
        Assert.Equal(7, both.Physics.Physics3D.Jitter2.SolverIterations);
        Assert.Null(JsonNode.Parse(Text(Parse($$"""{ "format": {{from}}, "name": "Bare" }""")))!["physics"]);
    }

    [Fact]
    public void BackendAndJoltSettingsRoundTripWithLowerCaseBackendNames()
    {
        var s = new ProjectSettings { Name = "Jolty", EngineVersion = "0.9.0" };
        s.Physics.Physics3D.Backend = PhysicsBackendKind.Jolt;
        s.Physics.Physics3D.Jolt.VelocitySteps = 12;
        s.Physics.Physics3D.Jolt.ContactEventCapacity = 1024;

        var p3 = JsonNode.Parse(Text(s))!["physics"]!["3d"]!;
        Assert.Equal("jolt", (string?)p3["backend"]);
        Assert.Equal(12, (int)p3["jolt"]!["velocitySteps"]!);
        Assert.Equal(1024, (int)p3["jolt"]!["contactEventCapacity"]!);
        Assert.Null(p3["jolt"]!["maxBodies"]); // defaults are not written
        Assert.Null(p3["jitter2"]);
        var back = Parse(Text(s));
        Assert.Equivalent(s.Physics.Physics3D, back.Physics.Physics3D, strict: true);
        Assert.Equal(PhysicsBackendKind.Jitter2, Parse("""{ "format": 4, "physics": { "3d": { "backend": "Jitter2" } } }""").Physics.Physics3D.Backend);
    }

    [Theory]
    [InlineData("""{ "format": 4, "physics": { "3d": { "backend": "havok" } } }""", "physics.3d.backend 'havok' is not one of")]
    [InlineData("""{ "format": 4, "physics": { "3d": { "jitter2": { "solverIterations": 0 } } } }""", "physics.3d.jitter2.solverIterations must be between 1 and 256")]
    [InlineData("""{ "format": 4, "physics": { "3d": { "jolt": { "velocitySteps": 0 } } } }""", "physics.3d.jolt.velocitySteps must be between 1 and 255")]
    [InlineData("""{ "format": 4, "physics": { "3d": { "jolt": 3 } } }""", "jolt must be an object")]
    public void InvalidBackendSettingsNameTheKey(string json, string expected)
    {
        var e = Assert.Throws<InvalidDataException>(() => Parse(json));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }
```

(If B1 merged first, the literal `"format": 4` in these two tests is `5`.) Also in `Everything()` after line 64 add:

```csharp
        s.Physics.Physics3D.Backend = PhysicsBackendKind.Jolt;
        s.Physics.Physics3D.Jolt.VelocitySteps = 12;
        s.Physics.Physics3D.Jolt.PositionSteps = 3;
        s.Physics.Physics3D.Jolt.MaxBodies = 4096;
        s.Physics.Physics3D.Jolt.MaxBodyPairs = 8192;
        s.Physics.Physics3D.Jolt.MaxContactConstraints = 2048;
        s.Physics.Physics3D.Jolt.TempAllocatorMB = 32;
        s.Physics.Physics3D.Jolt.ContactEventCapacity = 1024;
```

and change the format literals for the bump: line 136 `\"format\": 3,` → `\"format\": 4,`; line 215 `this engine reads up to 3` → `up to 4`; lines 272 and 289 `Assert.Equal(3, (int)json["format"]!)` → `Assert.Equal(4, …)`. Line 257's `3` belongs to the test's own pretend migration chain and stays.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~ProjectSettingsTests"` → the new tests and the four bumped literals fail.

- [ ] **Step 3: Implement.** `ProjectSettingsFormat.cs:20`: `public const int Current = 4;`. Append to `Migrations` after the 2 → 3 step:

```csharp

        // 3 → 4 (B2, ADR 0192): the 3D backend became selectable ("backend"); Jitter2's solver keys moved from
        // "physics.3d" into "physics.3d.jitter2" ("jolt" holds Jolt's own keys).
        new(3, static root =>
        {
            if (root["physics"] is not JsonObject physics || physics["3d"] is not JsonObject p3 || p3["jitter2"] is not (null or JsonObject))
                return;
            string[] keys = ["solverIterations", "relaxationIterations"];
            foreach (var key in keys)
            {
                if (p3[key] is not { } value)
                    continue;
                p3.Remove(key);
                if (p3["jitter2"] is not JsonObject jitter2)
                    p3["jitter2"] = jitter2 = new JsonObject();
                jitter2[key] ??= value;
            }
        }),
```

Replace `WritePhysics` (lines 194-262) with:

```csharp
    private static void WritePhysics(Utf8JsonWriter w, PhysicsProjectSettings s)
    {
        var d = new PhysicsProjectSettings();
        var p3 = s.Physics3D;
        var d3 = d.Physics3D;
        var j2 = p3.Jitter2;
        var dj2 = d3.Jitter2;
        var jo = p3.Jolt;
        var djo = d3.Jolt;
        var p2 = s.Physics2D;
        var d2 = d.Physics2D;
        var changedJitter2 = j2.SolverIterations != dj2.SolverIterations || j2.RelaxationIterations != dj2.RelaxationIterations;
        var changedJolt = jo.VelocitySteps != djo.VelocitySteps || jo.PositionSteps != djo.PositionSteps || jo.MaxBodies != djo.MaxBodies
                          || jo.MaxBodyPairs != djo.MaxBodyPairs || jo.MaxContactConstraints != djo.MaxContactConstraints
                          || jo.TempAllocatorMB != djo.TempAllocatorMB || jo.ContactEventCapacity != djo.ContactEventCapacity;
        var changed3 = p3.Backend != d3.Backend || p3.Gravity != d3.Gravity || p3.SubstepCount != d3.SubstepCount
                       || p3.AllowDeactivation != d3.AllowDeactivation || p3.MultiThreaded != d3.MultiThreaded || p3.Deterministic != d3.Deterministic
                       || changedJitter2 || changedJolt;
        var changed2 = p2.Gravity != d2.Gravity || p2.PixelsPerMeter != d2.PixelsPerMeter || p2.SubstepCount != d2.SubstepCount
                       || p2.AllowSleep != d2.AllowSleep || p2.EnableContinuous != d2.EnableContinuous;
        if (s.TicksPerSecond == d.TicksPerSecond && s.MaxStepsPerFrame == d.MaxStepsPerFrame && !changed3 && !changed2)
            return;

        w.WriteStartObject("physics");
        if (s.TicksPerSecond != d.TicksPerSecond)
            w.WriteNumber("ticksPerSecond", s.TicksPerSecond);
        if (s.MaxStepsPerFrame != d.MaxStepsPerFrame)
            w.WriteNumber("maxStepsPerFrame", s.MaxStepsPerFrame);
        if (changed3)
        {
            w.WriteStartObject("3d");
            if (p3.Backend != d3.Backend)
                w.WriteString("backend", BackendName(p3.Backend));
            if (p3.Gravity != d3.Gravity)
                WriteFloats(w, "gravity", [p3.Gravity.X, p3.Gravity.Y, p3.Gravity.Z]);
            if (p3.SubstepCount != d3.SubstepCount)
                w.WriteNumber("substeps", p3.SubstepCount);
            if (p3.AllowDeactivation != d3.AllowDeactivation)
                w.WriteBoolean("allowDeactivation", p3.AllowDeactivation);
            if (p3.MultiThreaded != d3.MultiThreaded)
                w.WriteBoolean("multiThreaded", p3.MultiThreaded);
            if (p3.Deterministic != d3.Deterministic)
                w.WriteBoolean("deterministic", p3.Deterministic);
            if (changedJitter2)
            {
                w.WriteStartObject("jitter2");
                if (j2.SolverIterations != dj2.SolverIterations)
                    w.WriteNumber("solverIterations", j2.SolverIterations);
                if (j2.RelaxationIterations != dj2.RelaxationIterations)
                    w.WriteNumber("relaxationIterations", j2.RelaxationIterations);
                w.WriteEndObject();
            }

            if (changedJolt)
            {
                w.WriteStartObject("jolt");
                if (jo.VelocitySteps != djo.VelocitySteps)
                    w.WriteNumber("velocitySteps", jo.VelocitySteps);
                if (jo.PositionSteps != djo.PositionSteps)
                    w.WriteNumber("positionSteps", jo.PositionSteps);
                if (jo.MaxBodies != djo.MaxBodies)
                    w.WriteNumber("maxBodies", jo.MaxBodies);
                if (jo.MaxBodyPairs != djo.MaxBodyPairs)
                    w.WriteNumber("maxBodyPairs", jo.MaxBodyPairs);
                if (jo.MaxContactConstraints != djo.MaxContactConstraints)
                    w.WriteNumber("maxContactConstraints", jo.MaxContactConstraints);
                if (jo.TempAllocatorMB != djo.TempAllocatorMB)
                    w.WriteNumber("tempAllocatorMB", jo.TempAllocatorMB);
                if (jo.ContactEventCapacity != djo.ContactEventCapacity)
                    w.WriteNumber("contactEventCapacity", jo.ContactEventCapacity);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        if (changed2)
        {
            w.WriteStartObject("2d");
            if (p2.Gravity != d2.Gravity)
                WriteFloats(w, "gravity", [p2.Gravity.X, p2.Gravity.Y]);
            if (p2.PixelsPerMeter != d2.PixelsPerMeter)
                w.WriteNumber("pixelsPerMeter", p2.PixelsPerMeter);
            if (p2.SubstepCount != d2.SubstepCount)
                w.WriteNumber("substeps", p2.SubstepCount);
            if (p2.AllowSleep != d2.AllowSleep)
                w.WriteBoolean("allowSleep", p2.AllowSleep);
            if (p2.EnableContinuous != d2.EnableContinuous)
                w.WriteBoolean("continuous", p2.EnableContinuous);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static string BackendName(PhysicsBackendKind kind) => kind switch
    {
        PhysicsBackendKind.Jitter2 => "jitter2",
        PhysicsBackendKind.Jolt => "jolt",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
```

In `ReadPhysics` replace the `if (Object(o, "3d") is { } p3) { … }` block (lines 558-571) with:

```csharp
            if (Object(o, "3d") is { } p3)
            {
                Known(p3, "physics.3d.", "backend", "gravity", "substeps", "allowDeactivation", "multiThreaded", "deterministic", "jitter2", "jolt");
                var p = s.Physics3D;
                p.Backend = EnumValue(p3, "backend", "physics.3d.backend", p.Backend);
                var g = Floats(p3, "gravity", "physics.3d.gravity", 3);
                if (g is not null)
                    p.Gravity = new Vector3(g[0], g[1], g[2]);
                p.SubstepCount = Int(p3, "substeps", "physics.3d.substeps", p.SubstepCount, 1, 64);
                p.AllowDeactivation = Bool(p3, "allowDeactivation", "physics.3d.allowDeactivation", p.AllowDeactivation);
                p.MultiThreaded = Bool(p3, "multiThreaded", "physics.3d.multiThreaded", p.MultiThreaded);
                p.Deterministic = Bool(p3, "deterministic", "physics.3d.deterministic", p.Deterministic);
                if (Object(p3, "jitter2") is { } j2)
                {
                    Known(j2, "physics.3d.jitter2.", "solverIterations", "relaxationIterations");
                    p.Jitter2.SolverIterations = Int(j2, "solverIterations", "physics.3d.jitter2.solverIterations", p.Jitter2.SolverIterations, 1, 256);
                    p.Jitter2.RelaxationIterations = Int(j2, "relaxationIterations", "physics.3d.jitter2.relaxationIterations", p.Jitter2.RelaxationIterations, 0, 256);
                }

                if (Object(p3, "jolt") is { } jo)
                {
                    Known(jo, "physics.3d.jolt.", "velocitySteps", "positionSteps", "maxBodies", "maxBodyPairs", "maxContactConstraints", "tempAllocatorMB",
                        "contactEventCapacity");
                    var j = p.Jolt;
                    j.VelocitySteps = Int(jo, "velocitySteps", "physics.3d.jolt.velocitySteps", j.VelocitySteps, 1, 255);
                    j.PositionSteps = Int(jo, "positionSteps", "physics.3d.jolt.positionSteps", j.PositionSteps, 1, 255);
                    j.MaxBodies = Int(jo, "maxBodies", "physics.3d.jolt.maxBodies", j.MaxBodies, 1, 8_388_607); // Jolt's BodyID has a 23-bit index
                    j.MaxBodyPairs = Int(jo, "maxBodyPairs", "physics.3d.jolt.maxBodyPairs", j.MaxBodyPairs, 1, 16_777_216);
                    j.MaxContactConstraints = Int(jo, "maxContactConstraints", "physics.3d.jolt.maxContactConstraints", j.MaxContactConstraints, 1, 16_777_216);
                    j.TempAllocatorMB = Int(jo, "tempAllocatorMB", "physics.3d.jolt.tempAllocatorMB", j.TempAllocatorMB, 1, 4096);
                    j.ContactEventCapacity = Int(jo, "contactEventCapacity", "physics.3d.jolt.contactEventCapacity", j.ContactEventCapacity, 1, 16_777_216);
                }
            }
```

Template: `Templates/MainframeEngine.Templates/content/mfgame/project.mfproj:2` → `"format": 4,`.

- [ ] **Step 4: Run the project tests**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Project"` → all pass.

- [ ] **Step 5: Physics suite, then commit**

```bash
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics"
git add MainframeEngine/Src/Project/ProjectSettingsFormat.cs Templates/MainframeEngine.Templates/content/mfgame/project.mfproj Tests/MainframeEngine.Tests/Project/ProjectSettingsTests.cs
git commit -F- <<'EOF'
Read and write physics.3d.backend and the jitter2/jolt groups (project format 4)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 5: Editor Project Settings → Physics

**Files:**
- Modify: `MainframeEngine.Editor/Src/Projects/ProjectSettingsModel.cs:32-41` (`ProjectSetting`), `:116-131` (physics entries)
- Modify: `MainframeEngine.Editor/Src/UI/ProjectSettingsDialog.cs:106-109`
- Modify: `Tests/MainframeEngine.Editor.Tests/Projects/ProjectWorkflowTests.cs:330-332`

**Interfaces:**
- Consumes: Task 3 settings.
- Produces: `ProjectSetting(..., Func<ProjectSettings, bool>? Visible = null)`; keys `physics.3d.backend`, `physics.3d.jitter2.*`, `physics.3d.jolt.*`.

- [ ] **Step 1: Write the failing tests.** In `EveryProjectSettingRoundTripsThroughTheModel` replace lines 331-332 (`physics.3d.solverIterations`/`relaxationIterations`) with:

```csharp
            ["physics.3d.backend"] = "Jolt",
            ["physics.3d.jitter2.solverIterations"] = "8",
            ["physics.3d.jitter2.relaxationIterations"] = "3",
            ["physics.3d.jolt.velocitySteps"] = "12",
            ["physics.3d.jolt.positionSteps"] = "3",
            ["physics.3d.jolt.maxBodies"] = "4096",
            ["physics.3d.jolt.maxBodyPairs"] = "8192",
            ["physics.3d.jolt.maxContactConstraints"] = "2048",
            ["physics.3d.jolt.tempAllocatorMB"] = "32",
            ["physics.3d.jolt.contactEventCapacity"] = "1024",
```

and add after that test:

```csharp
    [Fact]
    public void PhysicsShowsTheSharedKeysAndTheSelectedBackendsGroup()
    {
        var model = new ProjectSettingsModel(ProjectSettings.Load(_project.Root), _project.Abs(ProjectSettings.FileName));
        bool Shown(string key) => ProjectSettingsModel.Find(key)!.Visible?.Invoke(model.Current) ?? true;

        Assert.True(Shown("physics.3d.backend"));
        Assert.True(Shown("physics.3d.gravity"));
        Assert.True(Shown("physics.3d.jitter2.solverIterations"));
        Assert.False(Shown("physics.3d.jolt.velocitySteps"));

        Assert.Null(model.Set("physics.3d.backend", "Jolt"));
        Assert.False(Shown("physics.3d.jitter2.solverIterations"));
        Assert.True(Shown("physics.3d.jolt.velocitySteps"));
        Assert.True(Shown("physics.3d.substeps"));
        Assert.NotNull(model.Set("physics.3d.backend", "Havok"));
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~ProjectWorkflowTests"` → build error (`Visible`) / unknown keys.

- [ ] **Step 3: Implement.** `ProjectSetting` (lines 31-41):

```csharp
/// <summary>
/// One editable value of <c>project.mfproj</c>: its key (<c>window.width</c>), section, label, editor and accessors.
/// <see cref="Visible"/> (null: always) hides a setting that does not apply, such as another physics backend's group.
/// </summary>
public sealed record ProjectSetting(
    string Key,
    string Section,
    string Label,
    SettingKind Kind,
    Func<ProjectSettings, string> Get,
    Action<ProjectSettings, string>? Set,
    string Tooltip = "",
    IReadOnlyList<string>? Choices = null,
    string? Filter = null,
    Func<ProjectSettings, bool>? Visible = null);
```

In `Settings`, insert before `physics.3d.gravity` (line 120):

```csharp
        new("physics.3d.backend", "Physics", "3D Backend", SettingKind.Choice, static s => s.Physics.Physics3D.Backend.ToString(),
            static (s, v) => s.Physics.Physics3D.Backend = Enum.Parse<PhysicsBackendKind>(v, ignoreCase: true),
            "Jitter2 (pure C#, every platform) or Jolt (through BRUTAL). Jolt arrives with B3: until then a Jolt project runs Jitter2 and logs a warning.",
            Choices: Enum.GetNames<PhysicsBackendKind>()),
```

delete the `physics.3d.solverIterations` and `physics.3d.relaxationIterations` entries (lines 124-127), and insert after `physics.3d.deterministic` (line 133):

```csharp
        new("physics.3d.jitter2.solverIterations", "Physics", "Jitter2 Solver Iterations", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.Jitter2.SolverIterations), static (s, v) => s.Physics.Physics3D.Jitter2.SolverIterations = ParseInt(v),
            "Constraint iterations per sub-step (Jitter2 default 6).", Visible: static s => s.Physics.Physics3D.Backend == PhysicsBackendKind.Jitter2),
        new("physics.3d.jitter2.relaxationIterations", "Physics", "Jitter2 Relaxation Iterations", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.Jitter2.RelaxationIterations), static (s, v) => s.Physics.Physics3D.Jitter2.RelaxationIterations = ParseInt(v),
            "Velocity relaxation iterations per sub-step (Jitter2 default 4).", Visible: static s => s.Physics.Physics3D.Backend == PhysicsBackendKind.Jitter2),
        new("physics.3d.jolt.velocitySteps", "Physics", "Jolt Velocity Steps", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.Jolt.VelocitySteps), static (s, v) => s.Physics.Physics3D.Jolt.VelocitySteps = ParseInt(v),
            "Velocity solver steps per sub-step (Jolt default 10).", Visible: static s => s.Physics.Physics3D.Backend == PhysicsBackendKind.Jolt),
        new("physics.3d.jolt.positionSteps", "Physics", "Jolt Position Steps", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.Jolt.PositionSteps), static (s, v) => s.Physics.Physics3D.Jolt.PositionSteps = ParseInt(v),
            "Position solver steps per sub-step (Jolt default 2).", Visible: static s => s.Physics.Physics3D.Backend == PhysicsBackendKind.Jolt),
        new("physics.3d.jolt.maxBodies", "Physics", "Jolt Max Bodies", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.Jolt.MaxBodies), static (s, v) => s.Physics.Physics3D.Jolt.MaxBodies = ParseInt(v),
            "Bodies one world can hold.", Visible: static s => s.Physics.Physics3D.Backend == PhysicsBackendKind.Jolt),
        new("physics.3d.jolt.maxBodyPairs", "Physics", "Jolt Max Body Pairs", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.Jolt.MaxBodyPairs), static (s, v) => s.Physics.Physics3D.Jolt.MaxBodyPairs = ParseInt(v),
            "Broad-phase pairs per step.", Visible: static s => s.Physics.Physics3D.Backend == PhysicsBackendKind.Jolt),
        new("physics.3d.jolt.maxContactConstraints", "Physics", "Jolt Max Contact Constraints", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.Jolt.MaxContactConstraints), static (s, v) => s.Physics.Physics3D.Jolt.MaxContactConstraints = ParseInt(v),
            "Contact constraints per step.", Visible: static s => s.Physics.Physics3D.Backend == PhysicsBackendKind.Jolt),
        new("physics.3d.jolt.tempAllocatorMB", "Physics", "Jolt Temp Allocator (MB)", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.Jolt.TempAllocatorMB), static (s, v) => s.Physics.Physics3D.Jolt.TempAllocatorMB = ParseInt(v),
            "Per-world scratch memory.", Visible: static s => s.Physics.Physics3D.Backend == PhysicsBackendKind.Jolt),
        new("physics.3d.jolt.contactEventCapacity", "Physics", "Jolt Contact Events", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.Jolt.ContactEventCapacity), static (s, v) => s.Physics.Physics3D.Jolt.ContactEventCapacity = ParseInt(v),
            "Contact events recorded per step for contact monitors and areas (doubled on overflow).",
            Visible: static s => s.Physics.Physics3D.Backend == PhysicsBackendKind.Jolt),
```

`ProjectSettingsDialog.cs:106-109` — the default case becomes:

```csharp
                default:
                    foreach (var setting in ProjectSettingsModel.Settings)
                        if (setting.Section == _section && (setting.Visible?.Invoke(model.Current) ?? true))
                            RenderSetting(body, setting, setting.Get(model.Current), Workspace.Session.ProjectRoot);
                    break;
```

(`Render` already runs on every `History.Changed`, so switching the backend swaps the group.)

- [ ] **Step 4: Run the editor tests**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~ProjectWorkflowTests|FullyQualifiedName~ProjectSettings"` → all pass.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine.Editor/Src/Projects/ProjectSettingsModel.cs MainframeEngine.Editor/Src/UI/ProjectSettingsDialog.cs Tests/MainframeEngine.Editor.Tests/Projects/ProjectWorkflowTests.cs
git commit -F- <<'EOF'
Show the 3D backend and its settings group in Project Settings › Physics

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 6: The seam types and the layer rule

**Files:**
- Create: `MainframeEngine/Src/Physics/3D/Backend/PhysicsBackend3D.cs`, `ShapeDescription3D.cs`, `BackendResults3D.cs` (exactly the listings in **The contract** above)
- Modify: `MainframeEngine/Src/Physics/3D/PhysicsSpace3D.cs` (add `Collides`, `BakeShapeTransform`; `AngularVelocityBetween` at line 759 `private static` → `internal static`)
- Create: `Tests/MainframeEngine.Tests/Physics/PhysicsLayerRuleTests.cs`

**Interfaces:**
- Consumes: `CollisionLayers.ShouldCollide` (`PhysicsTypes.cs:43-44`), `Basis` (`Scene/Transform3D.cs:10`).
- Produces: the contract types; `internal static bool PhysicsSpace3D.Collides(uint layerA, uint maskA, bool areaA, uint layerB, uint maskB, bool areaB)`; `internal static void PhysicsSpace3D.BakeShapeTransform(Vector3 bodyScale, in Transform3D local, out Basis linear, out Vector3 translation)`; `internal static Vector3 PhysicsSpace3D.AngularVelocityBetween(Quaternion from, Quaternion to)`.

`PhysicsBackend3D.Create` references `Jitter2Backend`, which Task 8 adds: in this task write `Create`'s Jitter2 arm as
`PhysicsBackendKind.Jitter2 => throw new NotSupportedException("The Jitter2 backend is added in the next task."),` and
Task 8 Step 6 replaces it with `new Jitter2Backend(settings)`.

- [ ] **Step 1: Write the failing test**

```csharp
namespace MainframeEngine.Tests.Physics;

/// <summary>The layer rule both backends' filters call (ADR 0021).</summary>
public sealed class PhysicsLayerRuleTests
{
    [Theory]
    [InlineData(1u, 1u, 1u, 1u, true)]
    [InlineData(1u, 0u, 2u, 1u, true)]   // B scans A's layer: enough
    [InlineData(1u, 2u, 2u, 0u, true)]   // A scans B's layer: enough
    [InlineData(1u, 0u, 2u, 0u, false)]
    [InlineData(4u, 3u, 3u, 3u, true)]
    [InlineData(4u, 3u, 8u, 3u, false)]
    public void BodiesCollideWhenEitherScansTheOther(uint layerA, uint maskA, uint layerB, uint maskB, bool expected)
    {
        Assert.Equal(expected, PhysicsSpace3D.Collides(layerA, maskA, false, layerB, maskB, false));
        Assert.Equal(expected, PhysicsSpace3D.Collides(layerB, maskB, false, layerA, maskA, false));
        Assert.Equal(expected, CollisionLayers.ShouldCollide(layerA, maskA, layerB, maskB));
    }

    [Fact]
    public void AreasDetectBodiesByTheirMaskOnlyAndNeverAreas()
    {
        Assert.True(PhysicsSpace3D.Collides(0, 1, true, 1, 0, false));  // the area's mask has the body's layer
        Assert.False(PhysicsSpace3D.Collides(1, 0, true, 0, 1, false)); // the body's mask scanning the area's layer is not enough
        Assert.True(PhysicsSpace3D.Collides(1, 0, false, 0, 1, true));  // either argument order
        Assert.False(PhysicsSpace3D.Collides(0, 1, false, 1, 0, true));
        Assert.False(PhysicsSpace3D.Collides(1, 1, true, 1, 1, true));  // areas never detect areas
    }
}
```

- [ ] **Step 2: Run it to see it fail** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsLayerRuleTests"` → build error.

- [ ] **Step 3: Implement.** Create the three contract files. In `PhysicsSpace3D.cs` add `using System.Runtime.CompilerServices;` and, in the class body after `AngularVelocityBetween` (now `internal static`):

```csharp
    /// <summary>
    /// The layer rule (ADR 0021), shared by every backend's filters: two bodies collide when either one's mask has the
    /// other's layer; an area detects a body when the area's mask has the body's layer; areas never detect areas.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Collides(uint layerA, uint maskA, bool areaA, uint layerB, uint maskB, bool areaB)
    {
        if (areaA)
            return !areaB && (maskA & layerB) != 0;
        if (areaB)
            return (maskB & layerA) != 0;
        return CollisionLayers.ShouldCollide(layerA, maskA, layerB, maskB);
    }

    /// <summary>A shape child's transform in its body's frame with the body's global scale baked in (as the shapes are built).</summary>
    internal static void BakeShapeTransform(Vector3 bodyScale, in Transform3D local, out Basis linear, out Vector3 translation)
    {
        var scaleBasis = new Basis(Vector3.UnitX * bodyScale.X, Vector3.UnitY * bodyScale.Y, Vector3.UnitZ * bodyScale.Z);
        linear = scaleBasis * local.Basis;
        translation = local.Origin * bodyScale;
    }
```

(`BakeShapeTransform` is exactly `RebuildShapes`' `scaleBasis * local.Basis` / `local.Origin * scale` from lines 588 and 605; Task 9 makes `RebuildShapes` call it.)

- [ ] **Step 4: Run** `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsLayerRuleTests"` → pass; then the physics suite → pass; `just build` → 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Backend/ MainframeEngine/Src/Physics/3D/PhysicsSpace3D.cs Tests/MainframeEngine.Tests/Physics/PhysicsLayerRuleTests.cs
git commit -F- <<'EOF'
Add the PhysicsBackend3D seam types and the shared layer rule

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 7: Neutral shape descriptions

**Files:**
- Modify: `MainframeEngine/Src/Physics/3D/Shapes3D.cs` (expand only: the Jitter2 members stay until Task 10)
- Create: `Tests/MainframeEngine.Tests/Physics/ShapeDescription3DTests.cs`

**Interfaces:**
- Consumes: `ShapeDescription3D`, `ShapeKind3D` (Task 6), `PhysicsSpace3D.BakeShapeTransform`.
- Produces: `internal int Shape3D.ChangeCount { get; }`; `internal virtual void Shape3D.Describe(List<ShapeDescription3D> output, in Basis linear, Vector3 translation)`; `internal abstract ShapeDescription3D Shape3D.DescribeConvex(in Basis linear, Vector3 translation)`; `internal ShapeDescription3D Shape3D.DescribeQuery()`; `internal Vector3[] HeightMapShape3D.GetFaceArray()`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>What each shape resource hands a physics backend (B2): kind, sizes, transform, the resource's arrays.</summary>
public sealed class ShapeDescription3DTests
{
    private static readonly Basis Turned = Basis.FromRotationScale(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(1, 2, 3));
    private static readonly Vector3 Offset = new(1, 2, 3);

    private static ShapeDescription3D One(Shape3D shape)
    {
        var list = new List<ShapeDescription3D>();
        shape.Describe(list, Turned, Offset);
        var d = Assert.Single(list);
        Assert.Same(shape, d.Resource);
        Assert.Equal(shape.ChangeCount, d.ChangeCount);
        Assert.Equal(Turned, d.Linear);
        Assert.Equal(Offset, d.Translation);
        return d;
    }

    [Fact]
    public void Box()
    {
        var d = One(new BoxShape3D { Size = new Vector3(1, 2, 3) });
        Assert.Equal(ShapeKind3D.Box, d.Kind);
        Assert.Equal(new Vector3(1, 2, 3), d.Size);
    }

    [Fact]
    public void Sphere()
    {
        var d = One(new SphereShape3D { Radius = 0.75f });
        Assert.Equal(ShapeKind3D.Sphere, d.Kind);
        Assert.Equal(0.75f, d.Radius);
    }

    [Fact]
    public void Capsule()
    {
        var shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f };
        var d = One(shape);
        Assert.Equal(ShapeKind3D.Capsule, d.Kind);
        Assert.Equal((0.4f, 1.8f), (d.Radius, d.Height));
        Assert.Equal(shape.MidHeight, d.MidHeight); // the same expression, so the same float
        Assert.Equal(0f, One(new CapsuleShape3D { Radius = 1f, Height = 1.5f }).MidHeight);
    }

    [Fact]
    public void Cylinder()
    {
        var d = One(new CylinderShape3D { Radius = 0.3f, Height = 0.8f });
        Assert.Equal(ShapeKind3D.Cylinder, d.Kind);
        Assert.Equal((0.3f, 0.8f), (d.Radius, d.Height));
    }

    [Fact]
    public void ConvexHullSharesThePointArrayAndNeedsFourPoints()
    {
        var shape = new ConvexPolygonShape3D { Points = [new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0, 0.5f, -0.5f), new(0, 0, 0.5f)] };
        var d = One(shape);
        Assert.Equal(ShapeKind3D.ConvexHull, d.Kind);
        Assert.Same(shape.Points, d.Points);

        var e = Assert.Throws<InvalidOperationException>(() => new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY] }.Describe([], Turned, Offset));
        Assert.Equal("ConvexPolygonShape3D needs at least 4 points.", e.Message);
    }

    [Fact]
    public void ConcaveMeshSharesTheFaceArray()
    {
        var shape = new ConcavePolygonShape3D { Faces = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] };
        var d = One(shape);
        Assert.Equal(ShapeKind3D.ConcaveMesh, d.Kind);
        Assert.Same(shape.Faces, d.Points);
    }

    [Fact]
    public void HeightMapHandsItsCachedTriangleList()
    {
        var shape = new HeightMapShape3D { MapWidth = 3, MapDepth = 2, MapData = [0, 1, 0, 1, 2, 1] };
        var d = One(shape);
        Assert.Equal(ShapeKind3D.HeightMap, d.Kind);
        Assert.Equal(shape.GetFaces().ToArray(), d.Points);
        Assert.Same(d.Points, One(shape).Points); // built once until the map changes
        Assert.Equal(2 * 1 * 6, d.Points!.Length);
    }

    [Fact]
    public void QueriesDescribeConvexShapesAtTheOriginAndRejectConcaveOnes()
    {
        var d = new SphereShape3D { Radius = 2 }.DescribeQuery();
        Assert.Equal(Basis.Identity, d.Linear);
        Assert.Equal(Vector3.Zero, d.Translation);
        var e = Assert.Throws<NotSupportedException>(() => new HeightMapShape3D().DescribeQuery());
        Assert.Equal("HeightMapShape3D cannot be used as a query shape (it is concave).", e.Message);
    }

    [Fact]
    public void ChangeCountFollowsEveryGeometryEdit()
    {
        var box = new BoxShape3D();
        var count = box.ChangeCount;
        box.Size = Vector3.One; // no change: no bump
        Assert.Equal(count, box.ChangeCount);
        box.Size = new Vector3(2);
        Assert.Equal(count + 1, box.ChangeCount);

        // Terrain3D rewrites the faces in place and assigns the same array again: that must still count.
        var mesh = new ConcavePolygonShape3D { Faces = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] };
        var faces = mesh.Faces;
        count = mesh.ChangeCount;
        faces[1] = Vector3.UnitY;
        mesh.Faces = faces;
        Assert.Equal(count + 1, mesh.ChangeCount);

        var map = new HeightMapShape3D();
        count = map.ChangeCount;
        map.MapData = [1, 1, 1, 1];
        map.MapWidth = 2; // unchanged
        map.MapDepth = 3;
        Assert.Equal(count + 2, map.ChangeCount);
    }

    [Fact]
    public void TheBodysScaleIsBakedIntoTheChildTransform()
    {
        var local = Transform3D.FromTrs(new Vector3(1, 1, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f), new Vector3(1, 2, 1));
        PhysicsSpace3D.BakeShapeTransform(new Vector3(2, 3, 4), local, out var linear, out var translation);
        Assert.Equal(new Vector3(2, 3, 4), translation);
        var scale = new Basis(new Vector3(2, 0, 0), new Vector3(0, 3, 0), new Vector3(0, 0, 4));
        Assert.Equal(scale * local.Basis, linear);
    }
}
```

- [ ] **Step 2: Run them to see them fail** → build errors (`Describe`, `ChangeCount`).

- [ ] **Step 3: Implement in `Shapes3D.cs`** (keep `CreateShapes`, `CreateBaseShape`, `GetQueryShape`, `AddTriangleShapes` and the Jitter2 usings for now). In `Shape3D` add:

```csharp
    /// <summary>Bumped by every property change that affects the geometry (backends cache native shapes by it).</summary>
    internal int ChangeCount { get; private set; }

    /// <summary>
    /// Adds this shape's neutral description for one attachment: <paramref name="linear"/> (rotation × scale) and
    /// <paramref name="translation"/> place it in its body's frame.
    /// </summary>
    internal virtual void Describe(List<ShapeDescription3D> output, in Basis linear, Vector3 translation) =>
        output.Add(DescribeConvex(linear, translation));

    /// <summary>The description of a convex shape (concave shapes throw <see cref="NotSupportedException"/>).</summary>
    internal abstract ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation);

    /// <summary>The shape at its own origin and scale 1, for shape casts and overlap queries.</summary>
    internal ShapeDescription3D DescribeQuery()
    {
        if (IsConcave)
            throw new NotSupportedException($"{GetType().Name} cannot be used as a query shape (it is concave).");
        return DescribeConvex(Basis.Identity, Vector3.Zero);
    }

    private protected ShapeDescription3D Description(ShapeKind3D kind, in Basis linear, Vector3 translation) =>
        new() { Kind = kind, Resource = this, ChangeCount = ChangeCount, Linear = linear, Translation = translation };
```

and change `OnShapeChanged` (lines 48-52) to:

```csharp
    protected void OnShapeChanged()
    {
        _queryShape = null;
        ChangeCount++;
        EmitChanged();
    }
```

Overrides (each next to the class's `CreateBaseShape`):

```csharp
    // BoxShape3D
    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        Description(ShapeKind3D.Box, linear, translation) with { Size = Size };

    // SphereShape3D
    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        Description(ShapeKind3D.Sphere, linear, translation) with { Radius = Radius };

    // CapsuleShape3D
    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        Description(ShapeKind3D.Capsule, linear, translation) with { Radius = Radius, Height = Height };

    // CylinderShape3D
    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        Description(ShapeKind3D.Cylinder, linear, translation) with { Radius = Radius, Height = Height };

    // ConvexPolygonShape3D (the 4-point check moves here from CreateBaseShape, same exception and message)
    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation)
    {
        if (Points.Length < 4)
            throw new InvalidOperationException("ConvexPolygonShape3D needs at least 4 points.");
        return Description(ShapeKind3D.ConvexHull, linear, translation) with { Points = Points };
    }

    // ConcavePolygonShape3D
    internal override void Describe(List<ShapeDescription3D> output, in Basis linear, Vector3 translation) =>
        output.Add(Description(ShapeKind3D.ConcaveMesh, linear, translation) with { Points = Faces });

    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        throw new NotSupportedException("ConcavePolygonShape3D has no single convex base shape.");

    // HeightMapShape3D
    internal override void Describe(List<ShapeDescription3D> output, in Basis linear, Vector3 translation) =>
        output.Add(Description(ShapeKind3D.HeightMap, linear, translation) with { Points = GetFaceArray() });

    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        throw new NotSupportedException("HeightMapShape3D has no single convex base shape.");
```

`HeightMapShape3D.GetFaces` (lines 373-401) becomes `public ReadOnlySpan<Vector3> GetFaces() => GetFaceArray();` plus
`internal Vector3[] GetFaceArray()` holding the existing body unchanged (it returns `_faces`/`faces`, both `Vector3[]`).
`ConvexPolygonShape3D.CreateBaseShape` keeps its own `< 4` check until Task 10 deletes it.

- [ ] **Step 4: Run** `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~ShapeDescription3DTests"` → 10 pass; physics suite → pass; trace gate → pass.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Shapes3D.cs Tests/MainframeEngine.Tests/Physics/ShapeDescription3DTests.cs
git commit -F- <<'EOF'
Describe 3D shape resources as neutral ShapeDescription3Ds with a change count

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 8: The Jitter folder and `Jitter2Backend`

**Files:**
- Move: `git mv MainframeEngine/Src/Physics/3D/JitterMath.cs MainframeEngine/Src/Physics/3D/Jitter/JitterMath.cs`; `git mv MainframeEngine/Src/Physics/3D/SphereContactFilter.cs MainframeEngine/Src/Physics/3D/Jitter/SphereContactFilter.cs` (contents unchanged)
- Create: `MainframeEngine/Src/Physics/3D/Jitter/JitterLogMute.cs`, `JitterHull.cs`, `Jitter2Backend.cs`
- Modify: `PhysicsSpace3D.cs` (delete lines 1250-1267, the moved `JitterLogMute`), `PhysicsServer3D.cs:20-37` (static ctor body → `JitterLog.EnsureInstalled();`), `Backend/PhysicsBackend3D.cs` (`Create`'s Jitter2 arm)
- Create: `Tests/MainframeEngine.Tests/Physics/Jitter2BackendTests.cs`

**Interfaces:**
- Consumes: the contract (Task 6), `Shape3D.Describe`/`DescribeQuery` (Task 7), `PhysicsSpace3D.Collides`/`AngularVelocityBetween`/`MeasureLibraryAllocations`, `PhysicsSettings3D.Jitter2` (Task 3), `JitterMath`, `SphereContactFilter`.
- Produces: `internal sealed class Jitter2Backend : PhysicsBackend3D` (`public Jitter2Backend(PhysicsSettings3D settings)`), `internal sealed class JitterBody`, `internal static class JitterLog` (`EnsureInstalled()`), `internal readonly ref struct JitterLogMute`, `internal static class JitterHull` (`Vector3[] Tessellate(Vector3[] points, int subdivisions)`).

- [ ] **Step 1: Move the two files**, build (`just build`), physics suite → pass.

- [ ] **Step 2: Create `Jitter/JitterLogMute.cs`** (the listener moved from `PhysicsServer3D.cs:22-36`, the mute from `PhysicsSpace3D.cs:1250-1267`, both unchanged inside):

```csharp
namespace MainframeEngine;

/// <summary>Routes Jitter2's log to <see cref="Log"/> (<c>[Jitter2] …</c>), installed once per process.</summary>
internal static class JitterLog
{
    static JitterLog()
    {
        Jitter2.Logger.Listener = static (level, message) =>
        {
            switch (level)
            {
                case Jitter2.Logger.LogLevel.Error:
                    Log.Error($"[Jitter2] {message}");
                    break;
                case Jitter2.Logger.LogLevel.Warning:
                    Log.Warning($"[Jitter2] {message}");
                    break;
                default:
                    Log.Debug($"[Jitter2] {message}");
                    break;
            }
        };
    }

    /// <summary>Runs the static constructor (a no-op afterwards).</summary>
    public static void EnsureInstalled()
    {
    }
}

/// <summary>
/// Detaches Jitter2's log listener for the scope of a character query (cast, recovery, settle). Jitter2 formats a warning
/// string for every EPA that does not converge (<c>string.Format</c>, only while a listener is set); character queries
/// expect that on dense terrain (ADR 0128: the next query takes over), so warning about it would allocate on the main
/// thread every few seconds of walking and break the 0 B frame. Queries run on the main thread outside the world step.
/// </summary>
internal readonly ref struct JitterLogMute
{
    private readonly Action<Jitter2.Logger.LogLevel, string>? _listener;

    public JitterLogMute()
    {
        _listener = Jitter2.Logger.Listener;
        Jitter2.Logger.Listener = null;
    }

    public void Dispose() => Jitter2.Logger.Listener = _listener;
}
```

Delete `PhysicsSpace3D.cs:1250-1267`. Replace the body of `PhysicsServer3D`'s static constructor (lines 20-37) with
`static PhysicsServer3D() => JitterLog.EnsureInstalled();` (the listener is installed at the same moment as before;
Task 10 removes this line once the backend installs it).

- [ ] **Step 3: Create `Jitter/JitterHull.cs`** (from `Shapes3D.cs:235-246`):

```csharp
using System.Numerics;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>Convex hull triangles for collision-shape debug drawing (Jitter2's <c>ShapeHelper.Tessellate</c>, kept so the drawn hull is unchanged).</summary>
internal static class JitterHull
{
    /// <summary>The hull of <paramref name="source"/> as a triangle list (3 vertices each); empty below 4 points.</summary>
    public static Vector3[] Tessellate(Vector3[] source, int subdivisions)
    {
        if (source.Length < 4)
            return [];
        Span<JVector> points = source.Length <= 256 ? stackalloc JVector[source.Length] : new JVector[source.Length];
        for (var i = 0; i < source.Length; i++)
            points[i] = source[i].ToJ();
        var hull = ShapeHelper.Tessellate(points, subdivisions);
        var triangles = new Vector3[hull.Count * 3];
        for (var i = 0; i < hull.Count; i++)
        {
            triangles[i * 3] = hull[i].V0.ToNumerics();
            triangles[i * 3 + 1] = hull[i].V1.ToNumerics();
            triangles[i * 3 + 2] = hull[i].V2.ToNumerics();
        }

        return triangles;
    }
}
```

- [ ] **Step 4: Write the failing backend tests**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Physics;

/// <summary>The Jitter2 backend on its own, through the members the space uses (B2).</summary>
public sealed class Jitter2BackendTests : IDisposable
{
    private const float Dt = 1f / 60f;
    private static readonly BodyProperties3D Simulated = new(true, 0f, 0f, 1f, AxisLock.None, false, true);
    private readonly Jitter2Backend _backend = new(new PhysicsSettings3D { MultiThreaded = false });
    private readonly List<ShapeDescription3D> _shapes = [];
    private readonly List<ShapeError3D> _errors = [];

    public void Dispose() => _backend.Dispose();

    private BackendBody Create(PhysicsBodyKind kind, Vector3 position, uint layer = 1, uint mask = 1) =>
        _backend.CreateBody(new BodyDescription3D(kind, position, Quaternion.Identity, layer, mask,
            kind == PhysicsBodyKind.Dynamic ? Simulated : default, PhysicsMaterial.DefaultFriction, 0f));

    private void SetShapes(BackendBody body, PhysicsBodyKind kind)
    {
        _errors.Clear();
        _backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), kind == PhysicsBodyKind.Dynamic ? 1f : 0f, _errors);
        _shapes.Clear();
    }

    private BackendBody Add(PhysicsBodyKind kind, Vector3 position, Shape3D shape, uint layer = 1, uint mask = 1)
    {
        var body = Create(kind, position, layer, mask);
        shape.Describe(_shapes, Basis.Identity, Vector3.Zero);
        SetShapes(body, kind);
        Assert.Empty(_errors);
        return body;
    }

    private BackendBody Floor() => Add(PhysicsBodyKind.Static, new Vector3(0, -0.5f, 0), new BoxShape3D { Size = new Vector3(40, 1, 40) });

    private void Run(int steps)
    {
        for (var i = 0; i < steps; i++)
            _backend.Step(Dt);
    }

    [Fact]
    public void HandlesStartAtOneAndFreedOnesAreReused()
    {
        Assert.True(default(BackendBody).IsNone);
        var a = Floor();
        var b = Add(PhysicsBodyKind.Static, new Vector3(5, 0, 0), new BoxShape3D());
        Assert.Equal((1, 2), (a.Index, b.Index));
        _backend.DestroyBody(a);
        Assert.Equal(1, Add(PhysicsBodyKind.Static, new Vector3(9, 0, 0), new BoxShape3D()).Index);
    }

    [Fact]
    public void ADynamicBoxFallsRestsAndSleepsOnAStaticFloor()
    {
        Floor();
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 2, 0), new BoxShape3D());
        Run(20);
        Assert.True(_backend.GetLinearVelocity(box).Y < 0);
        Run(300);
        _backend.GetPose(box, out var position, out _);
        Assert.InRange(position.Y, 0.45f, 0.55f);
        Assert.True(_backend.IsSleeping(box));
        var asleep = _backend.ActiveBodyCount;
        _backend.Wake(box);
        Assert.False(_backend.IsSleeping(box));
        Assert.Equal(asleep + 1, _backend.ActiveBodyCount);
    }

    [Fact]
    public void RaysReportBodyAndShapeIndexAndHonourMaskAndExclude()
    {
        var floor = Floor();
        Assert.True(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 1, default, true, out var hit));
        Assert.Equal(floor, hit.Body);
        Assert.Equal(0, hit.ShapeIndex);
        Assert.Equal(0f, hit.Position.Y, 3);
        Assert.Equal(Vector3.UnitY, hit.Normal);
        Assert.Equal(0.5f, hit.Fraction, 3);
        Assert.False(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 2, default, true, out _));
        Assert.False(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 1, floor, true, out _));

        var compound = Create(PhysicsBodyKind.Static, new Vector3(5, 2, 0));
        new BoxShape3D().Describe(_shapes, Basis.Identity, new Vector3(-1, 0, 0));
        new SphereShape3D().Describe(_shapes, Basis.Identity, new Vector3(1, 0, 0));
        SetShapes(compound, PhysicsBodyKind.Static);
        Assert.True(_backend.RayCast(new Vector3(6, 5, 0), new Vector3(0, -10, 0), 1, default, true, out hit));
        Assert.Equal((compound, 1), (hit.Body, hit.ShapeIndex));
    }

    [Fact]
    public void ContactsAndAreaOverlapsAreCollectedAfterAStep()
    {
        var floor = Floor();
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 0.5f, 0), new BoxShape3D());
        var area = Add(PhysicsBodyKind.Area, new Vector3(0, 0.5f, 0), new BoxShape3D { Size = new Vector3(3) });
        Run(5);
        var others = new List<BackendBody>();
        _backend.CollectTouching(box, others);
        Assert.Contains(floor, others);

        others.Clear();
        Assert.True(_backend.CollectOverlaps(area, others));
        Assert.Equal(2, others.Count);
        Assert.Contains(floor, others);
        Assert.Contains(box, others);

        others.Clear();
        _backend.SetFilter(area, 1, 0);
        Assert.True(_backend.CollectOverlaps(area, others));
        Assert.Empty(others);
        Assert.False(_backend.CollectOverlaps(Create(PhysicsBodyKind.Area, Vector3.Zero), others));
    }

    [Fact]
    public void TheBroadPhaseUsesTheEnginesLayerRule()
    {
        Floor(); // layer 1, mask 1
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 1, 0), new BoxShape3D(), layer: 2, mask: 0); // neither scans the other
        Run(60);
        _backend.GetPose(box, out var position, out _);
        Assert.True(position.Y < -1f, $"fell through: {position}");

        _backend.SetFilter(box, 2, 1);
        _backend.Teleport(box, new Vector3(0, 1, 0), Quaternion.Identity);
        _backend.SetLinearVelocity(box, Vector3.Zero);
        Run(120);
        _backend.GetPose(box, out position, out _);
        Assert.InRange(position.Y, 0.45f, 0.55f);
    }

    [Fact]
    public void InvalidShapesAreReportedByDescriptionIndexAndSkipped()
    {
        var body = Create(PhysicsBodyKind.Static, Vector3.Zero);
        new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, new Vector3(1, 0, 1)] }.Describe(_shapes, Basis.Identity, Vector3.Zero);
        new BoxShape3D().Describe(_shapes, Basis.Identity, Vector3.Zero);
        SetShapes(body, PhysicsBodyKind.Static);

        Assert.Equal(0, Assert.Single(_errors).ShapeIndex);
        Assert.Equal(1, _backend.GetShapeCount(body));
        Assert.True(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 1, default, true, out var hit));
        Assert.Equal(1, hit.ShapeIndex);
    }

    [Fact]
    public void AKinematicTargetIsReachedInOneStep()
    {
        var body = Add(PhysicsBodyKind.Kinematic, Vector3.Zero, new BoxShape3D());
        var target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        _backend.MoveKinematic(body, new Vector3(1, 0, 0), target, Dt);
        _backend.Step(Dt);
        _backend.GetPose(body, out var position, out var rotation);
        Assert.Equal(1f, position.X, 4);
        Assert.True(MathF.Abs(Quaternion.Dot(rotation, target)) > 0.9999f, $"{rotation} vs {target}");
    }

    [Fact]
    public void CharacterPrimitivesMeasureTheGapToTheFloor()
    {
        var floor = Floor();
        var start = new Vector3(0, 0.905f, 0); // a 1.8 m capsule 5 mm above the floor
        var body = Add(PhysicsBodyKind.Character, start, new CapsuleShape3D { Radius = 0.4f, Height = 1.8f });

        Assert.True(_backend.CastBody(body, start, Quaternion.Identity, new Vector3(0, -0.1f, 0), out var hit));
        Assert.Equal(floor, hit.Body);
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitY) > 0.999f, $"{hit.Normal}");

        var nearby = new List<BackendShapeRef>();
        _backend.CollectNearbyShapes(body, start, Quaternion.Identity, 0.02f, nearby);
        var shape = Assert.Single(nearby);
        Assert.Equal(floor, shape.Body);
        Assert.Equal(1, _backend.GetShapeCount(body));
        Assert.True(_backend.Penetration(body, 0, start, Quaternion.Identity, shape, 0.02f, out var contact));
        Assert.InRange(contact.Distance, 0.004f, 0.006f);
        Assert.True(Vector3.Dot(Vector3.Normalize(contact.Normal), -Vector3.UnitY) > 0.999f); // from the body towards the floor

        Assert.True(_backend.Distance(body, start, Quaternion.Identity, floor, out var distance, out var normal));
        Assert.InRange(distance, 0.004f, 0.006f);
        Assert.True(Vector3.Dot(Vector3.Normalize(normal), -Vector3.UnitY) > 0.999f);
    }

    [Fact]
    public void QueryShapesAreCachedUntilTheResourceChangesAndDoNotAllocate()
    {
        Floor();
        var probe = new SphereShape3D { Radius = 0.5f };
        Assert.True(_backend.ShapeCast(probe.DescribeQuery(), new Vector3(0, 5, 0), Quaternion.Identity, new Vector3(0, -10, 0), 1, default, out var first));
        Assert.Equal(0.45f, first.Fraction, 2);
        probe.Radius = 1f;
        Assert.True(_backend.ShapeCast(probe.DescribeQuery(), new Vector3(0, 5, 0), Quaternion.Identity, new Vector3(0, -10, 0), 1, default, out var second));
        Assert.Equal(0.4f, second.Fraction, 2);

        var query = probe.DescribeQuery();
        var output = new List<BackendBody>(8);
        for (var i = 0; i < 3; i++) // warm up
        {
            _backend.ShapeCast(query, new Vector3(0, 5, 0), Quaternion.Identity, new Vector3(0, -10, 0), 1, default, out _);
            output.Clear();
            _backend.IntersectShape(query, new Vector3(0, 0.5f, 0), Quaternion.Identity, 1, default, output);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _backend.ShapeCast(query, new Vector3(0, 5, 0), Quaternion.Identity, new Vector3(0, -10, 0), 1, default, out _);
            output.Clear();
            _backend.IntersectShape(query, new Vector3(0, 0.5f, 0), Quaternion.Identity, 1, default, output);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Single(output);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var backend = new Jitter2Backend(new PhysicsSettings3D { MultiThreaded = false });
        backend.CreateBody(new BodyDescription3D(PhysicsBodyKind.Static, Vector3.Zero, Quaternion.Identity, 1, 1, default, 0.5f, 0f));
        backend.Dispose();
        backend.Dispose();
    }
}
```

- [ ] **Step 5: Run them to see them fail** → build error (`Jitter2Backend` missing).

- [ ] **Step 6: Implement `Jitter/Jitter2Backend.cs`.** Logic is moved from the files cited per member; only the record → `JitterBody` tag and the outputs change.

```csharp
using System.Numerics;
using System.Runtime.CompilerServices;
using Jitter2;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.DataStructures;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>A Jitter2 backend body, the tag of its <see cref="RigidBody"/>. Areas have no Jitter2 body: they hold shapes and a pose, queried after each step.</summary>
internal sealed class JitterBody(int index)
{
    public readonly int Index = index;
    public RigidBody? Body;
    public PhysicsBodyKind Kind;
    public uint Layer;
    public uint Mask;
    public float GravityScale = 1f;
    public bool GravityScaled;
    public Vector3 AreaPosition;
    public Quaternion AreaRotation = Quaternion.Identity;

    /// <summary>The built Jitter2 shapes (a concave description adds one triangle shape per triangle).</summary>
    public readonly List<RigidBodyShape> Shapes = [];

    /// <summary>For each entry of <see cref="Shapes"/>, the index of the description it was built from.</summary>
    public readonly List<int> ShapeDescriptions = [];

    public BackendBody Handle => new(Index);

    public bool IsArea => Kind == PhysicsBodyKind.Area;
}

/// <summary>
/// The Jitter2 2.9 backend (B2): one Jitter2 <see cref="World"/> per space. Jitter2-specific behaviour lives here: the
/// broad-phase filter over <see cref="PhysicsSpace3D.Collides"/>, the sphere contact filter (ADR 0134), the gravity-scale
/// force (Jitter2 has on/off gravity only), contact caches dropped after filter changes, areas as shape sets queried by
/// tree + narrow phase, and the EPA → distance → MPR chain of the character queries (ADR 0128) with Jitter2's log muted.
/// Shapes are built per body (a Jitter2 shape is one body's tree proxy, never shared); query shapes are cached per
/// resource and <see cref="Shape3D.ChangeCount"/> in a weak table (a game's throwaway query shapes are not kept alive).
/// </summary>
internal sealed class Jitter2Backend : PhysicsBackend3D
{
    private readonly World _world;
    private readonly PhysicsSettings3D _settings;
    private readonly List<JitterBody?> _bodies = [null]; // index 0 is BackendBody.None
    private readonly List<int> _free = [];
    private readonly List<JitterBody> _gravityScaled = [];
    private readonly Dictionary<RigidBodyShape, int> _shapeIndex = []; // an attached shape → its index in JitterBody.Shapes
    private readonly List<RigidBodyShape> _shapeScratch = [];
    private readonly List<IDynamicTreeProxy> _proxies = [];
    private readonly ConditionalWeakTable<Shape3D, QueryShape> _queryShapes = new();
    private readonly QueryFilters _filters = new();
    private bool _disposed;

    // PhysicsSpace3D.cs:197-206
    public Jitter2Backend(PhysicsSettings3D settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        JitterLog.EnsureInstalled();
        _settings = settings; // MultiThreaded is read at every step, as before
        _world = new World
        {
            Gravity = settings.Gravity.ToJ(),
            SubstepCount = settings.SubstepCount,
            SolverIterations = (settings.Jitter2.SolverIterations, settings.Jitter2.RelaxationIterations),
            AllowDeactivation = settings.AllowDeactivation,
            BroadPhaseFilter = new LayerFilter(),
            SolveMode = settings.Deterministic ? SolveMode.Deterministic : SolveMode.Regular,
        };
        _world.NarrowPhaseFilter = new SphereContactFilter(_world, _world.NarrowPhaseFilter);
    }

    public override PhysicsBackendKind Kind => PhysicsBackendKind.Jitter2;

    public override Vector3 Gravity
    {
        get => _world.Gravity.ToNumerics();
        set => _world.Gravity = value.ToJ();
    }

    public override int ActiveBodyCount => Math.Max(0, _world.RigidBodies.ActiveCount);

    private JitterBody Slot(BackendBody body) =>
        (uint)body.Index < (uint)_bodies.Count && _bodies[body.Index] is { } slot
            ? slot
            : throw new InvalidOperationException($"No Jitter2 backend body {body.Index}.");

    private JitterBody? SlotOrNull(BackendBody body) => (uint)body.Index < (uint)_bodies.Count ? _bodies[body.Index] : null;

    // ---- world (PhysicsSpace3D.cs:700-709 and the gravity force of 747-755) ---------------------------------------

    public override void Step(float dt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_gravityScaled.Count > 0)
        {
            // Jitter2 has on/off gravity only: a scale other than 0 or 1 opts out of it and gets mass · g · scale.
            var gravity = _world.Gravity.ToNumerics();
            foreach (var slot in _gravityScaled)
            {
                if (slot.Body is { IsActive: true } body)
                    body.AddForce((gravity * (slot.GravityScale * body.Mass)).ToJ(), wakeup: false);
            }
        }

        if (PhysicsSpace3D.MeasureLibraryAllocations)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _world.Step(dt, _settings.MultiThreaded);
            LibraryAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        else
        {
            _world.Step(dt, _settings.MultiThreaded);
        }
    }

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var slot in _bodies)
        {
            if (slot is null)
                continue;
            slot.Body = null;
            slot.Shapes.Clear();
            slot.ShapeDescriptions.Clear();
        }

        _bodies.Clear();
        _free.Clear();
        _gravityScaled.Clear();
        _shapeIndex.Clear();
        _world.Dispose();
    }

    // ---- bodies (PhysicsSpace3D.cs:266-288, 306-316, 456-483, 574-642, BodyRecord3D.ApplyMaterial 140-152) -----------

    public override BackendBody CreateBody(in BodyDescription3D desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int index;
        if (_free.Count > 0)
        {
            index = _free[^1];
            _free.RemoveAt(_free.Count - 1);
        }
        else
        {
            index = _bodies.Count;
            _bodies.Add(null);
        }

        var slot = new JitterBody(index) { Kind = desc.Kind, Layer = desc.Layer, Mask = desc.Mask };
        _bodies[index] = slot;
        if (desc.Kind == PhysicsBodyKind.Area)
        {
            slot.AreaPosition = desc.Position;
            slot.AreaRotation = desc.Rotation;
            return slot.Handle;
        }

        var body = _world.CreateRigidBody();
        body.Tag = slot;
        body.Position = desc.Position.ToJ();
        body.Orientation = desc.Rotation.ToJ();
        slot.Body = body;
        ApplyKind(slot);
        SetProperties(slot.Handle, desc.Properties);
        body.Friction = desc.Friction;
        body.Restitution = desc.Bounce;
        return slot.Handle;
    }

    public override void DestroyBody(BackendBody body)
    {
        var slot = Slot(body);
        if (slot.Body is { } rigid)
        {
            foreach (var shape in slot.Shapes)
                _shapeIndex.Remove(shape);
            if (!_disposed)
                _world.Remove(rigid);
            slot.Body = null;
        }

        if (slot.GravityScaled)
        {
            slot.GravityScaled = false;
            _gravityScaled.Remove(slot);
        }

        slot.Shapes.Clear();
        slot.ShapeDescriptions.Clear();
        _bodies[slot.Index] = null;
        _free.Add(slot.Index);
    }

    public override void SetKind(BackendBody body, PhysicsBodyKind kind)
    {
        var slot = Slot(body);
        slot.Kind = kind;
        if (slot.Body is not null)
            ApplyKind(slot);
    }

    private static void ApplyKind(JitterBody slot) =>
        slot.Body!.MotionType = slot.Kind switch
        {
            PhysicsBodyKind.Static => MotionType.Static,
            PhysicsBodyKind.Dynamic => MotionType.Dynamic,
            _ => MotionType.Kinematic,
        };

    public override void SetProperties(BackendBody body, in BodyProperties3D props)
    {
        var slot = Slot(body);
        if (slot.Body is { } rigid)
        {
            if (props.Simulated)
            {
                rigid.AffectedByGravity = props.GravityScale == 1f;
                rigid.Damping = (props.LinearDampingPerStep, props.AngularDampingPerStep);
                rigid.EnableSpeculativeContacts = props.Ccd;
                rigid.DeactivationTime = props.CanSleep ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(1e7);
                rigid.AllowedMotion = (MotionAxes)((int)MotionAxes.All & ~(int)props.AxisLocks);
            }
            else
            {
                rigid.AffectedByGravity = false;
                rigid.AllowedMotion = MotionAxes.All; // locks are a dynamic-body feature; kinematic moves must reach their target
            }
        }

        slot.GravityScale = props.GravityScale;
        var scaled = props.Simulated && props.GravityScale is not 1f and not 0f;
        if (scaled == slot.GravityScaled)
            return;
        slot.GravityScaled = scaled;
        if (scaled)
            _gravityScaled.Add(slot);
        else
            _gravityScaled.Remove(slot);
    }

    public override void SetMaterial(BackendBody body, float friction, float bounce)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        rigid.Friction = friction;
        rigid.Restitution = bounce;
    }

    public override void SetFilter(BackendBody body, uint layer, uint mask)
    {
        var slot = Slot(body);
        slot.Layer = layer;
        slot.Mask = mask;
    }

    public override void SetShapes(BackendBody body, ReadOnlySpan<ShapeDescription3D> shapes, float dynamicMass, List<ShapeError3D> errors)
    {
        var slot = Slot(body);
        var rigid = slot.Body;
        if (rigid is not null && slot.Shapes.Count > 0)
        {
            foreach (var shape in slot.Shapes)
                _shapeIndex.Remove(shape);
            rigid.ClearShapes(MassInertiaUpdateMode.Preserve);
        }

        slot.Shapes.Clear();
        slot.ShapeDescriptions.Clear();
        for (var i = 0; i < shapes.Length; i++)
        {
            _shapeScratch.Clear();
            try
            {
                Build(shapes[i], _shapeScratch);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // Degenerate data (coplanar hull points, zero scale, ...): the shape is skipped, the body still works.
                errors.Add(new ShapeError3D(i, e.Message));
                _shapeScratch.Clear();
                continue;
            }

            foreach (var created in _shapeScratch)
            {
                if (rigid is not null)
                {
                    _shapeIndex[created] = slot.Shapes.Count;
                    rigid.AddShape(created, MassInertiaUpdateMode.Preserve);
                }

                slot.Shapes.Add(created);
                slot.ShapeDescriptions.Add(i);
            }
        }

        _shapeScratch.Clear();
        if (rigid is not null && slot.Kind == PhysicsBodyKind.Dynamic && dynamicMass > 0f)
        {
            try
            {
                rigid.SetMassInertia(dynamicMass);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                errors.Add(new ShapeError3D(-1, e.Message));
                rigid.SetMassInertia(JSymmetricMatrix.Identity, dynamicMass);
            }
        }

        if (rigid is not null && slot.Kind != PhysicsBodyKind.Static)
            rigid.SetActivationState(true);
    }

    // Shape3D.CreateShapes/CreateBaseShape (Shapes3D.cs:24-34, 82, 106, 148, 187, 217-225) and AddTriangleShapes (280-296).
    private static void Build(in ShapeDescription3D d, List<RigidBodyShape> output)
    {
        if (d.Kind is ShapeKind3D.ConcaveMesh or ShapeKind3D.HeightMap)
        {
            AddTriangleShapes(output, d.Points!, d.Linear, d.Translation);
            return;
        }

        var shape = CreateConvex(d);
        if (JitterMath.IsIdentity(d.Linear) && d.Translation == Vector3.Zero)
            output.Add(shape);
        else
            output.Add(new TransformedShape(shape, d.Translation.ToJ(), d.Linear.ToJ()));
    }

    private static RigidBodyShape CreateConvex(in ShapeDescription3D d) => d.Kind switch
    {
        ShapeKind3D.Box => new BoxShape(d.Size.ToJ()),
        ShapeKind3D.Sphere => new SphereShape(d.Radius),
        ShapeKind3D.Capsule => new CapsuleShape(d.Radius, d.MidHeight),
        ShapeKind3D.Cylinder => new CylinderShape(d.Height, d.Radius), // Jitter2's argument order: (height, radius)
        ShapeKind3D.ConvexHull => CreatePointCloud(d.Points!),
        _ => throw new NotSupportedException($"{d.Resource.GetType().Name} has no single convex base shape."),
    };

    private static PointCloudShape CreatePointCloud(Vector3[] source)
    {
        Span<JVector> points = source.Length <= 256 ? stackalloc JVector[source.Length] : new JVector[source.Length];
        for (var i = 0; i < source.Length; i++)
            points[i] = source[i].ToJ();
        return new PointCloudShape(points);
    }

    private static void AddTriangleShapes(List<RigidBodyShape> output, ReadOnlySpan<Vector3> faces, in Basis linear, Vector3 translation)
    {
        if (faces.Length < 3)
            return;
        var soup = new JTriangle[faces.Length / 3];
        for (var i = 0; i < soup.Length; i++)
        {
            soup[i] = new JTriangle(
                (linear.Transform(faces[i * 3]) + translation).ToJ(),
                (linear.Transform(faces[i * 3 + 1]) + translation).ToJ(),
                (linear.Transform(faces[i * 3 + 2]) + translation).ToJ());
        }

        var mesh = new TriangleMesh(soup, ignoreDegenerated: true);
        for (var i = 0; i < mesh.Indices.Length; i++)
            output.Add(new TriangleShape(mesh, i));
    }

    // ---- state (BodyRecord3D 44-122, PhysicsSpace3D.cs:489-497, 559-563, 727-744, 791-806) ---------------------------

    public override void GetPose(BackendBody body, out Vector3 position, out Quaternion rotation)
    {
        var slot = Slot(body);
        if (slot.Body is { } rigid)
        {
            position = rigid.Position.ToNumerics();
            rotation = rigid.Orientation.ToNumerics();
        }
        else
        {
            position = slot.AreaPosition;
            rotation = slot.AreaRotation;
        }
    }

    public override void SetPose(BackendBody body, Vector3 position, Quaternion rotation)
    {
        var slot = Slot(body);
        if (slot.Body is { } rigid)
        {
            rigid.Position = position.ToJ();
            rigid.Orientation = rotation.ToJ();
        }
        else
        {
            slot.AreaPosition = position;
            slot.AreaRotation = rotation;
        }
    }

    public override void Teleport(BackendBody body, Vector3 position, Quaternion rotation)
    {
        SetPose(body, position, rotation);
        var slot = Slot(body);
        if (slot.Body is { } rigid && slot.Kind != PhysicsBodyKind.Static)
            rigid.SetActivationState(true);
    }

    public override void MoveKinematic(BackendBody body, Vector3 position, Quaternion rotation, float dt)
    {
        // Reach the target pose in exactly one step: contacts see a moving body, not a teleport.
        var rigid = Slot(body).Body!;
        var inverseDelta = 1f / dt;
        var current = rigid.Position.ToNumerics();
        var orientation = rigid.Orientation.ToNumerics();
        rigid.Velocity = ((position - current) * inverseDelta).ToJ();
        rigid.AngularVelocity = (PhysicsSpace3D.AngularVelocityBetween(orientation, rotation) * inverseDelta).ToJ();
    }

    public override Vector3 GetLinearVelocity(BackendBody body) => Slot(body).Body is { } rigid ? rigid.Velocity.ToNumerics() : Vector3.Zero;

    public override void SetLinearVelocity(BackendBody body, Vector3 velocity)
    {
        if (Slot(body).Body is { } rigid)
            rigid.Velocity = velocity.ToJ();
    }

    public override Vector3 GetAngularVelocity(BackendBody body) => Slot(body).Body is { } rigid ? rigid.AngularVelocity.ToNumerics() : Vector3.Zero;

    public override void SetAngularVelocity(BackendBody body, Vector3 velocity)
    {
        if (Slot(body).Body is { } rigid)
            rigid.AngularVelocity = velocity.ToJ();
    }

    public override void ApplyForce(BackendBody body, Vector3 force, Vector3? position)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        if (position is { } at)
            rigid.AddForce(force.ToJ(), at.ToJ());
        else
            rigid.AddForce(force.ToJ());
    }

    public override void ApplyImpulse(BackendBody body, Vector3 impulse, Vector3? position)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        if (position is { } at)
            rigid.ApplyImpulse(impulse.ToJ(), at.ToJ());
        else
            rigid.ApplyImpulse(impulse.ToJ());
    }

    public override void ApplyTorque(BackendBody body, Vector3 torque)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        rigid.Torque += torque.ToJ();
        rigid.SetActivationState(true);
    }

    public override bool IsSleeping(BackendBody body) => Slot(body).Body is { IsActive: false };

    public override void Wake(BackendBody body) => Slot(body).Body?.SetActivationState(true);

    public override void WakeTouching(BackendBody body)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        foreach (var arbiter in rigid.Contacts)
        {
            var other = ReferenceEquals(arbiter.Body1, rigid) ? arbiter.Body2 : arbiter.Body1;
            if (other.MotionType != MotionType.Static)
                other.SetActivationState(true);
        }
    }

    // Jitter2 keeps resting contacts until they break: drop them so a new filter applies now.
    public override void ClearContacts(BackendBody body) => Slot(body).Body?.ClearContactCache();

    // ---- scans (PhysicsSpace3D.cs:814-881) -----------------------------------------------------------------------

    public override void CollectTouching(BackendBody body, List<BackendBody> others)
    {
        if (Slot(body).Body is not { } rigid)
            return;
        foreach (var arbiter in rigid.Contacts)
        {
            var other = ReferenceEquals(arbiter.Body1, rigid) ? arbiter.Body2 : arbiter.Body1;
            if (other.Tag is not JitterBody otherSlot)
                continue;
            ref var data = ref arbiter.Handle.Data;
            if ((data.UsageMask & ContactData.MaskContactAll) == 0)
                continue; // all contact points broken this step
            others.Add(otherSlot.Handle);
        }
    }

    public override bool CollectOverlaps(BackendBody area, List<BackendBody> others)
    {
        var slot = Slot(area);
        if (slot.Shapes.Count == 0)
            return false;
        var position = slot.AreaPosition.ToJ();
        var rotation = slot.AreaRotation.ToJ();
        var box = Bounds(slot.Shapes, rotation, position);
        _proxies.Clear();
        var sink = new ProxySink(_proxies);
        _world.DynamicTree.Query(ref sink, box);
        foreach (var proxy in _proxies)
        {
            if (proxy is not RigidBodyShape { RigidBody: { Tag: JitterBody other } otherBody } otherShape ||
                !PhysicsSpace3D.Collides(slot.Layer, slot.Mask, true, other.Layer, other.Mask, other.IsArea) || others.Contains(other.Handle))
                continue;
            var otherPosition = otherBody.Position;
            var otherRotation = otherBody.Orientation;
            foreach (var areaShape in slot.Shapes)
            {
                if (NarrowPhase.Overlap(areaShape, otherShape, rotation, otherRotation, position, otherPosition))
                {
                    others.Add(other.Handle);
                    break;
                }
            }
        }

        _proxies.Clear();
        return true;
    }

    private static JBoundingBox Bounds(List<RigidBodyShape> shapes, in JQuaternion rotation, in JVector position)
    {
        var box = JBoundingBox.SmallBox;
        for (var i = 0; i < shapes.Count; i++)
        {
            shapes[i].CalculateBoundingBox(rotation, position, out var shapeBox);
            box = i == 0 ? shapeBox : JBoundingBox.CreateMerged(box, shapeBox);
        }

        return box;
    }

    // ---- queries (PhysicsDirectSpaceState3D.cs:47-166) -------------------------------------------------------------

    private int ShapeIndexOf(JitterBody slot, RigidBodyShape shape) => _shapeIndex.TryGetValue(shape, out var i) ? slot.ShapeDescriptions[i] : -1;

    private RigidBodyShape GetQueryShape(in ShapeDescription3D d)
    {
        if (_queryShapes.TryGetValue(d.Resource, out var cached) && cached.ChangeCount == d.ChangeCount)
            return cached.Shape;
        var shape = CreateConvex(d);
        _queryShapes.AddOrUpdate(d.Resource, new QueryShape(d.ChangeCount, shape));
        return shape;
    }

    public override bool RayCast(Vector3 from, Vector3 motion, uint mask, BackendBody exclude, bool hitFromInside, out BackendHit3D hit)
    {
        hit = default;
        _filters.Begin(mask, SlotOrNull(exclude));
        try
        {
            if (!_world.DynamicTree.RayCast(from.ToJ(), motion.ToJ(), 1f, _filters.RayPre, hitFromInside ? null : _filters.RayPostOutside, out var proxy,
                    out var normal, out var lambda) ||
                proxy is not RigidBodyShape { RigidBody.Tag: JitterBody slot } shape)
                return false;
            hit.Body = slot.Handle;
            hit.ShapeIndex = ShapeIndexOf(slot, shape);
            hit.Position = from + motion * lambda;
            hit.Normal = normal.ToNumerics();
            hit.Fraction = lambda;
            return true;
        }
        finally
        {
            _filters.End();
        }
    }

    public override bool ShapeCast(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, Vector3 motion, uint mask, BackendBody exclude,
        out BackendHit3D hit)
    {
        hit = default;
        var query = GetQueryShape(shape);
        _filters.Begin(mask, SlotOrNull(exclude));
        try
        {
            if (!_world.DynamicTree.SweepCast(query, rotation.ToJ(), position.ToJ(), motion.ToJ(), 1f, _filters.SweepPre, null,
                    out var proxy, out _, out var pointB, out var normal, out var lambda) ||
                proxy is not RigidBodyShape { RigidBody.Tag: JitterBody slot } hitShape)
                return false;
            hit.Body = slot.Handle;
            hit.ShapeIndex = ShapeIndexOf(slot, hitShape);
            hit.Position = pointB.ToNumerics();
            hit.Normal = normal.ToNumerics() == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(-normal.ToNumerics());
            hit.Fraction = lambda;
            return true;
        }
        finally
        {
            _filters.End();
        }
    }

    public override void IntersectShape(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, uint mask, BackendBody exclude,
        List<BackendBody> output)
    {
        var query = GetQueryShape(shape);
        var positionJ = position.ToJ();
        var rotationJ = rotation.ToJ();
        query.CalculateBoundingBox(rotationJ, positionJ, out var box);
        var excluded = SlotOrNull(exclude);
        _proxies.Clear();
        var sink = new ProxySink(_proxies);
        _world.DynamicTree.Query(ref sink, box);
        try
        {
            foreach (var proxy in _proxies)
            {
                if (proxy is not RigidBodyShape { RigidBody: { Tag: JitterBody slot } body } other || ReferenceEquals(slot, excluded) ||
                    (slot.Layer & mask) == 0 || output.Contains(slot.Handle))
                    continue;
                if (NarrowPhase.Overlap(query, other, rotationJ, body.Orientation, positionJ, body.Position))
                    output.Add(slot.Handle);
            }
        }
        finally
        {
            _proxies.Clear();
        }
    }

    public override void IntersectPoint(Vector3 point, uint mask, BackendBody exclude, List<BackendBody> output)
    {
        var p = point.ToJ();
        var box = new JBoundingBox(p - new JVector(1e-4f), p + new JVector(1e-4f));
        var excluded = SlotOrNull(exclude);
        _proxies.Clear();
        var sink = new ProxySink(_proxies);
        _world.DynamicTree.Query(ref sink, box);
        try
        {
            foreach (var proxy in _proxies)
            {
                if (proxy is not RigidBodyShape { RigidBody: { Tag: JitterBody slot } body } other || ReferenceEquals(slot, excluded) ||
                    (slot.Layer & mask) == 0 || output.Contains(slot.Handle))
                    continue;
                if (NarrowPhase.PointTest(other, JMatrix.CreateFromQuaternion(body.Orientation), body.Position, p))
                    output.Add(slot.Handle);
            }
        }
        finally
        {
            _proxies.Clear();
        }
    }

    // ---- character primitives (PhysicsSpace3D.cs:959-1133) ----------------------------------------------------------

    /// <summary>
    /// Jitter2's sweep reports contacts within a few millimetres as "already touching" (fraction 0, no normal); the normal
    /// then comes from a distance (or penetration) query against the hit shape.
    /// </summary>
    public override bool CastBody(BackendBody body, Vector3 position, Quaternion rotation, Vector3 motion, out BackendHit3D hit)
    {
        hit = default;
        var slot = Slot(body);
        if (slot.Body is null)
            return false;
        using var mute = new JitterLogMute();
        _filters.Begin(slot.Mask, slot);
        var best = float.MaxValue;
        var positionJ = position.ToJ();
        var rotationJ = rotation.ToJ();
        var motionJ = motion.ToJ();
        try
        {
            foreach (var shape in slot.Shapes)
            {
                if (!_world.DynamicTree.SweepCast(shape, rotationJ, positionJ, motionJ, 1f, _filters.SweepPre, null,
                        out var proxy, out _, out var pointB, out var normal, out var lambda) || lambda >= best)
                    continue;
                if (proxy is not RigidBodyShape { RigidBody: { Tag: JitterBody other } otherBody } otherShape)
                    continue;
                best = lambda;
                hit.Fraction = lambda;
                hit.Position = pointB.ToNumerics();
                hit.Body = other.Handle;
                hit.ShapeIndex = ShapeIndexOf(other, otherShape);
                // Jitter2's sweep normal points from the swept shape towards the hit one; surfaces face the other way.
                var surface = -normal.ToNumerics();
                if (surface.LengthSquared() < 1e-12f &&
                    !ContactNormal(shape, otherShape, otherBody, rotationJ, positionJ, out surface, out hit.Position))
                    surface = Vector3.Zero;
                hit.Normal = surface == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(surface);
            }
        }
        finally
        {
            _filters.End();
        }

        return best <= 1f;
    }

    // PhysicsSpace3D.cs:1001-1017, unchanged.
    private static bool ContactNormal(RigidBodyShape shape, RigidBodyShape other, RigidBody otherBody, in JQuaternion rotation,
        in JVector position, out Vector3 normal, out Vector3 point)
    {
        if (NarrowPhase.Distance(shape, other, rotation, otherBody.Orientation, position, otherBody.Position,
                out _, out var pointB, out var n, out _) ||
            NarrowPhase.MprEpa(shape, other, rotation, otherBody.Orientation, position, otherBody.Position,
                out _, out pointB, out n, out _))
        {
            normal = -n.ToNumerics(); // both report the direction from the shape towards the other one
            point = pointB.ToNumerics();
            return normal.LengthSquared() > 1e-12f;
        }

        normal = default;
        point = default;
        return false;
    }

    public override int GetShapeCount(BackendBody body) => Slot(body).Shapes.Count;

    public override void CollectNearbyShapes(BackendBody body, Vector3 position, Quaternion rotation, float expand, List<BackendShapeRef> output)
    {
        var slot = Slot(body);
        if (slot.Body is not { } self || slot.Shapes.Count == 0)
            return;
        var box = Bounds(slot.Shapes, rotation.ToJ(), position.ToJ());
        box.Min -= new JVector(expand);
        box.Max += new JVector(expand);
        _proxies.Clear();
        var sink = new ProxySink(_proxies);
        _world.DynamicTree.Query(ref sink, box);
        foreach (var proxy in _proxies)
        {
            if (proxy is not RigidBodyShape { RigidBody: { } otherBody } otherShape || ReferenceEquals(otherBody, self) ||
                otherBody.Tag is not JitterBody other || (slot.Mask & other.Layer) == 0 || !_shapeIndex.TryGetValue(otherShape, out var index))
                continue;
            output.Add(new BackendShapeRef(other.Handle, index));
        }

        _proxies.Clear();
    }

    /// <summary>
    /// EPA first (ADR 0128): one signed distance for touching, separated and overlapping shapes, accurate to well under a
    /// millimetre on large shapes where GJK and MPR answer near contact are off by millimetres; then GJK distance, then MPR.
    /// </summary>
    public override bool Penetration(BackendBody body, int shape, Vector3 position, Quaternion rotation, BackendShapeRef other, float maxDistance,
        out BackendContact3D contact)
    {
        using var mute = new JitterLogMute();
        var self = Slot(body).Shapes[shape];
        var target = Slot(other.Body);
        var otherBody = target.Body!;
        var otherShape = target.Shapes[other.Shape];
        var rotationJ = rotation.ToJ();
        if (NarrowPhase.Collision(self, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                out _, out var pointB, out var normal, out var epaDepth))
        {
            contact = new BackendContact3D(-epaDepth, normal.ToNumerics(), pointB.ToNumerics());
            return true;
        }

        if (NarrowPhase.Distance(self, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                out _, out pointB, out normal, out var distance))
        {
            contact = new BackendContact3D(distance, normal.ToNumerics(), pointB.ToNumerics());
            return true;
        }

        if (NarrowPhase.MprEpa(self, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                out _, out pointB, out normal, out var penetration) && penetration >= 0)
        {
            contact = new BackendContact3D(-penetration, normal.ToNumerics(), pointB.ToNumerics());
            return true;
        }

        contact = default;
        return false;
    }

    /// <summary>EPA's exact signed distance over every shape pair (ADR 0128: Jitter2's sweeps stop a few millimetres short on large shapes).</summary>
    public override bool Distance(BackendBody body, Vector3 position, Quaternion rotation, BackendBody other, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.Zero;
        var self = Slot(body);
        var target = Slot(other);
        if (target.Body is not { } otherBody)
            return false;
        using var mute = new JitterLogMute();
        var rotationJ = rotation.ToJ();
        var best = float.MaxValue;
        var bestNormal = JVector.Zero;
        foreach (var shape in self.Shapes)
            foreach (var otherShape in target.Shapes)
                if (NarrowPhase.Collision(shape, otherShape, rotationJ, otherBody.Orientation, position.ToJ(), otherBody.Position,
                        out _, out _, out var n, out var depth) && -depth < best)
                {
                    best = -depth; // separation
                    bestNormal = n;
                }

        if (best == float.MaxValue)
            return false;
        distance = best;
        normal = bestNormal.ToNumerics(); // from the body towards the other shape
        return true;
    }

    // ---- filters (PhysicsSpace3D.cs:1193-1247) ---------------------------------------------------------------------

    /// <summary>Collision layers on Jitter2's broad phase. Runs on worker threads: it only reads tags, changed on the main thread between steps.</summary>
    private sealed class LayerFilter : IBroadPhaseFilter
    {
        public bool Filter(IDynamicTreeProxy proxyA, IDynamicTreeProxy proxyB)
        {
            if (proxyA is not RigidBodyShape { RigidBody.Tag: JitterBody a } || proxyB is not RigidBodyShape { RigidBody.Tag: JitterBody b })
                return true;
            return PhysicsSpace3D.Collides(a.Layer, a.Mask, a.IsArea, b.Layer, b.Mask, b.IsArea);
        }
    }

    /// <summary>Cached query filter delegates over mutable state (no per-query allocation).</summary>
    private sealed class QueryFilters
    {
        private uint _mask;
        private JitterBody? _exclude;

        public QueryFilters()
        {
            RayPre = Accept;
            SweepPre = Accept;
            RayPostOutside = static result => result.Normal != JVector.Zero;
        }

        public DynamicTree.RayCastFilterPre RayPre { get; }

        /// <summary>Rejects shapes that contain the ray's origin (Jitter2 reports them with a zero normal).</summary>
        public DynamicTree.RayCastFilterPost RayPostOutside { get; }

        public DynamicTree.SweepCastFilterPre SweepPre { get; }

        public void Begin(uint mask, JitterBody? exclude)
        {
            _mask = mask;
            _exclude = exclude;
        }

        public void End() => _exclude = null;

        private bool Accept(IDynamicTreeProxy proxy) =>
            proxy is RigidBodyShape { RigidBody.Tag: JitterBody slot } && !ReferenceEquals(slot, _exclude) && (slot.Layer & _mask) != 0;
    }

    private sealed class QueryShape(int changeCount, RigidBodyShape shape)
    {
        public readonly int ChangeCount = changeCount;
        public readonly RigidBodyShape Shape = shape;
    }

    /// <summary>Collects tree query results into a reused list.</summary>
    private readonly struct ProxySink(List<IDynamicTreeProxy> list) : ISink<IDynamicTreeProxy>
    {
        public void Add(in IDynamicTreeProxy item) => list.Add(item);
    }
}
```

Then in `Backend/PhysicsBackend3D.cs` set `PhysicsBackendKind.Jitter2 => new Jitter2Backend(settings),`.

- [ ] **Step 7: Run the backend tests** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~Jitter2BackendTests"` → 10 pass. Then the physics suite and the trace gate → pass (the space does not use the backend yet).

- [ ] **Step 8: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Jitter/ MainframeEngine/Src/Physics/3D/Backend/PhysicsBackend3D.cs MainframeEngine/Src/Physics/3D/PhysicsSpace3D.cs MainframeEngine/Src/Physics/3D/PhysicsServer3D.cs Tests/MainframeEngine.Tests/Physics/Jitter2BackendTests.cs
git commit -F- <<'EOF'
Add Jitter2Backend behind PhysicsBackend3D and gather Jitter2 helpers in Physics/3D/Jitter

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 9: Switch `PhysicsSpace3D` and the queries to the backend

**Files:**
- Modify: `MainframeEngine/Src/Physics/3D/PhysicsSpace3D.cs`, `PhysicsDirectSpaceState3D.cs`, `PhysicsServer3D.cs:73`

**Interfaces:**
- Consumes: everything from Tasks 6–8.
- Produces: `internal PhysicsSpace3D(SceneViewport viewport, PhysicsSettings3D settings, PhysicsBackendKind backend)`; `public PhysicsBackendKind PhysicsSpace3D.BackendKind`; `internal PhysicsBackend3D Backend`; `internal BodyRecord3D? RecordOf(BackendBody)`; `internal BackendBody HandleOf(CollisionObject3D?)`; `internal List<BackendBody> QueryScratch`; `internal int AddHits(List<BackendBody>, List<CollisionObject3D>)`; `internal static void MaterialOf(CollisionObject3D, out float, out float)`; `BodyRecord3D.Handle`, `.ShapeOwners`, `.ShapeOwner(int)`. Unchanged signatures used by `CharacterBody3D`: `CastBody(BodyRecord3D, Vector3, Quaternion, Vector3, out BodyCastHit3D)`, `RecoverBody(BodyRecord3D, ref Vector3, Quaternion, float, Span<CharacterContact3D>, out bool)`, `SettleBody(BodyRecord3D, ref Vector3, Quaternion, float, CollisionObject3D, float)`.

This is the one step that cannot stay green half-way (the body tag changes from the record to `JitterBody` for every
caller at once). Do Steps 1–11 without running tests, then Step 12 runs the whole gate; the backend was tested alone in
Task 8 and the trace pins the result.

- [ ] **Step 1: Replace `BodyRecord3D`** (`PhysicsSpace3D.cs:12-163`):

```csharp
/// <summary>The physics state of one collision object in a <see cref="PhysicsSpace3D"/>.</summary>
internal sealed class BodyRecord3D(CollisionObject3D node, PhysicsSpace3D space, long sequence)
{
    public readonly CollisionObject3D Node = node;
    public readonly PhysicsSpace3D Space = space;
    public readonly long Sequence = sequence;
    public PhysicsBodyKind Kind;
    public BackendBody Handle;
    public uint Layer;
    public uint Mask;
    public bool Removed;
    public bool TransformDirty;
    public bool ShapesDirty;
    public bool InDirtyList;
    public bool Interpolated;
    public bool RenderAtCurrent = true;
    public bool HasTarget;
    public bool WasSleeping;
    public bool LoggedConcave;

    /// <summary>The <see cref="CollisionShape3D"/> of each description in the last <see cref="PhysicsBackend3D.SetShapes"/> (<see cref="BackendHit3D.ShapeIndex"/>).</summary>
    public readonly List<CollisionShape3D> ShapeOwners = [];

    public Vector3 Scale = Vector3.One;
    public Vector3 PreviousPosition, CurrentPosition, TargetPosition;
    public Quaternion PreviousRotation = Quaternion.Identity, CurrentRotation = Quaternion.Identity, TargetRotation = Quaternion.Identity;
    public Vector3 KinematicVelocity, KinematicAngularVelocity;

    /// <summary>Touching bodies (contact monitor) or overlapping bodies (area), with the step they were last seen.</summary>
    public Dictionary<BodyRecord3D, long>? Overlaps;

    public bool IsMoving => Kind is PhysicsBodyKind.Dynamic or PhysicsBodyKind.Kinematic or PhysicsBodyKind.Character;

    /// <summary>A live backend body that collides (areas only detect).</summary>
    public bool HasBody => !Removed && Kind != PhysicsBodyKind.Area;

    public CollisionShape3D? ShapeOwner(int index) => (uint)index < (uint)ShapeOwners.Count ? ShapeOwners[index] : null;

    // ---- state the nodes read and write --------------------------------------------------------------------------

    public Vector3 GetLinearVelocity() => Kind switch
    {
        PhysicsBodyKind.Dynamic => Space.Backend.GetLinearVelocity(Handle),
        PhysicsBodyKind.Kinematic => KinematicVelocity,
        _ => Vector3.Zero,
    };

    public Vector3 GetAngularVelocity() => Kind switch
    {
        PhysicsBodyKind.Dynamic => Space.Backend.GetAngularVelocity(Handle),
        PhysicsBodyKind.Kinematic => KinematicAngularVelocity,
        _ => Vector3.Zero,
    };

    public void SetLinearVelocity(Vector3 velocity)
    {
        if (Kind == PhysicsBodyKind.Dynamic)
            Space.Backend.SetLinearVelocity(Handle, velocity);
        else if (Kind == PhysicsBodyKind.Kinematic)
            KinematicVelocity = velocity;
    }

    public void SetAngularVelocity(Vector3 velocity)
    {
        if (Kind == PhysicsBodyKind.Dynamic)
            Space.Backend.SetAngularVelocity(Handle, velocity);
        else if (Kind == PhysicsBodyKind.Kinematic)
            KinematicAngularVelocity = velocity;
    }

    public bool IsSleeping => Kind == PhysicsBodyKind.Dynamic && !Removed && Space.Backend.IsSleeping(Handle);

    public void ApplyForce(Vector3 force, Vector3? position)
    {
        if (Kind == PhysicsBodyKind.Dynamic && !Removed)
            Space.Backend.ApplyForce(Handle, force, position);
    }

    public void ApplyImpulse(Vector3 impulse, Vector3? position)
    {
        if (Kind == PhysicsBodyKind.Dynamic && !Removed)
            Space.Backend.ApplyImpulse(Handle, impulse, position);
    }

    public void ApplyTorque(Vector3 torque)
    {
        if (Kind == PhysicsBodyKind.Dynamic && !Removed)
            Space.Backend.ApplyTorque(Handle, torque);
    }

    /// <summary>Moves the body (and node) to a pose now and restarts interpolation from it.</summary>
    public void Teleport(Vector3 position, Quaternion rotation)
    {
        rotation = Quaternion.Normalize(rotation);
        Node.ApplyPhysicsPose(position, rotation, Scale);
        if (HasBody)
            Space.Backend.Teleport(Handle, position, rotation); // wakes it unless static

        TransformDirty = false;
        HasTarget = false;
        PreviousPosition = CurrentPosition = position;
        PreviousRotation = CurrentRotation = rotation;
        RenderAtCurrent = true;
    }

    /// <summary>Layer or mask changed on the node.</summary>
    public void OnFilterChanged()
    {
        Layer = Node.CollisionLayer;
        Mask = Node.CollisionMask;
        if (Removed)
            return;
        var backend = Space.Backend;
        backend.SetFilter(Handle, Layer, Mask);
        if (Kind == PhysicsBodyKind.Area)
            return;
        // Resting contacts are dropped so the new filter applies now; what touched the body wakes.
        backend.WakeTouching(Handle);
        backend.ClearContacts(Handle);
        if (Kind != PhysicsBodyKind.Static)
            backend.Wake(Handle);
    }

    /// <summary>The node's physics material (or its values) changed.</summary>
    public void ApplyMaterial()
    {
        if (!HasBody)
            return;
        PhysicsSpace3D.MaterialOf(Node, out var friction, out var bounce);
        Space.Backend.SetMaterial(Handle, friction, bounce);
    }

    /// <summary>Adds the overlapping/touching nodes to <paramref name="results"/>.</summary>
    public int GetOverlaps(List<Node3D> results)
    {
        if (Overlaps is not { } overlaps)
            return 0;
        foreach (var other in overlaps.Keys)
            results.Add(other.Node);
        return overlaps.Count;
    }
}
```

- [ ] **Step 2: Usings, header, fields, constructor and properties.** Lines 1-8 become:

```csharp
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
```

Lines 165-243 become (doc comment, fields, constructor, properties):

```csharp
/// <summary>
/// The physics side of a <see cref="World3D"/>, owned by the <see cref="PhysicsServer3D"/>: the engine's bookkeeping over
/// one <see cref="PhysicsBackend3D"/> (Jitter2; Jolt in B3). Holds the collision objects in that world, steps them, writes
/// poses back to the nodes, interpolates them for rendering and queues contact/area/sleep signals, dispatched on the main
/// thread after each step.
/// </summary>
/// <remarks>Not thread-safe: nodes and queries use it from the main thread; the backend may use worker threads inside a step.</remarks>
public sealed class PhysicsSpace3D : IDisposable
{
    private readonly PhysicsBackend3D _backend;
    private readonly List<BodyRecord3D> _records = [];
    private readonly List<BodyRecord3D> _dirty = [];
    private readonly List<BodyRecord3D> _moving = [];
    private readonly List<BodyRecord3D> _areas = [];
    private readonly List<BodyRecord3D> _monitors = [];
    private List<PhysicsEvent<BodyRecord3D>> _events = [];
    private List<PhysicsEvent<BodyRecord3D>> _dispatching = [];
    private BodyRecord3D?[] _byHandle = new BodyRecord3D?[64];
    private readonly List<BackendBody> _others = [];
    private readonly List<BackendBody> _queryHits = [];
    private readonly List<BackendShapeRef> _nearby = [];
    private readonly List<ShapeDescription3D> _shapeDescriptions = [];
    private readonly List<ShapeError3D> _shapeErrors = [];
    private int _dispatchIndex = -1;
    private long _sequence;
    private long _step;
    private float _delta = 1f / 60f;
    private bool _disposed;

    internal PhysicsSpace3D(SceneViewport viewport, PhysicsSettings3D settings, PhysicsBackendKind backend)
    {
        Viewport = viewport;
        _backend = PhysicsBackend3D.Create(backend, settings);
        World3D.PhysicsSpace = this;
    }

    /// <summary>The viewport whose world this space simulates (debug lines go to its batch).</summary>
    public SceneViewport Viewport { get; }

    public World3D World3D => Viewport.World3D;

    /// <summary>The library this space runs on.</summary>
    public PhysicsBackendKind BackendKind => _backend.Kind;

    /// <summary>Collision objects in the space.</summary>
    public int ObjectCount => _records.Count;

    /// <summary>Bodies currently awake (simulated).</summary>
    public int ActiveBodyCount => _backend.ActiveBodyCount;

    /// <summary>Physics steps run so far.</summary>
    public long StepCount => _step;

    /// <summary>Gravity in m/s².</summary>
    public Vector3 Gravity
    {
        get => _backend.Gravity;
        set => _backend.Gravity = value;
    }

    /// <summary>Queries against this space (also <see cref="World3D.DirectSpaceState"/>).</summary>
    public PhysicsDirectSpaceState3D DirectSpaceState => World3D.DirectSpaceState;

    internal PhysicsBackend3D Backend => _backend;

    /// <summary>
    /// Test hook: when set, bytes allocated on this thread inside the library's step (Jitter2's worker pool occasionally
    /// allocates a few bytes when stepping multi-threaded) are added to <see cref="LibraryAllocatedBytes"/>.
    /// </summary>
    internal static bool MeasureLibraryAllocations { get; set; }

    internal long LibraryAllocatedBytes => _backend.LibraryAllocatedBytes;

    /// <summary>Scratch for <see cref="PhysicsDirectSpaceState3D"/>'s overlap queries.</summary>
    internal List<BackendBody> QueryScratch => _queryHits;

    internal BodyRecord3D? RecordOf(BackendBody body) => (uint)body.Index < (uint)_byHandle.Length ? _byHandle[body.Index] : null;

    /// <summary>The backend handle of a node in this space, or none (another space's node excludes nothing here).</summary>
    internal BackendBody HandleOf(CollisionObject3D? node) =>
        node?.Record is { Removed: false } record && ReferenceEquals(record.Space, this) ? record.Handle : BackendBody.None;

    private void MapHandle(BodyRecord3D record)
    {
        var index = record.Handle.Index;
        if (index >= _byHandle.Length)
            Array.Resize(ref _byHandle, Math.Max(index + 1, _byHandle.Length * 2));
        _byHandle[index] = record;
    }

    /// <summary>Adds the live nodes of <paramref name="hits"/> to <paramref name="results"/> (each once) and clears <paramref name="hits"/>.</summary>
    internal int AddHits(List<BackendBody> hits, List<CollisionObject3D> results)
    {
        var added = 0;
        foreach (var hit in hits)
        {
            if (RecordOf(hit) is not { Removed: false } record || results.Contains(record.Node))
                continue;
            results.Add(record.Node);
            added++;
        }

        hits.Clear();
        return added;
    }

    internal static void MaterialOf(CollisionObject3D node, out float friction, out float bounce)
    {
        var material = node switch
        {
            RigidBody3D rigid => rigid.PhysicsMaterial,
            StaticBody3D stat => stat.PhysicsMaterial,
            _ => null,
        };
        friction = material?.Friction ?? PhysicsMaterial.DefaultFriction;
        bounce = material?.Bounce ?? 0f;
    }
```

- [ ] **Step 3: `AddObject` and `RemoveObject`** (lines 249-319):

```csharp
    internal void AddObject(CollisionObject3D node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var record = new BodyRecord3D(node, this, ++_sequence)
        {
            Kind = node.Kind,
            Layer = node.CollisionLayer,
            Mask = node.CollisionMask,
        };
        node.Record = record;
        _records.Add(record);

        node.GlobalTransform.Decompose(out var position, out var rotation, out var scale);
        record.Scale = scale;
        record.PreviousPosition = record.CurrentPosition = position;
        record.PreviousRotation = record.CurrentRotation = rotation;

        MaterialOf(node, out var friction, out var bounce);
        record.Handle = _backend.CreateBody(new BodyDescription3D(record.Kind, position, rotation, record.Layer, record.Mask, PropertiesOf(record),
            friction, bounce));
        MapHandle(record);
        if (record.Kind != PhysicsBodyKind.Area)
        {
            if (node is RigidBody3D rigid && record.Kind == PhysicsBodyKind.Dynamic)
            {
                if (rigid.InitialLinearVelocity != Vector3.Zero)
                    _backend.SetLinearVelocity(record.Handle, rigid.InitialLinearVelocity);
                if (rigid.InitialAngularVelocity != Vector3.Zero)
                    _backend.SetAngularVelocity(record.Handle, rigid.InitialAngularVelocity);
            }
            else if (node is RigidBody3D kinematic)
            {
                record.KinematicVelocity = kinematic.InitialLinearVelocity;
                record.KinematicAngularVelocity = kinematic.InitialAngularVelocity;
            }
        }

        UpdateMembership(record);
        MarkShapesDirty(record);
    }

    internal void RemoveObject(BodyRecord3D record)
    {
        if (record.Removed)
            return;
        record.Removed = true;
        record.Node.Record = null;
        _records.Remove(record);
        _moving.Remove(record);
        _areas.Remove(record);
        _monitors.Remove(record);

        if (!_disposed)
        {
            if (record.Kind != PhysicsBodyKind.Area)
                _backend.WakeTouching(record.Handle);
            _backend.DestroyBody(record.Handle);
        }

        _byHandle[record.Handle.Index] = null; // before any handler can create a body that reuses the index
        record.Handle = BackendBody.None;
        record.ShapeOwners.Clear();
        record.Overlaps = null;
        EmitExitsForRemoved(record);
    }
```

(`CreateBody` applies kind, properties and material in that order, as `AddObject` did through `ApplyKind`,
`ApplyProperties` and `ApplyMaterial`.) Lines 321-384 stay verbatim.

- [ ] **Step 4: Kind, properties, membership** — replace lines 387-406 and 434-497 with (lines 409-432 and 447-454 stay):

```csharp
    internal void OnKindChanged(BodyRecord3D record)
    {
        record.Kind = record.Node.Kind;
        if (record.Kind != PhysicsBodyKind.Area)
        {
            _backend.SetKind(record.Handle, record.Kind);
            ApplyProperties(record);
            record.ShapesDirty = true; // mass (dynamic) or concave-shape eligibility may have changed
            AddDirty(record);
        }

        UpdateMembership(record);
    }

    internal void OnBodyPropertiesChanged(BodyRecord3D record)
    {
        if (record.Kind != PhysicsBodyKind.Area)
            ApplyProperties(record);
        UpdateMembership(record);
    }
```

```csharp
    private void UpdateMembership(BodyRecord3D record)
    {
        record.Interpolated = record.IsMoving && record.Node.PhysicsInterpolation;
        SetMember(_moving, record, record.IsMoving);
        SetMember(_areas, record, record.Kind == PhysicsBodyKind.Area);
        var monitor = record.Node is RigidBody3D { ContactMonitor: true };
        SetMember(_monitors, record, monitor);
        if ((monitor || record.Kind == PhysicsBodyKind.Area) && record.Overlaps is null)
            record.Overlaps = new(8);
    }
```

```csharp
    private void ApplyProperties(BodyRecord3D record) => _backend.SetProperties(record.Handle, PropertiesOf(record));

    // Dynamic rigid bodies are simulated (gravity scale, damping per step, CCD, sleep, axis locks); everything else is not.
    private BodyProperties3D PropertiesOf(BodyRecord3D record) =>
        record.Node is RigidBody3D rigid && record.Kind == PhysicsBodyKind.Dynamic
            ? new BodyProperties3D(true, StepDamping(rigid.LinearDamp), StepDamping(rigid.AngularDamp), rigid.GravityScale, rigid.AxisLocks, rigid.Ccd,
                rigid.CanSleep)
            : default;

    // Godot-style per-second damping → the velocity removed per step (1 − e^(−d·dt)).
    private float StepDamping(float perSecond) => Math.Clamp(1f - MathF.Exp(-perSecond * _delta), 0f, 1f);
```

(`WakeTouching`, lines 489-497, is deleted: it is `Jitter2Backend.WakeTouching`.)

- [ ] **Step 5: `PushTransform` and `RebuildShapes`.** In `PushTransform` (lines 557-568) the default branch becomes:

```csharp
            default:
                // Static, area: move there. Dynamic: teleport (velocity kept) and restart interpolation.
                _backend.SetPose(record.Handle, position, rotation);
                record.PreviousPosition = record.CurrentPosition = position;
                record.PreviousRotation = record.CurrentRotation = rotation;
                record.RenderAtCurrent = true;
                break;
```

`RebuildShapes` (lines 574-642) becomes:

```csharp
    private void RebuildShapes(BodyRecord3D record)
    {
        record.ShapesDirty = false;
        var node = record.Node;
        var descriptions = _shapeDescriptions;
        descriptions.Clear();
        record.ShapeOwners.Clear();
        for (var i = 0; i < node.ChildCount; i++)
        {
            if (node.GetChild(i) is not CollisionShape3D { Disabled: false, Shape: { } shape } owner)
                continue;
            if (shape.IsConcave && record.Kind == PhysicsBodyKind.Dynamic)
            {
                if (!record.LoggedConcave)
                    Log.Error($"[Physics] '{node.Name}': {shape.GetType().Name} is concave and only works on static or kinematic bodies; ignored.");
                record.LoggedConcave = true;
                continue;
            }

            BakeShapeTransform(record.Scale, owner.Transform, out var linear, out var translation);
            var start = descriptions.Count;
            try
            {
                shape.Describe(descriptions, linear, translation);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Log.Error($"[Physics] '{node.Name}/{owner.Name}': {shape.GetType().Name} is invalid ({e.Message}); ignored.");
                descriptions.RemoveRange(start, descriptions.Count - start);
                continue;
            }

            for (var d = start; d < descriptions.Count; d++)
                record.ShapeOwners.Add(owner);
        }

        var mass = record.Kind == PhysicsBodyKind.Dynamic && node is RigidBody3D rigid ? rigid.Mass : 0f;
        _shapeErrors.Clear();
        _backend.SetShapes(record.Handle, CollectionsMarshal.AsSpan(descriptions), mass, _shapeErrors);
        foreach (var error in _shapeErrors)
        {
            if (error.ShapeIndex < 0)
                Log.Error($"[Physics] '{node.Name}': cannot compute mass properties ({error.Message}); using a unit inertia.");
            else
                Log.Error($"[Physics] '{node.Name}/{record.ShapeOwners[error.ShapeIndex].Name}': {descriptions[error.ShapeIndex].Resource.GetType().Name} is invalid ({error.Message}); ignored.");
        }

        _shapeErrors.Clear();
        descriptions.Clear(); // don't keep the resources' arrays alive
    }
```

- [ ] **Step 6: `Step`, `PreStep`, `PostStep`** (lines 687-756 and 771-812; `AngularVelocityBetween` 759-769 stays, `internal static` since Task 6):

```csharp
    /// <summary>One fixed step: push node changes, step the backend, pull poses, compute contacts/overlaps, dispatch signals.</summary>
    internal void Step(float delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (delta != _delta)
        {
            _delta = delta;
            foreach (var record in _records)
                if (record.Kind != PhysicsBodyKind.Area)
                    ApplyProperties(record);
        }

        Flush(stepping: true);
        PreStep(delta);
        _backend.Step(delta); // the gravity-scale forces and the allocation measurement are the backend's
        _step++;
        PostStep();
        ScanContacts();
        ScanAreas();
        Dispatch();
    }

    private void PreStep(float delta)
    {
        foreach (var record in _moving)
        {
            if (record.Kind == PhysicsBodyKind.Dynamic)
                continue;
            if (record.HasTarget)
            {
                _backend.MoveKinematic(record.Handle, record.TargetPosition, record.TargetRotation, delta);
            }
            else if (record.Kind == PhysicsBodyKind.Kinematic)
            {
                _backend.SetLinearVelocity(record.Handle, record.KinematicVelocity);
                _backend.SetAngularVelocity(record.Handle, record.KinematicAngularVelocity);
            }
            else
            {
                _backend.SetLinearVelocity(record.Handle, Vector3.Zero);
                _backend.SetAngularVelocity(record.Handle, Vector3.Zero);
            }
        }
    }

    private void PostStep()
    {
        foreach (var record in _moving)
        {
            record.PreviousPosition = record.CurrentPosition;
            record.PreviousRotation = record.CurrentRotation;
            if (record.Kind == PhysicsBodyKind.Character)
            {
                // The node holds the authoritative pose (MoveAndSlide); the backend body follows it.
                if (record.HasTarget)
                {
                    record.CurrentPosition = record.TargetPosition;
                    record.CurrentRotation = record.TargetRotation;
                }

                record.HasTarget = false;
                continue;
            }

            record.HasTarget = false;
            _backend.GetPose(record.Handle, out var position, out var rotation);
            record.CurrentPosition = position;
            record.CurrentRotation = rotation;
            if (record.PreviousPosition != position || record.PreviousRotation != rotation || !record.RenderAtCurrent)
            {
                record.Node.ApplyPhysicsPose(position, rotation, record.Scale);
                record.RenderAtCurrent = true;
            }

            if (record.Kind == PhysicsBodyKind.Dynamic)
            {
                var sleeping = _backend.IsSleeping(record.Handle);
                if (sleeping != record.WasSleeping)
                {
                    record.WasSleeping = sleeping;
                    _events.Add(new PhysicsEvent<BodyRecord3D>(PhysicsEventKind.SleepChanged, record, null, record.Sequence, 0));
                }
            }
        }
    }
```

- [ ] **Step 7: Scans** (lines 814-881; `Touch`, `Untouch`, `Dispatch` 883-944 stay; `FindShapeOwner` 947-948 is deleted):

```csharp
    private void ScanContacts()
    {
        foreach (var monitor in _monitors)
        {
            if (monitor.Overlaps is not { } touching)
                continue;
            _others.Clear();
            _backend.CollectTouching(monitor.Handle, _others);
            foreach (var handle in _others)
                if (RecordOf(handle) is { Removed: false } other)
                    Touch(monitor, touching, other, PhysicsEventKind.ContactEntered);
            _others.Clear();
            Untouch(monitor, touching, PhysicsEventKind.ContactExited);
        }
    }

    private void ScanAreas()
    {
        foreach (var area in _areas)
        {
            if (area.Overlaps is not { } overlaps)
                continue;
            _others.Clear();
            if (area.Node is Area3D { Monitoring: false } || !_backend.CollectOverlaps(area.Handle, _others))
            {
                _others.Clear();
                Untouch(area, overlaps, PhysicsEventKind.AreaExited, everything: true);
                continue;
            }

            foreach (var handle in _others)
                if (RecordOf(handle) is { Removed: false } other)
                    Touch(area, overlaps, other, PhysicsEventKind.AreaEntered);
            _others.Clear();
            Untouch(area, overlaps, PhysicsEventKind.AreaExited);
        }
    }
```

- [ ] **Step 8: Character queries** (lines 954-1133; `ContactNormal` 1001-1017 is deleted, it is the backend's):

```csharp
    /// <summary>Sweeps every shape of <paramref name="record"/> from the given pose along <paramref name="motion"/>; the closest hit wins.</summary>
    internal bool CastBody(BodyRecord3D record, Vector3 position, Quaternion rotation, Vector3 motion, out BodyCastHit3D hit)
    {
        hit = default;
        if (!record.HasBody || motion.LengthSquared() < 1e-14f)
            return false;
        FlushForQuery();
        if (!_backend.CastBody(record.Handle, position, rotation, motion, out var result))
            return false;
        hit.Fraction = result.Fraction;
        hit.Point = result.Position;
        hit.Normal = result.Normal;
        hit.Collider = RecordOf(result.Body)!.Node;
        return true;
    }

    /// <summary>
    /// Recovery: pushes <paramref name="position"/> out of everything the body's shapes penetrate or come closer to than
    /// <paramref name="margin"/>, leaving <paramref name="margin"/> clearance; each push moves the pose the next pair is
    /// measured from. Everything within two margins counts as a contact (that is how a resting character stays "on the
    /// floor"). Returns how many contacts were found (written to <paramref name="contacts"/>, up to its length).
    /// </summary>
    internal int RecoverBody(BodyRecord3D record, ref Vector3 position, Quaternion rotation, float margin, Span<CharacterContact3D> contacts,
        out bool pushed)
    {
        pushed = false;
        if (!record.HasBody)
            return 0;
        FlushForQuery();
        var shapeCount = _backend.GetShapeCount(record.Handle);
        if (shapeCount == 0)
            return 0;
        _nearby.Clear();
        _backend.CollectNearbyShapes(record.Handle, position, rotation, margin * 2, _nearby);
        var found = 0;
        foreach (var candidate in _nearby)
        {
            if (RecordOf(candidate.Body) is not { Removed: false } other)
                continue;
            for (var shape = 0; shape < shapeCount; shape++)
            {
                // Within two margins counts as touching (a resting or slope-walking body hovers at about one margin); only
                // closer than one margin is pushed back out, along the negative of the normal towards the other shape.
                if (!_backend.Penetration(record.Handle, shape, position, rotation, candidate, margin * 2, out var contact) ||
                    contact.Distance >= margin * 2)
                    continue;
                var push = MathF.Max(0f, margin - contact.Distance);
                var direction = -contact.Normal;
                if (direction.LengthSquared() < 1e-12f)
                    continue;
                direction = Vector3.Normalize(direction);
                position += direction * push;
                pushed |= push > 0;
                if (found < contacts.Length)
                    contacts[found++] = new CharacterContact3D { Normal = direction, Point = contact.Point, Collider = other.Node };
            }
        }

        _nearby.Clear();
        return found;
    }

    /// <summary>
    /// Moves a character body that a cast left a little short of <paramref name="collider"/> onto the safe margin
    /// (ADR 0128), along the backend's exact signed distance; gaps larger than <paramref name="maxGap"/> beyond the margin
    /// are left alone. Returns true if the body moved.
    /// </summary>
    internal bool SettleBody(BodyRecord3D record, ref Vector3 position, Quaternion rotation, float margin, CollisionObject3D collider, float maxGap)
    {
        if (!record.HasBody || collider.Record is not { HasBody: true } other || !ReferenceEquals(other.Space, this))
            return false;
        FlushForQuery();
        if (!_backend.Distance(record.Handle, position, rotation, other.Handle, out var best, out var normal))
            return false;
        if (best <= margin || best > margin + maxGap)
            return false;
        if (normal.LengthSquared() < 1e-12f)
            return false;
        position += Vector3.Normalize(normal) * (best - margin); // the normal points from the body towards the other shape
        return true;
    }
```

Why this is bit-identical to lines 1025-1133: the three Jitter2 branches return `d = −epaDepth`, `d = distance`,
`d = −penetration`; the old skips `epaDepth <= −2m`, `distance >= 2m`, `penetration < 0` are `d >= 2m` (the first two)
and the backend's `false` (the third); the old pushes `max(0, epaDepth + m)`, `max(0, m − distance)`,
`penetration + m` equal `max(0, m − d)` exactly in IEEE arithmetic (`a − (−b) = a + b`, addition commutes,
`m + penetration > 0`); candidates come in the same tree-query order and the position is still updated between pairs.

- [ ] **Step 9: Debug draw and `Dispose`.** In `DrawDebug` (line 1153):
`PhysicsBodyKind.Dynamic => _backend.IsSleeping(record.Handle) ? SleepingColor : DynamicColor,`. `Dispose` (lines 1165-1187):

```csharp
    /// <summary>Detaches the space's objects, then disposes the backend.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var record in _records)
        {
            record.Removed = true;
            record.Node.Record = null;
            record.Handle = BackendBody.None;
        }

        _records.Clear();
        _moving.Clear();
        _areas.Clear();
        _monitors.Clear();
        _dirty.Clear();
        Array.Clear(_byHandle);
        if (ReferenceEquals(World3D.PhysicsSpace, this))
            World3D.PhysicsSpace = null;
        _backend.Dispose();
    }
```

Delete lines 1189-1247 (`LayerFilter`, `QueryFilters`, `Filters`, `ProxyScratch`, `ProxySink`, now in `Jitter2Backend`).

- [ ] **Step 10: Rewrite `PhysicsDirectSpaceState3D.cs`** (records `RayHit3D`/`ShapeCastHit3D` and all doc comments of lines 8-46, 71-74, 103-106, 124 unchanged):

```csharp
using System.Numerics;

namespace MainframeEngine;

// RayHit3D, ShapeCastHit3D and the class doc comment: PhysicsDirectSpaceState3D.cs:8-29 verbatim.
public sealed class PhysicsDirectSpaceState3D
{
    private readonly World3D _world;

    internal PhysicsDirectSpaceState3D(World3D world)
    {
        _world = world;
    }

    private PhysicsSpace3D? Space => _world.PhysicsSpace;

    public bool RayCast(Vector3 from, Vector3 to, out RayHit3D hit, uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null,
        bool hitFromInside = true)
    {
        hit = default;
        var direction = to - from;
        if (Space is not { } space || direction.LengthSquared() < 1e-14f)
            return false;
        space.FlushForQuery();
        if (!space.Backend.RayCast(from, direction, collisionMask, space.HandleOf(exclude), hitFromInside, out var result) ||
            space.RecordOf(result.Body) is not { } record)
            return false;
        hit = new RayHit3D(record.Node, record.ShapeOwner(result.ShapeIndex), result.Position, result.Normal, result.Fraction);
        return true;
    }

    public bool ShapeCast(Shape3D shape, in Transform3D from, Vector3 motion, out ShapeCastHit3D hit,
        uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        hit = default;
        if (Space is not { } space || motion.LengthSquared() < 1e-14f)
            return false;
        var query = shape.DescribeQuery();
        space.FlushForQuery();
        from.Decompose(out var position, out var rotation, out _);
        if (!space.Backend.ShapeCast(query, position, rotation, motion, collisionMask, space.HandleOf(exclude), out var result) ||
            space.RecordOf(result.Body) is not { } record)
            return false;
        hit = new ShapeCastHit3D(record.Node, record.ShapeOwner(result.ShapeIndex), result.Position, result.Normal, result.Fraction);
        return true;
    }

    public int IntersectShape(Shape3D shape, in Transform3D transform, List<CollisionObject3D> results,
        uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(results);
        if (Space is not { } space)
            return 0;
        var query = shape.DescribeQuery();
        space.FlushForQuery();
        transform.Decompose(out var position, out var rotation, out _);
        var hits = space.QueryScratch;
        hits.Clear();
        space.Backend.IntersectShape(query, position, rotation, collisionMask, space.HandleOf(exclude), hits);
        return space.AddHits(hits, results);
    }

    public int IntersectPoint(Vector3 point, List<CollisionObject3D> results, uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (Space is not { } space)
            return 0;
        space.FlushForQuery();
        var hits = space.QueryScratch;
        hits.Clear();
        space.Backend.IntersectPoint(point, collisionMask, space.HandleOf(exclude), hits);
        return space.AddHits(hits, results);
    }
}
```

(Each method keeps its XML doc comment from the old file above it.)

- [ ] **Step 11: `PhysicsServer3D.cs:73`** → `space = new PhysicsSpace3D(viewport, Settings, PhysicsBackendKind.Jitter2);` (Task 11 resolves the setting).

- [ ] **Step 12: Run the gate for the switch**

```bash
just build
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics"
MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsAllocationTests"
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Terrain|FullyQualifiedName~MainframeEngine.Tests.Project"
```

Expected: 0 warnings; everything passes; the trace test reports **passed** (not skipped). A trace difference is a
refactor bug: set `WriteEvery = 1` locally, re-record into a scratch copy from `main` in a worktree to diff the first
differing value, fix the code, never re-record the committed trace.

- [ ] **Step 13: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/PhysicsSpace3D.cs MainframeEngine/Src/Physics/3D/PhysicsDirectSpaceState3D.cs MainframeEngine/Src/Physics/3D/PhysicsServer3D.cs
git commit -F- <<'EOF'
Run PhysicsSpace3D and its queries on PhysicsBackend3D (Jitter2), behaviour unchanged

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 10: Remove the old Jitter2 code and lock the folders

**Files:**
- Modify: `MainframeEngine/Src/Physics/3D/Shapes3D.cs`, `PhysicsServer3D.cs:20` (static constructor)
- Create: `Tests/MainframeEngine.Tests/Physics/BackendSourceRulesTests.cs`

**Interfaces:**
- Consumes: `PhysicsTraceTests.RepoRoot()` (Task 2), `JitterHull.Tessellate` (Task 8).
- Produces: `public sealed class BackendSourceRulesTests`.

- [ ] **Step 1: Write the failing rule test**

```csharp
using System.Text.RegularExpressions;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// Physics libraries stay in their backend folders (ADR 0192, B2): Jitter2 in <c>Src/Physics/3D/Jitter</c>, Jolt through
/// BRUTAL in <c>Src/Physics/3D/Jolt</c>, Box2D.NET in <c>Src/Physics/2D</c>. Counts imports (<c>using Jitter2…</c>) and
/// qualified names (<c>Jitter2.Logger</c>, <c>global::Jitter2</c>) in engine and editor sources and project-wide
/// <c>&lt;Using&gt;</c> items; comments are ignored, and members named like a library (<c>settings.Jitter2</c>,
/// <c>PhysicsBackendKind.Jitter2</c>) are not library uses.
/// </summary>
public sealed class BackendSourceRulesTests
{
    public static TheoryData<string, string, bool> Rules => new()
    {
        { "Jitter2", "MainframeEngine/Src/Physics/3D/Jitter/", true },
        { "Brutal.JoltApi", "MainframeEngine/Src/Physics/3D/Jolt/", false },
        { "Box2D.NET", "MainframeEngine/Src/Physics/2D/", true },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void LibraryNamespacesStayInTheirBackendFolder(string ns, string folder, bool usedThere)
    {
        var root = PhysicsTraceTests.RepoRoot();
        var name = Regex.Escape(ns);
        var use = new Regex($@"^\s*(global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?{name}\b|(?<![\w.])(global::)?{name}\.[A-Z]", RegexOptions.Multiline);
        var violations = new List<string>();
        var found = false;
        foreach (var dir in (string[])["MainframeEngine/Src", "MainframeEngine.Editor/Src"])
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var comment = lines[i].IndexOf("//", StringComparison.Ordinal);
                    var code = comment < 0 ? lines[i] : lines[i][..comment];
                    if (!use.IsMatch(code))
                        continue;
                    if (relative.StartsWith(folder, StringComparison.Ordinal))
                        found = true;
                    else
                        violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        foreach (var project in (string[])["MainframeEngine/MainframeEngine.csproj", "MainframeEngine.Editor/MainframeEngine.Editor.csproj"])
            if (File.ReadAllText(Path.Combine(root, project)).Contains($"<Using Include=\"{ns}", StringComparison.Ordinal))
                violations.Add($"{project}: a project-wide <Using> of {ns}");

        Assert.True(violations.Count == 0, $"{ns} is used outside {folder}:\n" + string.Join('\n', violations));
        if (usedThere)
            Assert.True(found, $"No {ns} use found in {folder}: the rule's pattern no longer matches real uses.");
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~BackendSourceRulesTests"` → the Jitter2 case fails, listing `Shapes3D.cs` (usings, `RigidBodyShape`, `ShapeHelper`) and `PhysicsServer3D.cs` is clean; Box2D and Jolt pass.

- [ ] **Step 2: Contract `Shapes3D.cs`.** Delete `using Jitter2.Collision.Shapes;` and `using Jitter2.LinearMath;` (lines 2-3), `_queryShape` (15), `CreateShapes` (20-31), `CreateBaseShape` (33-34), `GetQueryShape` (36-42), `_queryShape = null;` in `OnShapeChanged`, every `CreateBaseShape` override (lines 82, 106, 148, 186-187, 217-225, 298-299, 406-407), `ConcavePolygonShape3D.CreateShapes`/`AddTriangleShapes` (277-296) and `HeightMapShape3D.CreateShapes` (403-404). `ConvexPolygonShape3D.DrawDebug` becomes:

```csharp
    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color)
    {
        if (!_hullValid)
        {
            _hullValid = true;
            _hullTriangles = JitterHull.Tessellate(Points, 2);
        }

        lines.AddTriangles(transform, _hullTriangles, color);
    }
```

and its class comment (lines 193-196) ends "the hull is only built for debug drawing (Jitter2's tessellation, through `JitterHull`)."
Update `Shape3D`'s class comment "Changing a property rebuilds every body using it at the next physics step." (unchanged) and the `CylinderShape3D` comment `// Note Jitter2's argument order` moves with the code (it is in `Jitter2Backend.CreateConvex` already).

- [ ] **Step 3: `PhysicsServer3D`.** Delete the static constructor (`static PhysicsServer3D() => JitterLog.EnsureInstalled();`): `Jitter2Backend`'s constructor installs the listener before its `World` exists.

- [ ] **Step 4: Run everything physics**

```bash
just build
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics"
MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"
grep -rn "Jitter2" --include='*.cs' MainframeEngine/Src MainframeEngine.Editor/Src | grep -v "Src/Physics/3D/Jitter/"
```

Expected: 0 warnings; all pass (the rule test included); the trace passes; the grep lists only comments, `Jitter2PhysicsSettings`, `PhysicsBackendKind.Jitter2`, `.Jitter2.` member accesses and the `Jitter2 =>` property.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Shapes3D.cs MainframeEngine/Src/Physics/3D/PhysicsServer3D.cs Tests/MainframeEngine.Tests/Physics/BackendSourceRulesTests.cs
git commit -F- <<'EOF'
Keep Jitter2 inside Physics/3D/Jitter and enforce library folders with a source rule test

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 11: Backend selection and the Jolt fallback

**Files:**
- Modify: `MainframeEngine/Src/Physics/3D/PhysicsServer3D.cs:3-13` (class doc), `:67-79` (`AddObject`)
- Create: `Tests/MainframeEngine.Tests/Physics/PhysicsBackendSelectionTests.cs`

**Interfaces:**
- Consumes: `PhysicsSettings3D.Backend`, `PhysicsSpace3D.BackendKind`.
- Produces: `internal PhysicsBackendKind PhysicsServer3D.ResolveBackend()`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>Which library a space runs on (B2: Jitter2; selecting Jolt falls back with one warning per server).</summary>
public sealed class PhysicsBackendSelectionTests
{
    [Fact]
    public void Jitter2IsTheDefaultBackend()
    {
        using var h = new PhysicsHarness3D();
        h.AddFloor();
        Assert.Equal(PhysicsBackendKind.Jitter2, h.Space.BackendKind);
    }

    [Fact]
    public void SelectingJoltRunsJitter2AndWarnsOncePerServer()
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            using var h = new PhysicsHarness3D(new PhysicsSettings3D { Backend = PhysicsBackendKind.Jolt, MultiThreaded = false });
            h.AddFloor();
            var viewport = h.Add(new SceneViewport { Name = "Sub" });
            viewport.AddChild(new StaticBody3D { Name = "Late" }); // a second space of the same server
            var box = h.AddBox(new Vector3(0, 2, 0));
            h.RunSeconds(2f);

            Assert.Equal(2, h.Server.Spaces.Count);
            Assert.All(h.Server.Spaces, s => Assert.Equal(PhysicsBackendKind.Jitter2, s.BackendKind));
            Assert.InRange(box.Position.Y, 0.45f, 0.55f);
            Assert.Single(sink.Snapshot(), e => e.Message.Contains("Jolt is not available on", StringComparison.Ordinal) &&
                                                e.Message.Contains("using Jitter2.", StringComparison.Ordinal));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }
}
```

- [ ] **Step 2: Run them to see the second fail** (no warning is logged yet).

- [ ] **Step 3: Implement.** Add `using System.Runtime.InteropServices;`; class doc (lines 3-13):

```csharp
/// <summary>
/// The 3D physics server: one <see cref="PhysicsSpace3D"/> per <see cref="World3D"/>, created when the first collision
/// object enters that world, each over a <see cref="PhysicsSettings3D.Backend"/> library (Jitter2; Jolt from B3). Ticked
/// by the scene tree's fixed step (after <see cref="Node.OnPhysicsProcess"/>; frozen while paused); interpolates moving
/// bodies for rendering; draws collision shapes into the viewport's <see cref="DebugLines"/> when <see cref="DebugDrawEnabled"/>.
/// </summary>
/// <remarks>
/// <see cref="Engine"/> registers one with <see cref="EngineOptions.Physics3D"/>. Trees without one (tools, tests) get a
/// default server registered by <see cref="For"/> when the first body enters. Library calls stay inside each backend's
/// folder (<c>Src/Physics/3D/Jitter</c>; the packages are pinned).
/// </remarks>
```

add the field `private bool _warnedJolt;`, and:

```csharp
    /// <summary>The backend new spaces get: the setting, or Jitter2 (with one warning per server) where it is not available.</summary>
    internal PhysicsBackendKind ResolveBackend()
    {
        if (Settings.Backend != PhysicsBackendKind.Jolt)
            return Settings.Backend;
        if (!_warnedJolt)
        {
            _warnedJolt = true;
            Log.Warning($"[Physics] Jolt is not available on {RuntimeInformation.RuntimeIdentifier} (the Jolt backend arrives in B3); using Jitter2.");
        }

        return PhysicsBackendKind.Jitter2;
    }
```

and in `AddObject` (line 73): `space = new PhysicsSpace3D(viewport, Settings, ResolveBackend());`.

- [ ] **Step 4: Run** `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsBackendSelectionTests"` → 2 pass; physics suite and trace gate → pass.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/PhysicsServer3D.cs Tests/MainframeEngine.Tests/Physics/PhysicsBackendSelectionTests.cs
git commit -F- <<'EOF'
Resolve physics.3d.backend per server; Jolt falls back to Jitter2 with one warning until B3

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 12: Docs and all gates

**Files:**
- Modify: `CLAUDE.md:98-99`, `:237`; `docs/design/physics.md`; `docs/design/project-and-gamehost.md:60,69-74`; `docs/design/future/brutal-backend.md` (B2 seam table, gate paragraph); `docs/milestones.md:349`; `memory/context/progress-log.md` (append)

**Interfaces:**
- Consumes: the finished seam.
- Produces: docs that match the code.

- [ ] **Step 1: `CLAUDE.md`.** Lines 98-99 →

```markdown
- `MainframeEngine/Src/Physics/` — M6 physics: `PhysicsServer3D` / `PhysicsServer2D` (Box2D.NET), spaces, body
  nodes (`StaticBody*`, `RigidBody*`, `CharacterBody*`, `Area*`), `CollisionShape*`, shape resources, queries; 3D runs
  on an internal backend seam (`3D/Backend/PhysicsBackend3D`, ADR 0192 B2): `3D/Jitter/Jitter2Backend` (Jitter2), `3D/Jolt/` (B3)
```

Line 237 → `Keep Jitter2 calls inside Src/Physics/3D/Jitter, Jolt (BRUTAL) calls inside Src/Physics/3D/Jolt and Box2D.NET calls inside Src/Physics/2D (pinned versions; APIs change between minors; BackendSourceRulesTests enforces it). The engine side of 3D physics talks to PhysicsBackend3D only. Box2D.NET allocates`
(the sentence continues as before: "inside its own step; engine code must not. See `docs/design/physics.md`."), with the identifiers in backticks.

- [ ] **Step 2: `docs/design/physics.md`.**
  - Libraries paragraph after the table (the "A second 3D backend…" sentence) → "3D physics runs behind an internal seam, `PhysicsBackend3D` ([Backends](#backends-3d)): Jitter2 is the backend today; Jolt through BRUTAL arrives in B3 ([BRUTAL backend](future/brutal-backend.md)). Physics is single precision and offers no cross-platform lockstep mode ([ADR 0020](../../memory/decisions/0020-physics-precision-and-determinism.md))."
  - Key types: the first row's note → "one `PhysicsBackend3D` per `World3D`"; add a row `| PhysicsBackend3D, ShapeDescription3D, Jitter2Backend | [Physics/3D/Backend/](../../MainframeEngine/Src/Physics/3D/Backend/), [Physics/3D/Jitter/](../../MainframeEngine/Src/Physics/3D/Jitter/) | internal seam (B2) |`.
  - Servers and spaces, Settings bullet → "**Settings** (`PhysicsSettings3D`): `Backend` (`Jitter2`; `Jolt` from B3, until then it falls back with a warning), `Gravity` (−9.81 Y), `SubstepCount` (1), `AllowDeactivation`, `MultiThreaded`, `Deterministic`, and per backend `Jitter2.SolverIterations` (6) / `RelaxationIterations` (4) and `Jolt.*` (read from B3). (`PhysicsSettings2D`): …" (2D part unchanged); `project.mfproj` keys `physics.3d.backend`, `physics.3d.jitter2.*`, `physics.3d.jolt.*` (format 4 moved the solver keys under `jitter2`).
  - The bullet "All Jitter2 / Box2D calls live in `PhysicsSpace*` (plus the shape resources' `Create*` methods)." → "Jitter2 calls live in `Src/Physics/3D/Jitter/` (`Jitter2Backend`), Box2D calls in `Src/Physics/2D/`; `BackendSourceRulesTests` fails otherwise."
  - The "Shape resources → library shapes" intro sentence gains: "Shape resources describe themselves (`Shape3D.Describe` → `ShapeDescription3D`: kind, sizes, the body-frame transform with the body's scale baked in, the resource's arrays, `ChangeCount`); the backend builds the library shapes."
  - New section after "Servers and spaces":

```markdown
## Backends (3D)

`PhysicsSpace3D` holds the engine's behaviour and calls one internal `PhysicsBackend3D` per space
([BRUTAL backend](future/brutal-backend.md) B2): records, dirty tracking and the flush before steps and queries,
membership lists, creation order, interpolation, user moves (teleport, move, kinematic target), the layer rule
(`PhysicsSpace3D.Collides`, ADR 0021), signal diffing, order and removal exits (ADR 0022), damping per step, gravity-scale
semantics, the concave-on-dynamic error, disabled-node removal, debug draw and `MoveAndSlide` stay engine code. Bodies
are `BackendBody` handles (per backend; index 0 is none), shapes `ShapeDescription3D`s, results go into lists the space
owns; every member is main-thread, between steps and allocation-free.

| Group | Members |
|---|---|
| World | `Create(kind, settings)`, `Kind`, `Gravity`, `ActiveBodyCount`, `Step(dt)`, `Dispose()` |
| Bodies | `CreateBody(desc)` (no shapes: they come at the first flush), `DestroyBody`, `SetKind`, `SetShapes(body, shapes, dynamicMass, errors)`, `SetFilter(body, layer, mask)`, `SetProperties`, `SetMaterial` |
| State | `GetPose`, `SetPose`, `Teleport`, `MoveKinematic`, `Get/SetLinearVelocity`, `Get/SetAngularVelocity`, `ApplyForce/Impulse/Torque`, `IsSleeping`, `Wake`, `WakeTouching`, `ClearContacts` |
| Scans | `CollectTouching`, `CollectOverlaps` (false: the area has no shapes) |
| Queries | `RayCast`, `ShapeCast`, `IntersectShape`, `IntersectPoint` |
| Character | `CastBody`, `GetShapeCount`, `CollectNearbyShapes` + `Penetration` (recovery pushes the pose between pairs), `Distance` (settle) |

**Jitter2** (`Jitter2Backend`): one `World` per space; the broad-phase filter calls `Collides`; areas have no Jitter2 body
(their shapes are queried by tree AABB and narrow phase after each step); a Jitter2 shape is one body's tree proxy, so
shapes are built per body, and query shapes are cached per space by resource and `ChangeCount` in a weak table; a
gravity scale other than 0/1 becomes a force; filter changes drop contact caches; the sphere contact filter (ADR 0134),
the EPA → distance → MPR chain (ADR 0128) with `JitterLogMute`, and the hull tessellation of debug draw (`JitterHull`)
live in `Src/Physics/3D/Jitter/`. `physics.3d.backend: "jolt"` logs `[Physics] Jolt is not available on <rid> (…); using
Jitter2.` once per server until B3.
```

  - Testing paragraph: append "`PhysicsSeamTests` (shared resources, handles across spaces, kind changes and removals in handlers, sub-viewport disposal), `Jitter2BackendTests`, `ShapeDescription3DTests`, `PhysicsLayerRuleTests`, `PhysicsBackendSelectionTests`, `BackendSourceRulesTests`, and the opt-in bit-exact trace (`MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"`, per RID, `MF_PHYSICS_TRACE=record` re-records)."

- [ ] **Step 3: `docs/design/project-and-gamehost.md`.** Line 60 `"format": 3,` → `"format": 4,`; lines 71-72 →

```
    "3d": { "backend": "jitter2", "gravity": [0, -20, 0], "substeps": 2, "allowDeactivation": false, "multiThreaded": false,
            "deterministic": true, "jitter2": { "solverIterations": 8, "relaxationIterations": 3 } },
```

and below the example add one sentence: "`physics.3d.backend` picks the 3D library (`jitter2`, `jolt` from B3); `jitter2`/`jolt` hold each one's own keys (format 4 moved `solverIterations`/`relaxationIterations` under `jitter2`)."

- [ ] **Step 4: `docs/design/future/brutal-backend.md`.** Replace the B2 `PhysicsBackend3D` table (lines 263-270) with the table of Step 2 and add under it: "Built (B2): `CreateBody` takes no shapes (they arrive at the space's first flush, as before); mass is a `SetShapes` argument because inertia follows the shapes; friction and bounce have their own `SetMaterial` (the material changes on its own); `SetFilter` takes no area flag (the kind says it); `CastBody` takes no margin (the caller extends the motion); recovery is `CollectNearbyShapes` + `Penetration`, because the engine moves the pose between pairs. Shape indices in hits are description indices; the record maps them to `CollisionShape3D`s." In "B2's gate", append: "and a bit-exact trace of a Jitter2 scene recorded before the seam (`PhysicsTraceTests`)." Status line 3: `**Status:** 🟨 B2 done ·`.

- [ ] **Step 5: `docs/milestones.md:349`** → the B2 row's status `⬜` → `✅`.

- [ ] **Step 6: `memory/context/progress-log.md`** — append:

```markdown

### 2026-10-16 — BRUTAL B2: the 3D physics seam (ADR 0192)
- `PhysicsBackend3D` (internal, `Src/Physics/3D/Backend/`) under `PhysicsSpace3D`; every Jitter2 call moved to `Jitter2Backend` (`Src/Physics/3D/Jitter/`, with `JitterMath`, `SphereContactFilter`, `JitterLogMute`/`JitterLog`, `JitterHull`). Shapes describe themselves (`ShapeDescription3D`, `Shape3D.ChangeCount`).
- `physics.3d.backend` + `jitter2`/`jolt` groups, project format 4 (migration moves the solver keys); editor Project Settings › Physics shows the selected backend's group. Jolt selected → Jitter2 with one warning per server until B3.
- Gate: physics tests, both allocation gates, `physics`/`physics-debug` goldens, determinism, benchmarks unchanged; bit-exact trace `MF_PHYSICS_TRACE=1` (per RID). `BackendSourceRulesTests` keeps libraries in their folders.
```

- [ ] **Step 7: Run all gates** (display awake for the render tests: `caffeinate -u -t 600 &`)

```bash
just build
dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false
just test
just test-render
just format-check
just shaders-check
just template-smoke
MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"
just bench '*PhysicsBenchmarks*'
```

Expected: 0 warnings in both builds; all unit, editor and video tests pass (the trace skipped there); render tests pass
with the committed `physics`, `physics-debug` goldens and `PhysicsCapturesAreDeterministicAcrossRuns` (no
`just golden-update`); format and shaders clean; template smoke passes; the trace passes; the three `PhysicsBenchmarks`
(`Step1kRigidBodies3D`, `Raycast10k3D`, `Step1kRigidBodies2D`) are within 10 % of `baseline.json` with unchanged
allocations (run on a quiet machine; a regression is investigated, not re-baselined). If `just format-check` fails, run
`just format` and re-run the tests.

- [ ] **Step 8: Commit**

```bash
git add CLAUDE.md docs/design/physics.md docs/design/project-and-gamehost.md docs/design/future/brutal-backend.md docs/milestones.md memory/context/progress-log.md
git commit -F- <<'EOF'
Document the 3D physics backend seam (B2)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

Do not push; tell the user the branch is ready (they push once and open the PR).

---

## Self-review

| Spec B2 requirement | Task |
|---|---|
| Layout: `Backend/PhysicsBackend3D.cs`, `ShapeDescription3D.cs`, `BackendResults3D.cs`, `Jitter/Jitter2Backend.cs`, `JitterMath.cs`, `SphereContactFilter.cs`, `JitterLogMute.cs` | 6, 8 |
| CLAUDE.md rule "inside its backend folder" + source grep test (`Jitter2.`, `Brutal.JoltApi`, Box2D) | 10, 12 |
| Stays in the engine: records, dirty/flush, membership, sequence numbers | 9 (Steps 1-4) |
| Interpolation, `BeforeFixedSteps`/`AfterFixedSteps` | 9 (unchanged lines 648-684) |
| User moves → teleport / move / kinematic target and the one-step velocity | 9 (Steps 5-6; `MoveKinematic` uses `AngularVelocityBetween`) |
| `Collides(layerA, maskA, areaA, layerB, maskB, areaB)` called by backend filters | 6, 8 (`LayerFilter`, `CollectOverlaps`) |
| Signal diffing, order, dispatch, removal exits | 9 (Steps 3, 7; `Touch`/`Untouch`/`Dispatch`/`EmitExitsForRemoved` verbatim) |
| Damping conversion, gravity-scale semantics, concave-on-dynamic error, disabled-node removal | 9 (Steps 4-5); disabled removal untouched in `CollisionObject3D` |
| Debug draw from resources and render poses | 9 (Step 9), 10 (`JitterHull`) |
| `MoveAndSlide` engine logic over `CastBody`/penetrations/`Distance` | 9 (Step 8; `CharacterBody3D` unchanged) |
| `PhysicsBackend3D` abstract, `BackendBody(int Index)` | 6 |
| World / Bodies / State / Scans / Queries / Character groups | 6 (contract), 8 (Jitter2) |
| Main-thread, allocation-free, `Step` once per fixed step, sub-steps the backend's | 8, 9; allocation gates in 9, 12 |
| `Shape3D.Describe(...)` + `ChangeCount`; descriptions with kind, sizes, transform, owner, resource arrays | 7 (owner → `BodyRecord3D.ShapeOwners` by description index, 9) |
| Per-space native shape cache by (resource, ChangeCount, scale) | 8 (Jitter2: query shapes only, body shapes are per-body proxies) |
| `PhysicsSettings3D.Backend`, `Jitter2`/`Jolt` sub-objects | 3 |
| `project.mfproj` keys + migration moving the solver keys | 4 |
| Editor Project Settings → Physics: shared keys + selected backend's group | 5 |
| `EngineOptions.Physics3D` carries the object; `PhysicsServer3D(settings)` creates backends | 9, 11 (`EngineOptions` unchanged) |
| Selecting Jolt in B2 | 11 |
| Gate: physics tests, both allocation gates, goldens, determinism, benchmarks | 9, 12 |
| `ShapeDescription3D` test per shape kind (sizes, transforms, scale baking) | 7 |
| CLAUDE.md + physics.md | 12 |

Name consistency checked across tasks: `PhysicsBackend3D`, `Jitter2Backend`, `JitterBody`, `BackendBody.None/IsNone`,
`BodyDescription3D`, `BodyProperties3D(Simulated, LinearDampingPerStep, AngularDampingPerStep, GravityScale, AxisLocks, Ccd, CanSleep)`,
`BackendHit3D{Body, ShapeIndex, Position, Normal, Fraction}`, `BackendContact3D(Distance, Normal, Point)`,
`BackendShapeRef(Body, Shape)`, `ShapeError3D(ShapeIndex, Message)`, `ShapeKind3D`, `ShapeDescription3D{Kind, Resource,
ChangeCount, Size, Radius, Height, Points, Linear, Translation, MidHeight}`, `Shape3D.Describe/DescribeConvex/DescribeQuery/ChangeCount`,
`HeightMapShape3D.GetFaceArray`, `PhysicsSpace3D.Collides/BakeShapeTransform/AngularVelocityBetween/MaterialOf/RecordOf/HandleOf/QueryScratch/AddHits/Backend/BackendKind`,
`BodyRecord3D.Handle/ShapeOwners/ShapeOwner/HasBody`, `PhysicsServer3D.ResolveBackend`, `JitterLog.EnsureInstalled`,
`JitterLogMute`, `JitterHull.Tessellate`, `PhysicsBackendKind`, `Jitter2PhysicsSettings`, `JoltPhysicsSettings`,
`ProjectSetting.Visible`, `MF_PHYSICS_TRACE`.
