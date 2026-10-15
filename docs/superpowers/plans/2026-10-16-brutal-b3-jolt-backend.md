# BRUTAL B3: Jolt Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A second 3D physics backend, `JoltBackend` (Jolt 5.5.0 through `Ahwoo.BRUTAL.Jolt` 2026.10.2), implementing B2's
`PhysicsBackend3D` exactly, selected with `physics.3d.backend: "jolt"` and falling back to Jitter2 with one log line where
the native library is missing (osx-x64), and every shared 3D physics test, both allocation gates, the `physics`,
`physics-debug` and determinism render tests and the physics benchmarks running on both backends, so B4 can decide the
default on evidence.

**Architecture:** Everything Jolt lives in `MainframeEngine/Src/Physics/3D/Jolt/`, split by responsibility: `JoltRuntime`
(process-wide init, availability, log bridge, the immutable broad-phase objects every space shares), `JoltApiFacts`
(binding facts pinned by tests), `JoltMath` (bit-cast conversions), `JoltLayers` (interned `(layer, mask)` object layers
and the three filters), `JoltShapes` (neutral description → native shape, per-space refcounted cache), `JoltBodies`
(handle table and body slots), `JoltContacts` (the drained `ContactEventListener` → per-body touching sets, overflow →
query mode), `JoltQueries` (rays, casts, overlaps, the character primitives), `JoltNativeLedger` (debug leak check) and
`JoltBackend`, the façade that owns the world and the teardown order. `PhysicsSpace3D` (engine behaviour) is not touched;
`PhysicsServer3D` resolves the backend through `JoltRuntime` and re-creates an empty world's space when the setting
changes. The shared test suites become abstract classes with `…_Jitter2`/`…_Jolt` subclasses over a backend-aware
`PhysicsHarness3D` with named tolerances.

**Tech Stack:** C# 13 / .NET 10, `Ahwoo.BRUTAL.Jolt` 2026.10.2 (`Brutal.JoltApi`, Jolt 5.5.0, single precision, 16-bit
object layers), `Brutal.Numerics` (`float3`, `floatQuat`, `float4x4`), Jitter2 2.9.0 (unchanged), xUnit v3, BenchmarkDotNet,
RmlUi (Demo panel), `just`.

**Spec:** docs/design/future/brutal-backend.md (B3) · ADR 0192 · depends on the B1 and B2 plans
(`docs/superpowers/plans/2026-10-16-brutal-b1-attribution.md`, `docs/superpowers/plans/2026-10-16-brutal-b2-physics-seam.md`)

## Global Constraints

- Branch `brutal/b3-jolt-backend`, cut from `main` after B1 and B2 are merged; stop and ask the user if `MainframeEngine/Src/Physics/3D/Backend/PhysicsBackend3D.cs` or the `Ahwoo.BRUTAL.Jolt` `[2026.10.2]` pin in `Directory.Packages.props` is missing.
- Two PRs: **PR 1 = Tasks 1–5** (Jolt core: runtime, facts, layers, shapes, bodies, queries; not selectable from a project), **PR 2 = Tasks 6–14** (selection, signals, characters, both-backend suites, gates, goldens, benchmarks, Demo, docs).
- The B2 contract (`BackendResults3D.cs`, `ShapeDescription3D.cs`, `PhysicsBackend3D.cs` as listed in the B2 plan's "The contract") is implemented exactly; it is not edited, except `PhysicsBackend3D.Create`'s `Jolt` arm (Task 6).
- `Brutal.JoltApi` and `Brutal.Numerics` appear only in `MainframeEngine/Src/Physics/3D/Jolt/` (engine) — `BackendSourceRulesTests` enforces both; tests may use them anywhere under `Tests/`.
- No package is added, removed or re-versioned (B1 pinned `Ahwoo.BRUTAL.*` exactly; Jitter2 stays `2.9.0`).
- Jitter2 behaviour does not change: `MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"` passes (1 passed, not skipped) after every task.
- Default backend stays `Jitter2` (`PhysicsSettings3D.Backend`); B4 flips it.
- Zero managed allocations in steady state on both backends: `PhysicsAllocationTests_*.SteadyState3DPhysicsDoesNotAllocate(false)` = 0 B, `(true)` = 0 B outside the library's step; Jolt's native allocations do not count.
- Every `PhysicsBackend3D` member is main-thread only, called between steps; Jolt managed callbacks run on Jolt worker threads and read only state written between steps.
- Native handles have no finalizers: every handle the backend creates is disposed in `JoltBackend.Dispose` in the order bodies → `PhysicsSystem` → contact listener → filters and query settings → job systems → temp allocator → shapes and creation helpers; Debug builds count them (`JoltNativeLedger`, 0 outstanding after dispose).
- Per-backend differences are named fields of `PhysicsTolerances` in `Tests/MainframeEngine.Tests/Physics/PhysicsHarness.cs` and rows of the physics doc's Backends section; never inline numbers in a shared test.
- Jolt test classes skip with `Jolt is not available on <rid>: <reason>` when `JoltRuntime.TryInitialize` fails (osx-x64 CI never runs them).
- Physics test command: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics"`.
- Build: 0 warnings (warnings are errors), Debug and `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`.
- Commit after each task, locally only (no push except at the two PR boundaries, when the user says so). Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Final gates (Task 14): `just build`; the Release build above; `just test`; `just test-render` (display awake: `caffeinate -u -t 600 &`); `just render-tests-linux`; `just format-check`; `just shaders-check`; `just template-smoke`; the trace gate; `just bench '*PhysicsBenchmarks*'` on the quiet Mac that recorded `baseline.json`.

## Review Focus

1. **A space disposed while Jolt worker threads still run.** Expected: `PhysicsSystem.Update` returns only after its jobs finish (Jolt: "no jobs will be running when this function returns"), `JoltBackend.Dispose` destroys the thread pool after the system and joins its workers; creating, stepping multi-threaded and disposing 40 spaces in a row neither crashes nor leaves threads behind (process thread count back within 4 of the start), and the ledger reports 0 outstanding handles. Test: `JoltBackendTests.DisposingMultiThreadedSpacesRightAfterAStepJoinsTheirWorkers` (Task 4).
2. **More than 16 383 distinct (layer, mask) pairs in one space.** Expected: the 16 384th distinct pair throws `InvalidOperationException` naming the limit and the Jitter2 alternative from `CreateBody`/`SetFilter`, before anything native is created (the handle slot is returned, no Jolt body leaks); pairs no longer used by any body free their index for reuse, so the limit is on pairs in use at once; the space keeps stepping and querying. Tests: `JoltLayerTableTests.TheTableHolds16383PairsReusesFreedOnesAndRefusesMore` (Task 2), `JoltBackendTests.ABodyBeyondTheLayerPairLimitIsRefusedAndTheSpaceKeepsWorking` (Task 4).
3. **A body teleported into a sensor in the same step it is removed.** Expected: a body teleported into an area and freed before the next step never enters; a body freed inside its own `BodyEntered` handler exits once (engine removal exit) and the `Removed` contact event Jolt reports for its old body id in the next update is ignored (no exception, no ghost, no second exit), and a body created right after with the same backend handle inherits nothing. Tests: `JoltContactTests.ABodyTeleportedIntoAnAreaAndFreedBeforeTheStepNeverEntersIt`, `JoltContactTests.ABodyFreedInItsEnterHandlerExitsOnceAndItsLateRemovedEventIsIgnored` (Task 7).
4. **A shape resource edited while shared by bodies in two spaces.** Expected: each space has its own cache keyed by (resource, `ChangeCount`, linear map); the edit bumps `ChangeCount`, both spaces rebuild their bodies at their next flush, both see the new geometry in queries, and each space releases its old native shape when the last of its bodies stops using it (ledger shape count unchanged after the rebuild). Tests: `JoltBackendTests.EditingASharedResourceRebuildsInBothSpacesAndReleasesTheOldShapes` (Task 4) and B2's `PhysicsSeamTests.EditingASharedShapeRebuildsItsBodiesInEverySpace` on Jolt (Task 9).
5. **A filter callback throwing on a worker thread.** Expected: BRUTAL's thunk catches it; after `Update` the backend calls `TakeCallbackException()` on every managed callback object and rethrows on the main thread as `InvalidOperationException("A Jolt collision filter threw during the physics step; see the inner exception.")` with the original as inner exception; the space stays usable (the next step succeeds once the cause is gone) and disposes cleanly. Test: `JoltBackendTests.AThrowingLayerFilterRethrowsOnTheMainThreadAfterTheStep` (Task 4).

---

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `MainframeEngine/Src/Physics/3D/Jolt/JoltApiFacts.cs` | create | binding facts the backend relies on (body-id layout, job limits, height-field rules, diagonal), each pinned by `JoltApiFactsTests` |
| `MainframeEngine/Src/Physics/3D/Jolt/JoltRuntime.cs` | create | `TryInitialize`, availability, trace/assert bridge to `Log`, friction/restitution combine callbacks, the shared broad-phase layer table and filters |
| `MainframeEngine/Src/Physics/3D/Jolt/JoltMath.cs` | create | `System.Numerics` ↔ `Brutal.Numerics` bit casts, centre-of-mass transforms |
| `MainframeEngine/Src/Physics/3D/Jolt/JoltLayers.cs` | create | `JoltLayerTable` (interned pairs, object-layer encoding, the layer rule over it), `JoltObjectLayerPairFilter`, `JoltObjectVsBroadPhaseFilter`, `JoltNoAreasFilter` |
| `MainframeEngine/Src/Physics/3D/Jolt/JoltShapes.cs` | create | `JoltShapeBuilder` (description → native shape, scale decomposition, convex radius, height field or mesh) and `JoltShapeCache` (per space, refcounted body shapes, bounded query shapes, warn-once) |
| `MainframeEngine/Src/Physics/3D/Jolt/JoltBodies.cs` | create | `JoltBody` (one slot: id, kind, filter, shapes, contacts), `JoltChild`, `JoltBodyTable` (handles from 1, freed reuse, Jolt id → slot) |
| `MainframeEngine/Src/Physics/3D/Jolt/JoltContacts.cs` | create | `JoltContactTracker`: the `ContactEventListener`, drain into per-body sets, overflow → doubled capacity + query mode |
| `MainframeEngine/Src/Physics/3D/Jolt/JoltQueries.cs` | create | `JoltQueries`: query filters and settings, result buffers, rays/casts/overlaps/points, contact rebuilds, `CastBody`/`CollectNearbyShapes`/`Penetration`/`Distance` |
| `MainframeEngine/Src/Physics/3D/Jolt/JoltNativeLedger.cs` | create | Debug-only count of live native handles per kind |
| `MainframeEngine/Src/Physics/3D/Jolt/JoltBackend.cs` | create | `JoltBackend : PhysicsBackend3D`: world objects, every override, `Step`, teardown |
| `MainframeEngine/Src/Physics/3D/Backend/PhysicsBackend3D.cs` | modify (Task 6) | `Create`'s `Jolt` arm |
| `MainframeEngine/Src/Physics/3D/PhysicsServer3D.cs` | modify (Task 6) | `ResolveBackend` through `JoltRuntime`, `ForceJoltUnavailableForTesting`, space re-creation on a backend change |
| `MainframeEngine/Src/Physics/PhysicsSettings.cs` | modify (Task 6) | `Backend`/`Jolt` doc comments |
| `Tests/MainframeEngine.Tests/Physics/Jolt/RawJoltWorld.cs` | create | a bare Jolt world for the facts tests, `SkipUnlessAvailable` |
| `Tests/MainframeEngine.Tests/Physics/Jolt/JoltApiFactsTests.cs` | create | the spike's results as tests |
| `Tests/MainframeEngine.Tests/Physics/Jolt/JoltRuntimeTests.cs`, `JoltMathTests.cs`, `JoltLayerTableTests.cs`, `JoltShapeTests.cs`, `JoltBackendTests.cs`, `JoltQueryTests.cs`, `JoltContactTests.cs`, `JoltCharacterTests.cs` | create | the Jolt parts on their own |
| `Tests/MainframeEngine.Tests/Physics/PhysicsHarness.cs` | modify | backend constructor, `PhysicsTolerances`, backend guard |
| `Tests/MainframeEngine.Tests/Physics/Physics3DTests.cs`, `Physics3DQueryTests.cs`, `PhysicsSeamTests.cs`, `PhysicsAllocationTests.cs` | modify | abstract + `…_Jitter2`/`…_Jolt` |
| `Tests/MainframeEngine.Tests/Physics/Physics3DEdgeCaseTests.cs`, `DebugDraw3DTests.cs`, `PhysicsBackendDifferenceTests.cs` | create | the 3D halves of `PhysicsEdgeCaseTests`/`DebugDrawTests`, and the named differences |
| `Tests/MainframeEngine.Tests/Physics/PhysicsEdgeCaseTests.cs`, `DebugDrawTests.cs` | modify | 2D halves only |
| `Tests/MainframeEngine.Tests/Physics/PhysicsBackendSelectionTests.cs`, `BackendSourceRulesTests.cs` | modify | Jolt selection and fallback; folder rules |
| `Tests/MainframeEngine.Tests/Terrain/TerrainCollisionTests.cs`, `TerrainCharacterAllocationTests.cs` | create | terrain collision and the walking gate on both backends |
| `Tests/MainframeEngine.Tests/Terrain/Terrain3DTests.cs`, `TerrainAllocationTests.cs` | modify | the moved tests removed |
| `Tests/MainframeEngine.RenderTests.Host/Scenes/PhysicsScene.cs`, `Program.cs` | modify | `physics-jolt`, `physics-debug-jolt` |
| `Tests/MainframeEngine.RenderTests/SceneTests.cs` | modify | the three Jolt render tests |
| `Tests/MainframeEngine.RenderTests/Goldens/{moltenvk,lavapipe}/physics-jolt_frame0030.png`, `physics-jolt_frame0150.png`, `physics-debug-jolt_frame0150.png` | create (recorded) | goldens |
| `Tests/MainframeEngine.Benchmarks/PhysicsBenchmarks.cs`, `PhysicsBenchmarks3D.cs`, `PhysicsBenchmarksTerrain.cs`, `baseline.json` | modify/create | 2D stays; 3D and the terrain build per backend |
| `Examples/Demo/Demo/Src/Physics/Physics3DPanel.cs`, `Physics3DScene.cs`, `Examples/Demo/Content/Physics/panel3d.rml`, `Examples/Demo/Content/locale/**`, `Examples/Demo/Demo.Tests/Physics3DSceneTests.cs` | modify | the backend toggle |
| `docs/design/physics.md`, `docs/design/future/brutal-backend.md`, `docs/design/demo.md`, `docs/design/testing.md`, `docs/milestones.md`, `CLAUDE.md`, `README.md`, `docs-site/src/components/landing/Features.astro`, `Showcase.astro`, `docs/images/demo/physics_3d.png`, `memory/context/progress-log.md` | modify | docs, site, README |

---

## PR 1 — Jolt core (Tasks 1–5)

### Task 1: Branch and the Jolt API facts spike

**Files:**
- Create: `MainframeEngine/Src/Physics/3D/Jolt/JoltApiFacts.cs`, `MainframeEngine/Src/Physics/3D/Jolt/JoltRuntime.cs`
- Create: `Tests/MainframeEngine.Tests/Physics/Jolt/RawJoltWorld.cs`, `Tests/MainframeEngine.Tests/Physics/Jolt/JoltApiFactsTests.cs`
- Modify: `Tests/MainframeEngine.Tests/Physics/BackendSourceRulesTests.cs` (B2 Task 10: the `Brutal.JoltApi` row's `usedThere` `false` → `true`)

**Interfaces:**
- Consumes (binding, `Brutal.JoltApi`, verified from the 2026.10.2 metadata): `Jolt.RegisterDefaultAllocator()`, `Jolt.RegisterTypes()`, `Jolt.VerifyJoltVersionID()`, `Jolt.SetTraceHandler(delegate* unmanaged<byte*, void>)`, `Jolt.SetAssertHandler(delegate* unmanaged<byte*, byte*, byte*, uint, Bool8>) → bool`, `Factory.Create()`, `Factory.Instance { set; }`, `PhysicsSystem.Create()`, `PhysicsSystem.Init(uint maxBodies, uint numBodyMutexes, uint maxBodyPairs, uint maxContactConstraints, BroadPhaseLayerInterface, ObjectVsBroadPhaseLayerFilter, ObjectLayerPairFilter)`, `PhysicsSystem.Update(float, int, TempAllocator, JobSystem) → PhysicsUpdateError`, `PhysicsSystem.SetCombineFriction/SetCombineRestitution(delegate* unmanaged<Body, uint*, Body, uint*, float>)`, `PhysicsSystem.SetContactListener(ContactListener)`, `BroadPhaseLayerInterfaceTable.Create(uint, uint)`/`MapObjectToBroadPhaseLayer(ushort, byte)`, `ObjectLayerPairFilterTable.Create(uint)`/`EnableCollision(ushort, ushort)`, `ObjectVsBroadPhaseLayerFilterTable.Create(BroadPhaseLayerInterface, uint, ObjectLayerPairFilter, uint)`, `TempAllocatorImplWithMallocFallback.Create(uint)`, `JobSystemSingleThreaded.Create(uint)`, `JobSystemThreadPool.Create(uint, uint, int)`, `BodyCreationSettings.Create(Shape, float3, floatQuat, MotionType, ushort)` + setters, `BodyInterface.CreateAndAddBody(BodyCreationSettings, Activation) → uint`, `ContactEventListener(uint, ContactEventMask)`, `.Drain(Span<ContactEvent>, out uint)`, `NarrowPhaseQuery.CastRayClosest/CastShapeClosest/CollideShape`, `HeightFieldShapeSettings.Create(Ptr<float>, float3, float3, uint, Ptr<byte>, ReadOnlySpan<PhysicsMaterial>)`, `MotionProperties.SetMassProperties(AllowedDOFs, MassProperties)`.
- Produces: `internal static class JoltApiFacts` (constants below, `HeightFieldBlockSize(int)`), `internal static unsafe class JoltRuntime` (`TryInitialize(out string?)`, `NativesHaveAsserts`, `OnTrace`, `OnAssert`, `CombineFriction`, `CombineRestitution`), test types `RawJoltWorld` (`SkipUnlessAvailable()`), `JoltApiFactsTests`.

Answers recorded by this task (the spec's open questions; the tests below pin them):

| Question | Answer (2026.10.2 metadata + XML docs, pinned by) |
|---|---|
| 0 workers / single-threaded job system | `JobSystemSingleThreaded.Create(uint maxJobs)` exists; the backend uses it when `MultiThreaded` is false or there is one core. `JobSystemThreadPool.Create(maxJobs, maxBarriers, 0)` is also exercised (`AThreadPoolWithNWorkersSteps(0)`) |
| `float3`/`floatQuat`/`float4x4` layout | sequential `X,Y,Z` / `X,Y,Z,W` floats and four `float4` rows, the sizes of `Vector3`/`Quaternion`/`Matrix4x4`; `Matrix4x4` (row-vector convention, translation in row 4) has the memory of Jolt's column-major `Mat44`, so `JoltMath` uses `Unsafe.BitCast` (`NumericsTypesShareSystemNumericsLayouts`, `WorldTransformsComeBackInSystemNumericsLayout`) |
| `ContactEventListener` | `new ContactEventListener(capacity, ContactEventMask.AddedAndRemoved)` (5, the default), installed with `PhysicsSystem.SetContactListener(listener)` (implicit `ContactListener`), drained between updates with `Drain(Span<ContactEvent>, out uint remaining)`; `Dropped` only grows; events are per sub-shape pair, `Removed` carries ids only; take it off the system (`SetContactListener(default)`) or dispose the system before disposing it |
| Sensors seeing static/kinematic bodies | `BodyCreationSettings.SetIsSensor(true)` + `SetCollideKinematicVsNonDynamic(true)` on a **kinematic, active** body (`SetAllowSleeping(false)`, added with `Activation.Activate`); a static sensor sees only active dynamic/kinematic bodies (`KinematicSensorsSeeStaticAndKinematicBodiesStaticSensorsDoNot`) |
| User data, object layers | `BodyCreationSettings.SetUserData(ulong)`, `SetObjectLayer(ushort)`; at run time `BodyInterface.SetUserData/SetObjectLayer(uint id, …)` |
| Shapes | `*ShapeSettings.Create(...)` then `CreateShape()`, which throws `JoltException` carrying Jolt's error (`ConvexHullErrorsSurfaceAsJoltException`); shapes are reference counted (a body keeps its own reference; `Dispose` releases ours); `StaticCompoundShapeSettings.AddShape(float3, floatQuat, Shape, uint userData)`; `RotatedTranslatedShape.Create(float3, floatQuat, Shape)`; `ScaledShape.Create(Shape, float3)`; `MeshShapeSettings.Create(ReadOnlySpan<Triangle>, ReadOnlySpan<PhysicsMaterial>)` (degenerate triangles sanitized); `HeightFieldShapeSettings`: `sampleCount × sampleCount` samples, `offset + scale · (x, h, y)`, `SetBlockSize` in [2, 8], sample count a multiple of the block size and at least twice it (`HeightFieldSampleCountsFollowTheBlockSizeRule`) |
| `NarrowPhaseQuery` | `CastRayClosest(in RRayCast, RayCastSettings, out RayCastResult, BroadPhaseLayerFilter, ObjectLayerFilter, BodyFilter, ShapeFilter)`; `CastShapeClosest(in RShapeCast, ShapeCastSettings, float3 baseOffset, out ShapeCastResult, …)`; `CollideShape(Shape, float3 scale, in float4x4 comTransform, CollideShapeSettings, float3 baseOffset, Span<CollideShapeResult>, out uint totalHits, …) → uint`; `CollidePoint(float3, Span<CollidePointResult>, out uint totalHits, …) → uint`; filters are `Managed*Filter` subclasses (implicit conversion) |
| Friction / restitution combine | Jitter2 2.9 `ContactData.Init` (IL: `call MathF::Max` on both `get_Friction`, then on both `get_Restitution`): **max** for both. Jolt's defaults are `sqrt(f1·f2)` and `max(r1, r2)`; the backend installs `max` for both through `SetCombineFriction/SetCombineRestitution` (`CustomCombineFunctionsReplaceTheGeometricMean`) |
| Logging / trace hooks | Brutal.Jolt does not reference Brutal.Logging (assembly refs: Interop, Core, Numerics, Strings); Jolt's traces and asserts come through `Jolt.SetTraceHandler`/`SetAssertHandler` (unmanaged callbacks, any thread, must not throw); the bridge forwards to `Log` |
| Damping | Jolt multiplies `v` by `max(0, 1 − c·dt)` per collision step (`LinearDampingRemovesCTimesDtPerStep`) |
| Axis locks | `AllowedDOFs` (bit-for-bit `AxisLock`), changeable at run time with `MotionProperties.SetMassProperties(AllowedDOFs, MassProperties)`: no body re-creation (`AllowedDofsChangeAtRunTimeThroughSetMassProperties`); Jolt cannot lock all six (`Can't lock all axes`), so such a body is held kinematic |
| Function-pointer parameters | plain `delegate* unmanaged<…>` (no calling-convention modifier in the metadata): callbacks are `[UnmanagedCallersOnly]` without `CallConvs` (the C convention on win-x64, linux-x64 and osx-arm64) |

- [ ] **Step 1: Create the branch**

```bash
cd /Users/brogan/mainframe/mainframe-engine
git switch main && git pull --ff-only
test -f MainframeEngine/Src/Physics/3D/Backend/PhysicsBackend3D.cs || { echo "B2 is not on main"; exit 1; }
grep -q 'Ahwoo.BRUTAL.Jolt" Version="\[2026.10.2\]"' Directory.Packages.props || { echo "B1's Jolt pin is not on main"; exit 1; }
git switch -c brutal/b3-jolt-backend
```

- [ ] **Step 2: Write `JoltApiFacts.cs`**

```csharp
namespace MainframeEngine;

/// <summary>
/// What the Jolt backend relies on in <c>Ahwoo.BRUTAL.Jolt</c> 2026.10.2 (Jolt 5.5.0, single precision, 16-bit object
/// layers), each value pinned by <c>Tests/MainframeEngine.Tests/Physics/Jolt/JoltApiFactsTests.cs</c>. Re-run those tests
/// first on every BRUTAL upgrade (ADR 0192: upgrades are their own PRs).
/// </summary>
/// <remarks>
/// Layouts: <c>Brutal.Numerics.float3</c>/<c>floatQuat</c>/<c>float4x4</c> have the sizes and field order of
/// <c>System.Numerics.Vector3</c>/<c>Quaternion</c>/<c>Matrix4x4</c>, and a <c>Matrix4x4</c> (row vectors, translation in
/// row 4) has the memory of Jolt's column-major <c>Mat44</c>: <see cref="JoltMath"/> bit-casts.
/// Callbacks: BRUTAL's function-pointer parameters carry no calling-convention modifier, so callbacks are
/// <c>[UnmanagedCallersOnly]</c> without <c>CallConvs</c> (the C convention on every shipped RID).
/// Sensors: only a kinematic, active sensor with collide-kinematic-vs-non-dynamic sees static and kinematic bodies.
/// Contacts: <c>ContactEventListener</c> events are per sub-shape pair; every <c>Added</c> has one <c>Removed</c>; a
/// destroyed body's contacts end with <c>Removed</c> events (ids only) in the next update; sleeping pairs keep theirs.
/// Combine: Jitter2 2.9 uses the maximum of both bodies' friction and of their restitution (its <c>ContactData.Init</c>).
/// </remarks>
internal static class JoltApiFacts
{
    /// <summary>The binding version these facts were checked against.</summary>
    public const string PackageVersion = "2026.10.2";

    /// <summary>A Jolt <c>BodyID</c> keeps the body index in bits 0–22 and a sequence number in bits 24–31.</summary>
    public const uint BodyIdIndexMask = 0x007F_FFFF;

    /// <summary><c>BodyID::cInvalidBodyID</c>.</summary>
    public const uint InvalidBodyId = 0xFFFF_FFFF;

    /// <summary>Jolt's <c>cMaxPhysicsJobs</c> and <c>cMaxPhysicsBarriers</c>, the job system sizes of its samples.</summary>
    public const uint MaxJobs = 2048;

    public const uint MaxBarriers = 8;

    /// <summary><c>PhysicsSettings.mSpeculativeContactDistance</c>'s default: contacts (not sensors) start this far apart.</summary>
    public const float SpeculativeContactDistance = 0.02f;

    /// <summary>
    /// Whether a Jolt height-field cell splits along the same diagonal as <see cref="HeightMapShape3D"/> (from sample
    /// (x+1, z) to (x, z+1)). Jolt splits from (x, y) to (x+1, y+1), so the backend turns the grid a quarter turn
    /// (<see cref="JoltShapeBuilder"/>) to give the same surface as the engine's triangles and Jitter2.
    /// </summary>
    public const bool HeightFieldDiagonalMatchesEngine = false;

    /// <summary>Whether the loaded natives have asserts (Debug or <c>JOLT_DEBUG</c> builds); the shipped ones do not.</summary>
    public const bool ShippedNativesHaveAsserts = false;

    /// <summary>
    /// The smallest block size Jolt accepts for a height field of <paramref name="sampleCount"/> samples per side (a
    /// divisor in [2, 8] with at least two blocks per side: the best query performance), or 0 when there is none (the
    /// backend then builds a mesh).
    /// </summary>
    public static int HeightFieldBlockSize(int sampleCount)
    {
        for (var block = 2; block <= 8; block++)
        {
            if (sampleCount % block == 0 && sampleCount / block >= 2)
                return block;
        }

        return 0;
    }
}
```

- [ ] **Step 3: Write `JoltRuntime.cs` (initialisation, the log bridge and the combine callbacks; Task 2 adds the shared broad-phase objects)**

```csharp
using System.Runtime.InteropServices;
using Brutal;
using Brutal.JoltApi;

namespace MainframeEngine;

/// <summary>
/// Process-wide Jolt state (ADR 0192, B3). <see cref="TryInitialize"/> loads the <c>jolt</c> native through the
/// assembly's normal <c>runtimes/&lt;rid&gt;/native</c> probing, then registers Jolt's allocator, factory and types and
/// the log bridge, once per process; they stay for the process lifetime (Jolt has no cheap re-init; spaces come and go).
/// Unavailable (osx-x64, a missing native, Android/iOS/consoles later) is remembered with its reason.
/// </summary>
internal static unsafe class JoltRuntime
{
    private static readonly object Gate = new();
    private static int s_state; // 0: not tried, 1: available, -1: unavailable
    private static string? s_reason;

    /// <summary>Whether the loaded natives have asserts (only Debug natives do; see <see cref="JoltApiFacts.ShippedNativesHaveAsserts"/>).</summary>
    public static bool NativesHaveAsserts { get; private set; }

    /// <summary>Initialises Jolt for the process on the first call; false (with the reason) where it cannot run.</summary>
    public static bool TryInitialize(out string? reason)
    {
        lock (Gate)
        {
            if (s_state == 0)
                s_state = Initialize(out s_reason) ? 1 : -1;
            reason = s_reason;
            return s_state == 1;
        }
    }

    private static bool Initialize(out string? reason)
    {
        if (!NativeLibrary.TryLoad("jolt", typeof(Jolt).Assembly, null, out _))
        {
            reason = $"Ahwoo.BRUTAL.Jolt {JoltApiFacts.PackageVersion} has no jolt native library for {RuntimeInformation.RuntimeIdentifier}";
            return false;
        }

        try
        {
            if (!Jolt.VerifyJoltVersionID())
            {
                reason = "the jolt native library does not match Brutal.Jolt's Jolt version";
                return false;
            }

            Jolt.RegisterDefaultAllocator();
            Jolt.SetTraceHandler(&OnTrace); // before the first update (Jolt's default trace hook asserts)
            NativesHaveAsserts = Jolt.SetAssertHandler(&OnAssert);
            Factory.Instance = Factory.Create();
            Jolt.RegisterTypes();
            InitializeShared();
            reason = null;
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or JoltException)
        {
            reason = e.Message;
            return false;
        }
    }

    // Task 2 fills this with the shared broad-phase layer table and filters.
    private static void InitializeShared()
    {
    }

    /// <summary>Jolt's traces (any thread): <c>[Jolt] …</c> warnings, formatted only when warnings are enabled.</summary>
    [UnmanagedCallersOnly]
    internal static void OnTrace(byte* message)
    {
        try
        {
            if (Log.IsEnabled(Log.Level.Warning))
                Log.Warning("[Jolt] " + Marshal.PtrToStringUTF8((nint)message));
        }
        catch (Exception)
        {
            // Called from Jolt's worker threads: nothing may cross back into native code.
        }
    }

    /// <summary>A failed Jolt assert (Debug natives only): logged as an error; never breaks into the debugger.</summary>
    [UnmanagedCallersOnly]
    internal static Bool8 OnAssert(byte* expression, byte* message, byte* file, uint line)
    {
        try
        {
            if (Log.IsEnabled(Log.Level.Error))
            {
                var text = message is null ? "" : " " + Marshal.PtrToStringUTF8((nint)message);
                Log.Error($"[Jolt] assert {Marshal.PtrToStringUTF8((nint)expression)}{text} at {Marshal.PtrToStringUTF8((nint)file)}:{line}");
            }
        }
        catch (Exception)
        {
            // As OnTrace.
        }

        return false;
    }

    /// <summary>The Jitter2 2.9 rule (its <c>ContactData.Init</c>): the larger of the two frictions.</summary>
    [UnmanagedCallersOnly]
    internal static float CombineFriction(Body body1, uint* subShape1, Body body2, uint* subShape2) =>
        MathF.Max(body1.GetFriction(), body2.GetFriction());

    /// <summary>The Jitter2 2.9 rule: the larger of the two restitutions (Jolt's default too, set explicitly).</summary>
    [UnmanagedCallersOnly]
    internal static float CombineRestitution(Body body1, uint* subShape1, Body body2, uint* subShape2) =>
        MathF.Max(body1.GetRestitution(), body2.GetRestitution());
}
```

- [ ] **Step 4: Write the test world `RawJoltWorld.cs`**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// A bare Jolt world (two object layers: 0 static, 1 moving, tables as in Brutal.Jolt's README) for the facts tests:
/// what the binding does, without the engine's backend in between.
/// </summary>
internal sealed unsafe class RawJoltWorld : IDisposable
{
    public const ushort StaticLayer = 0;
    public const ushort MovingLayer = 1;
    public const float Dt = 1f / 60f;

    private readonly BroadPhaseLayerInterfaceTable _broadPhase;
    private readonly ObjectLayerPairFilterTable _pairs;
    private readonly ObjectVsBroadPhaseLayerFilterTable _objectVsBroadPhase;
    private readonly List<Shape> _shapes = [];
    private readonly List<uint> _ids = [];

    public RawJoltWorld(uint maxBodies = 1024)
    {
        SkipUnlessAvailable();
        _broadPhase = BroadPhaseLayerInterfaceTable.Create(2, 2);
        _broadPhase.MapObjectToBroadPhaseLayer(StaticLayer, 0);
        _broadPhase.MapObjectToBroadPhaseLayer(MovingLayer, 1);
        _pairs = ObjectLayerPairFilterTable.Create(2);
        _pairs.EnableCollision(StaticLayer, MovingLayer);
        _pairs.EnableCollision(MovingLayer, MovingLayer);
        _objectVsBroadPhase = ObjectVsBroadPhaseLayerFilterTable.Create(_broadPhase, 2, _pairs, 2);
        System = PhysicsSystem.Create();
        System.Init(maxBodies, 0, 4096, 4096, _broadPhase, _objectVsBroadPhase, _pairs);
        Bodies = System.GetBodyInterfaceNoLock();
        Query = System.GetNarrowPhaseQueryNoLock();
        Temp = TempAllocatorImplWithMallocFallback.Create(8u << 20);
        Jobs = JobSystemSingleThreaded.Create(JoltApiFacts.MaxJobs);
    }

    public PhysicsSystem System { get; }
    public BodyInterface Bodies { get; }
    public NarrowPhaseQuery Query { get; }
    public TempAllocatorImplWithMallocFallback Temp { get; }
    public JobSystemSingleThreaded Jobs { get; }

    /// <summary>Skips the calling test where Jolt cannot run (osx-x64, or <c>MF_TEST_NO_JOLT=1</c>).</summary>
    public static void SkipUnlessAvailable()
    {
        if (Environment.GetEnvironmentVariable("MF_TEST_NO_JOLT") == "1")
            Assert.Skip("Jolt is disabled for this run (MF_TEST_NO_JOLT=1).");
        if (!JoltRuntime.TryInitialize(out var reason))
            Assert.Skip($"Jolt is not available on {RuntimeInformation.RuntimeIdentifier}: {reason}");
    }

    public Shape Keep(Shape shape)
    {
        _shapes.Add(shape);
        return shape;
    }

    public Shape Box(Vector3 halfExtent)
    {
        using var settings = BoxShapeSettings.Create(halfExtent.ToJolt(), 0.05f, default);
        return Keep(settings.CreateShape());
    }

    public Shape Sphere(float radius)
    {
        using var settings = SphereShapeSettings.Create(radius, default);
        return Keep(settings.CreateShape());
    }

    public uint Add(Shape shape, Vector3 position, Quaternion rotation, MotionType motion, ushort layer, Action<BodyCreationSettings>? configure = null) =>
        AddBody(shape, position, rotation, motion, layer, configure).GetID();

    /// <summary>Creates and adds a body (tracked: <see cref="Dispose"/> removes and destroys it) and returns its handle.</summary>
    public Body AddBody(Shape shape, Vector3 position, Quaternion rotation, MotionType motion, ushort layer, Action<BodyCreationSettings>? configure = null)
    {
        using var settings = BodyCreationSettings.Create(shape, position.ToJolt(), rotation.ToJolt(), motion, layer);
        configure?.Invoke(settings);
        var body = Bodies.CreateBody(settings);
        Assert.False(body.IsNull());
        Bodies.AddBody(body.GetID(), motion == MotionType.Static ? Activation.DontActivate : Activation.Activate);
        _ids.Add(body.GetID());
        return body;
    }

    /// <summary>Removes and destroys a tracked body now.</summary>
    public void Destroy(uint id)
    {
        Assert.True(_ids.Remove(id));
        Bodies.RemoveBody(id);
        Bodies.DestroyBody(id);
    }

    public uint AddFloor() => Add(Box(new Vector3(20, 0.5f, 20)), new Vector3(0, -0.5f, 0), Quaternion.Identity, MotionType.Static, StaticLayer);

    public void Step(int steps = 1) => Step(steps, Jobs);

    public void Step(int steps, JobSystem jobs)
    {
        for (var i = 0; i < steps; i++)
            Assert.Equal(PhysicsUpdateError.None, System.Update(Dt, 1, Temp, jobs));
    }

    public Vector3 Position(uint body) => Bodies.GetPosition(body).FromJolt();

    public void Dispose()
    {
        if (_ids.Count > 0)
        {
            Bodies.RemoveBodies(CollectionsMarshal.AsSpan(_ids));
            Bodies.DestroyBodies(CollectionsMarshal.AsSpan(_ids));
        }

        System.Dispose();
        _objectVsBroadPhase.Dispose();
        _pairs.Dispose();
        _broadPhase.Dispose();
        Jobs.Dispose();
        Temp.Dispose();
        foreach (var shape in _shapes)
            shape.Dispose();
    }
}
```

`ToJolt`/`FromJolt` are `JoltMath`'s (Task 2). In this task add a temporary private copy at the bottom of `RawJoltWorld.cs`
so the file compiles, and delete it in Task 2 Step 5:

```csharp
internal static class RawJoltMath
{
    public static float3 ToJolt(this Vector3 v) => System.Runtime.CompilerServices.Unsafe.BitCast<Vector3, float3>(v);
    public static floatQuat ToJolt(this Quaternion q) => System.Runtime.CompilerServices.Unsafe.BitCast<Quaternion, floatQuat>(q);
    public static Vector3 FromJolt(this float3 v) => System.Runtime.CompilerServices.Unsafe.BitCast<float3, Vector3>(v);
}
```

- [ ] **Step 5: Write the facts tests**

```csharp
using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// The B3 spike (ADR 0192): what <c>Ahwoo.BRUTAL.Jolt</c> 2026.10.2 does, pinned so the backend's assumptions break
/// loudly on an upgrade. Each test names the <see cref="JoltApiFacts"/> member or backend rule it pins.
/// </summary>
public sealed unsafe class JoltApiFactsTests
{
    [Fact]
    public void NumericsTypesShareSystemNumericsLayouts()
    {
        RawJoltWorld.SkipUnlessAvailable();
        Assert.Equal(Unsafe.SizeOf<Vector3>(), Unsafe.SizeOf<float3>());
        Assert.Equal(Unsafe.SizeOf<Quaternion>(), Unsafe.SizeOf<floatQuat>());
        Assert.Equal(Unsafe.SizeOf<Matrix4x4>(), Unsafe.SizeOf<float4x4>());
        Assert.Equal(new Vector3(1, 2, 3), Unsafe.BitCast<float3, Vector3>(new float3(1, 2, 3)));
        Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), Unsafe.BitCast<floatQuat, Quaternion>(new floatQuat(0.1f, 0.2f, 0.3f, 0.9f)));
        Assert.Equal(Matrix4x4.CreateTranslation(4, 5, 6), Unsafe.BitCast<float4x4, Matrix4x4>(float4x4.CreateTranslation(new float3(4, 5, 6))));
        Assert.Equal(44, Unsafe.SizeOf<Triangle>()); // float3_3 (36 B) + material index + user data
        Assert.Equal(64, Unsafe.SizeOf<ContactEvent>());
    }

    [Fact]
    public void WorldTransformsComeBackInSystemNumericsLayout()
    {
        using var w = new RawJoltWorld();
        var rotation = Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f);
        var id = w.Add(w.Box(Vector3.One), new Vector3(1, 2, 3), rotation, MotionType.Static, RawJoltWorld.StaticLayer);
        var actual = Unsafe.BitCast<float4x4, Matrix4x4>(w.Bodies.GetWorldTransform(id));
        var expected = Matrix4x4.CreateFromQuaternion(rotation);
        expected.Translation = new Vector3(1, 2, 3);
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                Assert.Equal(expected[r, c], actual[r, c], 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void AThreadPoolWithNWorkersSteps(int workers)
    {
        using var w = new RawJoltWorld();
        using var pool = JobSystemThreadPool.Create(JoltApiFacts.MaxJobs, JoltApiFacts.MaxBarriers, workers);
        var box = w.Add(w.Box(new Vector3(0.5f)), new Vector3(0, 5, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer);
        w.Step(30, pool);
        Assert.True(w.Position(box).Y < 4.5f);
    }

    [Fact]
    public void TheSingleThreadedJobSystemSteps()
    {
        using var w = new RawJoltWorld();
        var box = w.Add(w.Box(new Vector3(0.5f)), new Vector3(0, 5, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer);
        w.Step(30);
        Assert.True(w.Position(box).Y < 4.5f);
    }

    [Fact]
    public void BodyIdsKeepTheIndexInTheLow23BitsAndChangeOnReuse()
    {
        using var w = new RawJoltWorld();
        var shape = w.Box(Vector3.One);
        var a = w.Add(shape, Vector3.Zero, Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer);
        var b = w.Add(shape, new Vector3(5, 0, 0), Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer);
        Assert.Equal(0u, a & JoltApiFacts.BodyIdIndexMask);
        Assert.Equal(1u, b & JoltApiFacts.BodyIdIndexMask);
        w.Destroy(a);
        var c = w.Add(shape, Vector3.Zero, Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer);
        Assert.True((c & JoltApiFacts.BodyIdIndexMask) < 1024);
        if ((c & JoltApiFacts.BodyIdIndexMask) == (a & JoltApiFacts.BodyIdIndexMask))
            Assert.NotEqual(a, c); // same slot, new sequence number
    }

    [Fact]
    public void ARunOutOfBodiesReturnsANullBody()
    {
        using var w = new RawJoltWorld(maxBodies: 2);
        var shape = w.Box(Vector3.One);
        using var settings = BodyCreationSettings.Create(shape, new float3(0), floatQuat.Identity, MotionType.Static, RawJoltWorld.StaticLayer);
        var first = w.Bodies.CreateBody(settings);
        var second = w.Bodies.CreateBody(settings);
        Assert.False(first.IsNull());
        Assert.False(second.IsNull());
        Assert.True(w.Bodies.CreateBody(settings).IsNull());
        w.Bodies.DestroyBody(first.GetID()); // created, never added
        w.Bodies.DestroyBody(second.GetID());
    }

    private static List<ContactEvent> Drain(ContactEventListener listener)
    {
        var buffer = new ContactEvent[256];
        var all = new List<ContactEvent>();
        uint remaining;
        do
        {
            var n = listener.Drain(buffer, out remaining);
            all.AddRange(buffer.AsSpan(0, (int)n).ToArray());
        }
        while (remaining > 0);
        return all;
    }

    [Fact]
    public void ContactEventsArePairedAddedAndRemovedPerSubShapePair()
    {
        using var w = new RawJoltWorld();
        using var listener = new ContactEventListener(256);
        w.System.SetContactListener(listener);
        var floor = w.AddFloor();
        var box = w.Add(w.Box(new Vector3(0.5f)), new Vector3(0, 0.6f, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer);
        ContactEvent? first = null;
        for (var i = 0; i < 30 && first is null; i++)
        {
            w.Step();
            foreach (var e in Drain(listener))
            {
                if (e.Kind == ContactEventKind.Added)
                {
                    first = e;
                    break;
                }
            }
        }

        Assert.NotNull(first);
        var added = first.Value;
        Assert.Equal(new[] { floor, box }.Order(), new[] { added.Body1, added.Body2 }.Order());
        w.Bodies.SetPositionAndRotation(box, new float3(0, 10, 0), floatQuat.Identity, Activation.Activate);
        w.Step();
        var removed = Assert.Single(Drain(listener), e => e.Kind == ContactEventKind.Removed);
        Assert.Equal((added.Body1, added.Body2, added.SubShape1, added.SubShape2), (removed.Body1, removed.Body2, removed.SubShape1, removed.SubShape2));
        Assert.Equal(0u, listener.Dropped);
        w.System.SetContactListener(default);
    }

    [Fact]
    public void DestroyingATouchingBodyReportsRemovedInTheNextUpdate()
    {
        using var w = new RawJoltWorld();
        using var listener = new ContactEventListener(256);
        w.System.SetContactListener(listener);
        w.AddFloor();
        var box = w.Add(w.Box(new Vector3(0.5f)), new Vector3(0, 0.5f, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer);
        w.Step(2);
        Assert.Contains(Drain(listener), e => e.Kind == ContactEventKind.Added);
        w.Destroy(box);
        w.Step();
        var removed = Assert.Single(Drain(listener), e => e.Kind == ContactEventKind.Removed);
        Assert.True(removed.Body1 == box || removed.Body2 == box);
        Assert.Equal(0ul, removed.UserData1); // Removed events carry ids only
        w.System.SetContactListener(default);
    }

    [Fact]
    public void ContactsStartWithinTheSpeculativeDistanceAndPersistWhileAsleep()
    {
        using var w = new RawJoltWorld();
        using var listener = new ContactEventListener(256);
        w.System.SetContactListener(listener);
        w.AddFloor();
        void Floating(BodyCreationSettings s) => s.SetGravityFactor(0);
        var near = w.Add(w.Box(new Vector3(0.5f)), new Vector3(0, 0.51f, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer, Floating);
        var far = w.Add(w.Box(new Vector3(0.5f)), new Vector3(5, 0.53f, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer, Floating);
        w.Step();
        var events = Drain(listener);
        Assert.Contains(events, e => e.Kind == ContactEventKind.Added && (e.Body1 == near || e.Body2 == near));
        Assert.DoesNotContain(events, e => e.Body1 == far || e.Body2 == far);
        Assert.Equal(0.02f, w.System.GetPhysicsSettings().SpeculativeContactDistance, 6);

        w.Step(180); // both fall asleep (Jolt default: 0.5 s below the sleep threshold)
        Assert.False(w.Bodies.IsActive(near));
        Assert.DoesNotContain(Drain(listener), e => e.Kind == ContactEventKind.Removed && (e.Body1 == near || e.Body2 == near));
        w.System.SetContactListener(default);
    }

    [Fact]
    public void KinematicSensorsSeeStaticAndKinematicBodiesStaticSensorsDoNot()
    {
        using var w = new RawJoltWorld();
        using var listener = new ContactEventListener(256);
        w.System.SetContactListener(listener);
        var wall = w.Add(w.Box(new Vector3(0.5f)), new Vector3(1, 0, 0), Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer);
        var platform = w.Add(w.Box(new Vector3(0.5f)), new Vector3(-1, 0, 0), Quaternion.Identity, MotionType.Kinematic, RawJoltWorld.MovingLayer);
        var sensor = w.Add(w.Box(new Vector3(2)), Vector3.Zero, Quaternion.Identity, MotionType.Kinematic, RawJoltWorld.MovingLayer, s =>
        {
            s.SetIsSensor(true);
            s.SetCollideKinematicVsNonDynamic(true);
            s.SetAllowSleeping(false);
        });
        var staticSensor = w.Add(w.Box(new Vector3(2)), new Vector3(1, 0, 0), Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer, s => s.SetIsSensor(true));
        w.Step();
        var added = Drain(listener).Where(e => e.Kind == ContactEventKind.Added).Select(e => (Math.Min(e.Body1, e.Body2), Math.Max(e.Body1, e.Body2))).ToList();
        Assert.Contains((Math.Min(sensor, wall), Math.Max(sensor, wall)), added);
        Assert.Contains((Math.Min(sensor, platform), Math.Max(sensor, platform)), added);
        Assert.DoesNotContain(added, p => p.Item1 == staticSensor || p.Item2 == staticSensor);

        // Sensors overlap exactly: no speculative distance.
        var hover = w.Add(w.Sphere(0.25f), new Vector3(0, 2.26f, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer, s => s.SetGravityFactor(0));
        w.Step();
        Assert.DoesNotContain(Drain(listener), e => e.Body1 == hover || e.Body2 == hover);
        w.System.SetContactListener(default);
    }

    [Fact]
    public void ConvexHullErrorsSurfaceAsJoltException()
    {
        RawJoltWorld.SkipUnlessAvailable();
        float3[] coplanar = [new(0, 0, 0), new(1, 0, 0), new(0, 0, 1), new(1, 0, 1)];
        using var settings = ConvexHullShapeSettings.Create(coplanar, 0.05f, default);
        var e = Assert.Throws<JoltException>(() => settings.CreateShape());
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 2)]
    [InlineData(5, 0)]
    [InlineData(6, 2)]
    [InlineData(9, 3)]
    [InlineData(65, 5)]
    [InlineData(129, 3)]
    [InlineData(257, 0)]
    public void HeightFieldSampleCountsFollowTheBlockSizeRule(int samples, int block)
    {
        RawJoltWorld.SkipUnlessAvailable();
        Assert.Equal(block, JoltApiFacts.HeightFieldBlockSize(samples));
        var heights = new float[samples * samples];
        fixed (float* p = heights)
        {
            using var settings = HeightFieldShapeSettings.Create(new Brutal.Pointers.Ptr<float>(p), new float3(0), new float3(1), (uint)samples, default, default);
            settings.SetBlockSize((uint)(block == 0 ? 2 : block));
            if (block == 0 && samples % 2 != 0)
            {
                Assert.Throws<JoltException>(() => settings.CreateShape()); // not a multiple of the block size
            }
            else if (block != 0)
            {
                using var shape = settings.CreateShape();
                Assert.False(shape.IsNull());
            }
        }
    }

    [Fact]
    public void HeightFieldCellsSplitAlongTheDiagonalJoltApiFactsRecords()
    {
        using var w = new RawJoltWorld();
        // Cell (0,0) has height 1 at (1,0) and (0,1), 0 at (0,0) and (1,1): its centre is 1 on the engine's diagonal
        // ((x+1, z)–(x, z+1)) and 0 on the other one.
        var heights = new float[16];
        heights[0 * 4 + 1] = 1;
        heights[1 * 4 + 0] = 1;
        Shape shape;
        fixed (float* p = heights)
        {
            using var settings = HeightFieldShapeSettings.Create(new Brutal.Pointers.Ptr<float>(p), new float3(0), new float3(1), 4, default, default);
            settings.SetBlockSize(2);
            shape = w.Keep(settings.CreateShape());
        }

        w.Add(shape, Vector3.Zero, Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer);
        using var ray = RayCastSettings.Create();
        var cast = new RRayCast { Origin = new float3(0.5f, 5, 0.5f), Direction = new float3(0, -10, 0) };
        Assert.True(w.Query.CastRayClosest(in cast, ray, out var hit));
        var y = 5 - 10 * hit.Fraction;
        Assert.True(MathF.Abs(y - 1) < 1e-3f || MathF.Abs(y) < 1e-3f, $"centre height {y}");
        var engineDiagonal = MathF.Abs(y - 1) < 1e-3f;
        Assert.True(engineDiagonal == JoltApiFacts.HeightFieldDiagonalMatchesEngine, $"Jolt's cell diagonal matches the engine's: {engineDiagonal}");
    }

    [Fact]
    public void LinearDampingRemovesCTimesDtPerStep()
    {
        using var w = new RawJoltWorld();
        var body = w.Add(w.Sphere(0.5f), new Vector3(0, 10, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer, s =>
        {
            s.SetGravityFactor(0);
            s.SetLinearDamping(3f);
            s.SetLinearVelocity(new float3(10, 0, 0));
        });
        w.Step();
        Assert.Equal(10f * (1f - 3f * RawJoltWorld.Dt), w.Bodies.GetLinearVelocity(body).X, 4);
    }

    [Fact]
    public void AllowedDofsChangeAtRunTimeThroughSetMassProperties()
    {
        using var w = new RawJoltWorld();
        var shape = w.Box(new Vector3(0.5f));
        var body = w.AddBody(shape, new Vector3(0, 10, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer, s => s.SetGravityFactor(0));
        using (var mass = shape.GetMassProperties())
            body.GetMotionProperties().SetMassProperties(AllowedDOFs.All & ~AllowedDOFs.TranslationX, mass);
        w.Bodies.AddImpulse(body.GetID(), new float3(5000, 5000, 0)); // the 1 m box weighs 1000 kg (Jolt's default density)
        w.Step();
        var v = w.Bodies.GetLinearVelocity(body.GetID());
        Assert.Equal(0f, v.X, 5);
        Assert.True(v.Y > 1f, $"{v.Y}");
        Assert.Equal((int)AxisLock.LinearX | (int)AxisLock.AngularZ, (int)(AllowedDOFs.TranslationX | AllowedDOFs.RotationZ)); // the same bits
    }

    [Fact]
    public void CustomCombineFunctionsReplaceTheGeometricMean()
    {
        float Slide(bool max)
        {
            using var w = new RawJoltWorld();
            if (max)
            {
                w.System.SetCombineFriction(&JoltRuntime.CombineFriction);
                w.System.SetCombineRestitution(&JoltRuntime.CombineRestitution);
            }

            w.Add(w.Box(new Vector3(20, 0.5f, 20)), new Vector3(0, -0.5f, 0), Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer, s => s.SetFriction(1f));
            var box = w.Add(w.Box(new Vector3(0.5f)), new Vector3(0, 0.5f, 0), Quaternion.Identity, MotionType.Dynamic, RawJoltWorld.MovingLayer, s =>
            {
                s.SetFriction(0f);
                s.SetLinearVelocity(new float3(4, 0, 0));
            });
            w.Step(30);
            return w.Bodies.GetLinearVelocity(box).X;
        }

        Assert.True(Slide(max: false) > 3.5f, "geometric mean of 0 and 1: no friction");
        Assert.True(Slide(max: true) < 0.5f, "maximum: friction 1");
    }

    [Fact]
    public void TheShippedNativesHaveNoAsserts()
    {
        RawJoltWorld.SkipUnlessAvailable();
        Assert.True(JoltRuntime.NativesHaveAsserts == JoltApiFacts.ShippedNativesHaveAsserts, $"the natives have asserts: {JoltRuntime.NativesHaveAsserts}");
    }

    [Fact]
    public void RaysFromInsideAConvexShapeHitAtTheOriginOrSkipIt()
    {
        using var w = new RawJoltWorld();
        w.AddFloor();
        var box = w.Add(w.Box(Vector3.One), new Vector3(0, 2, 0), Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer);
        var cast = new RRayCast { Origin = new float3(0, 2, 0), Direction = new float3(0, -10, 0) };
        using var solid = RayCastSettings.Create();
        solid.SetTreatConvexAsSolid(true);
        Assert.True(w.Query.CastRayClosest(in cast, solid, out var hit));
        Assert.Equal((box, 0f), (hit.BodyID, hit.Fraction));
        using var hollow = RayCastSettings.Create();
        hollow.SetTreatConvexAsSolid(false);
        hollow.SetBackFaceModeConvex(BackFaceMode.IgnoreBackFaces);
        Assert.True(w.Query.CastRayClosest(in cast, hollow, out hit));
        Assert.NotEqual(box, hit.BodyID); // the floor behind it
        Assert.Equal(0.2f, hit.Fraction, 4);
    }

    [Fact]
    public void ShapeCastsAndCollisionsReportAxesFromTheQueryShapeTowardsTheOther()
    {
        using var w = new RawJoltWorld();
        var floor = w.AddFloor();
        var sphere = w.Sphere(0.5f);
        var start = float4x4.CreateTranslation(new float3(0, 5, 0));
        var cast = new RShapeCast
        {
            Shape = sphere, Scale = new float3(1), CenterOfMassStart = start, Direction = new float3(0, -10, 0),
            ShapeWorldBounds = sphere.GetWorldSpaceBounds(in start, new float3(1)),
        };
        using var castSettings = ShapeCastSettings.Create();
        Assert.True(w.Query.CastShapeClosest(in cast, castSettings, default, out var castHit));
        Assert.Equal(floor, castHit.BodyID2);
        Assert.Equal(0.45f, castHit.Fraction, 3);
        Assert.True(Vector3.Dot(Vector3.Normalize(castHit.PenetrationAxis.FromJolt()), -Vector3.UnitY) > 0.999f);
        Assert.Equal(0f, castHit.ContactPointOn2.Y, 3);

        using var collide = CollideShapeSettings.Create();
        collide.SetMaxSeparationDistance(0.1f);
        var hover = float4x4.CreateTranslation(new float3(0, 0.55f, 0));
        var results = new CollideShapeResult[4];
        var n = w.Query.CollideShape(sphere, new float3(1), in hover, collide, default, results, out _);
        Assert.Equal(1u, n);
        Assert.Equal(-0.05f, results[0].PenetrationDepth, 3); // negative: separated
        Assert.True(Vector3.Dot(Vector3.Normalize(results[0].PenetrationAxis.FromJolt()), -Vector3.UnitY) > 0.999f);
    }

    [Fact]
    public void MeshTrianglesWoundLikeTheEngineFaceUpAndCullBackFacesLikeJitter2()
    {
        using var w = new RawJoltWorld();
        // HeightMapShape3D's winding (p00, p01, p10): counter-clockwise seen from above. Jitter2's TriangleShape rays use
        // CullMode.BackFacing (its IL), so a ray from below does not hit; Jolt ignores back faces by default too.
        var triangles = new Triangle[1];
        triangles[0].V[0] = new float3(0, 0, 0);
        triangles[0].V[1] = new float3(0, 0, 1);
        triangles[0].V[2] = new float3(1, 0, 0);
        using var settings = MeshShapeSettings.Create(triangles, default);
        w.Add(w.Keep(settings.CreateShape()), Vector3.Zero, Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer);
        using var ray = RayCastSettings.Create();
        var down = new RRayCast { Origin = new float3(0.2f, 1, 0.2f), Direction = new float3(0, -2, 0) };
        var up = new RRayCast { Origin = new float3(0.2f, -1, 0.2f), Direction = new float3(0, 2, 0) };
        Assert.True(w.Query.CastRayClosest(in down, ray, out _));
        Assert.False(w.Query.CastRayClosest(in up, ray, out _));
    }

    [Fact]
    public void MoveKinematicReachesItsTargetInOneStep()
    {
        using var w = new RawJoltWorld();
        var body = w.Add(w.Box(new Vector3(0.5f)), Vector3.Zero, Quaternion.Identity, MotionType.Kinematic, RawJoltWorld.MovingLayer);
        var target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        w.Bodies.MoveKinematic(body, new float3(1, 0, 0), target.ToJolt(), RawJoltWorld.Dt);
        w.Step();
        Assert.Equal(1f, w.Position(body).X, 4);
    }
}
```

- [ ] **Step 6: Update the source rule** — in `BackendSourceRulesTests.Rules` (B2 Task 10) change `{ "Brutal.JoltApi", "MainframeEngine/Src/Physics/3D/Jolt/", false }` to `{ "Brutal.JoltApi", "MainframeEngine/Src/Physics/3D/Jolt/", true }`.

- [ ] **Step 7: Run the facts**

Run: `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltApiFactsTests|FullyQualifiedName~BackendSourceRulesTests"`
Expected on osx-arm64/linux-x64/win-x64: all pass. On osx-x64: the facts are skipped with `Jolt is not available on osx-x64: …`.
Each failure is a fact that differs from the table above; resolve it here, before any backend code depends on it:
- `HeightFieldCellsSplitAlongTheDiagonalJoltApiFactsRecords` fails with the centre at 1 → set `HeightFieldDiagonalMatchesEngine = true` (the builder's identity branch in Task 3 is then used; both branches are written and tested there).
- `TheShippedNativesHaveNoAsserts` fails → set `ShippedNativesHaveAsserts = true` (nothing else changes: the assert handler logs and continues).
- `HeightFieldSampleCountsFollowTheBlockSizeRule` fails for a row → Jolt's rule differs from "a multiple of the block size, at least two blocks": change `HeightFieldBlockSize` to the rule the failures show (the builder also falls back to a mesh whenever `CreateShape` throws, Task 3).
- `ContactsStartWithinTheSpeculativeDistanceAndPersistWhileAsleep`'s sleep assertion fails (contacts end on sleep) → Task 7's tracker keeps a sleeping body's set: add `if (!_bi.IsActive(a.Id) && !_bi.IsActive(b.Id)) return;` before the decrement in `JoltContactTracker.Apply` and note it in `JoltApiFacts`'s remarks.
- Any other failure: stop and report it to the user with the test output (the spec's assumptions do not hold).

- [ ] **Step 8: Physics suite and trace gate**

```bash
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics"
MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"
```
Expected: all pass; the trace 1 passed.

- [ ] **Step 9: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Jolt/JoltApiFacts.cs MainframeEngine/Src/Physics/3D/Jolt/JoltRuntime.cs Tests/MainframeEngine.Tests/Physics/Jolt/ Tests/MainframeEngine.Tests/Physics/BackendSourceRulesTests.cs
git commit -F- <<'EOF'
Pin the Ahwoo.BRUTAL.Jolt 2026.10.2 behaviours the Jolt backend relies on (B3 spike)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 2: Runtime sharing, math and layers

**Files:**
- Create: `MainframeEngine/Src/Physics/3D/Jolt/JoltMath.cs`, `MainframeEngine/Src/Physics/3D/Jolt/JoltLayers.cs`
- Modify: `MainframeEngine/Src/Physics/3D/Jolt/JoltRuntime.cs` (`InitializeShared` and the shared members)
- Modify: `Tests/MainframeEngine.Tests/Physics/Jolt/RawJoltWorld.cs` (delete `RawJoltMath`), `Tests/MainframeEngine.Tests/Physics/BackendSourceRulesTests.cs` (a `Brutal.Numerics` row)
- Create: `Tests/MainframeEngine.Tests/Physics/Jolt/JoltMathTests.cs`, `JoltLayerTableTests.cs`, `JoltRuntimeTests.cs`

**Interfaces:**
- Consumes: `PhysicsSpace3D.Collides(uint, uint, bool, uint, uint, bool)` (B2 Task 6), `ManagedObjectLayerPairFilter.ShouldCollide(ushort, ushort)`, `ManagedObjectVsBroadPhaseLayerFilter.ShouldCollide(ushort, byte)`, `ManagedBroadPhaseLayerFilter.ShouldCollide(byte)`, `BroadPhaseLayerInterfaceTable`.
- Produces: `internal static class JoltMath` (`ToJolt(this Vector3)`, `ToJolt(this Quaternion)`, `FromJolt(this float3)`, `FromJolt(this floatQuat)`, `Transform(Vector3, Quaternion) → float4x4`, `CenterOfMassTransform(Vector3, Quaternion, Vector3) → float4x4`, `AsJolt(ReadOnlySpan<Vector3>) → ReadOnlySpan<float3>`); `internal sealed class JoltLayerTable` (consts `Static`, `Moving`, `Area`, `BroadPhaseCount`, `ObjectLayerCount`, `MaxPairs`; `ObjectLayer(byte, int)`, `BroadPhaseOf(ushort)`, `Acquire(uint, uint) → int`, `Release(int)`, `Count`, `Collides(ushort, ushort)`, `InQueryMask(ushort, uint)`); `JoltObjectLayerPairFilter`, `JoltObjectVsBroadPhaseFilter`, `JoltNoAreasFilter`; `JoltRuntime.BroadPhaseLayers`, `.ObjectVsBroadPhase`, `.NoAreas`, `.TakeSharedCallbackException()`.

Deviation from the spec, recorded here and in the physics doc: the pair table is a fixed array written in place between
steps instead of a copy-on-write array (Jolt's filters only run inside `Update` or inside a query on the main thread, and
the job system's barriers order the writes, so no copy and no allocation is needed); and the 65 536-entry
broad-phase table plus the stateless object-vs-broad-phase and "no areas" filters are created once per process in
`JoltRuntime`, not per space (they are immutable and identical for every space; spaces created by the hundred in tests no
longer pay 65 536 native calls each).

- [ ] **Step 1: Write the failing tests**

`JoltMathTests.cs`:

```csharp
using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal.Numerics;

namespace MainframeEngine.Tests.Physics;

public sealed class JoltMathTests
{
    [Fact]
    public void ConversionsRoundTripBitForBit()
    {
        var v = new Vector3(1.5f, -2.25f, 3.125f);
        var q = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f));
        Assert.Equal(v, v.ToJolt().FromJolt());
        Assert.Equal(q, q.ToJolt().FromJolt());
        Assert.Equal(new float3(1.5f, -2.25f, 3.125f), v.ToJolt());
    }

    [Fact]
    public void CenterOfMassTransformsPutTheRotatedOffsetIntoTheTranslation()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var m = Unsafe.BitCast<float4x4, Matrix4x4>(JoltMath.CenterOfMassTransform(new Vector3(1, 2, 3), rotation, new Vector3(1, 0, 0)));
        var expected = new Vector3(1, 2, 3) + Vector3.Transform(Vector3.UnitX, rotation);
        Assert.Equal(expected.X, m.Translation.X, 5);
        Assert.Equal(expected.Y, m.Translation.Y, 5);
        Assert.Equal(expected.Z, m.Translation.Z, 5);
        Assert.Equal(Vector3.Transform(Vector3.UnitZ, rotation).X, Vector3.TransformNormal(Vector3.UnitZ, m).X, 5);
    }

    [Fact]
    public void PointSpansAreReinterpretedNotCopied()
    {
        Vector3[] points = [Vector3.UnitX, Vector3.UnitY];
        var span = JoltMath.AsJolt(points);
        Assert.Equal(2, span.Length);
        Assert.True(Unsafe.AreSame(ref Unsafe.As<Vector3, float3>(ref points[0]), ref Unsafe.AsRef(in span[0])));
    }
}
```

`JoltLayerTableTests.cs`:

```csharp
namespace MainframeEngine.Tests.Physics;

/// <summary>Interned (layer, mask) object layers (B3): pure managed, no native code.</summary>
public sealed class JoltLayerTableTests
{
    [Fact]
    public void ObjectLayersPutTheBroadPhaseInTheTopTwoBits()
    {
        Assert.Equal((ushort)((2 << 14) | 5), JoltLayerTable.ObjectLayer(JoltLayerTable.Area, 5));
        Assert.Equal(JoltLayerTable.Area, JoltLayerTable.BroadPhaseOf((ushort)((2 << 14) | 5)));
        Assert.Equal(JoltLayerTable.Static, JoltLayerTable.BroadPhaseOf(JoltLayerTable.ObjectLayer(JoltLayerTable.Static, 16383)));
        Assert.Equal(16383, JoltLayerTable.MaxPairs);
        Assert.Equal(65536u, JoltLayerTable.ObjectLayerCount);
    }

    [Fact]
    public void EqualPairsShareAnIndexAndTheRuleIsTheEnginesOnTheEncodedLayers()
    {
        var table = new JoltLayerTable();
        var a = table.Acquire(1, 1);
        Assert.Equal(a, table.Acquire(1, 1));
        var b = table.Acquire(2, 0);
        var area = table.Acquire(0, 2);
        Assert.Equal(4, table.Acquire(4, 3)); // indices are handed out from 1
        ushort Moving(int index) => JoltLayerTable.ObjectLayer(JoltLayerTable.Moving, index);
        ushort Area(int index) => JoltLayerTable.ObjectLayer(JoltLayerTable.Area, index);
        Assert.False(table.Collides(Moving(a), Moving(b)));  // neither scans the other
        Assert.True(table.Collides(Area(area), Moving(b)));   // the area's mask has layer 2
        Assert.False(table.Collides(Area(area), Area(area))); // areas never detect areas
        Assert.Equal(PhysicsSpace3D.Collides(1, 1, false, 2, 0, false), table.Collides(Moving(a), JoltLayerTable.ObjectLayer(JoltLayerTable.Static, b)));
        Assert.True(table.InQueryMask(Moving(b), 2));
        Assert.False(table.InQueryMask(Area(area), uint.MaxValue)); // queries never report areas
    }

    [Fact]
    public void TheTableHolds16383PairsReusesFreedOnesAndRefusesMore()
    {
        var table = new JoltLayerTable();
        var indices = new int[JoltLayerTable.MaxPairs];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = table.Acquire((uint)i + 1, 7);
        Assert.Equal(JoltLayerTable.MaxPairs, table.Count);
        Assert.Equal(Enumerable.Range(1, JoltLayerTable.MaxPairs), indices.Order());

        var e = Assert.Throws<InvalidOperationException>(() => table.Acquire(0xFFFF_0000, 7));
        Assert.Contains("16383", e.Message, StringComparison.Ordinal);
        Assert.Contains("jitter2", e.Message, StringComparison.Ordinal);

        table.Release(indices[100]);
        Assert.Equal(indices[100], table.Acquire(0xFFFF_0000, 7)); // the freed index is reused
        Assert.Equal(0xFFFF_0000u, table.LayerOf(indices[100]));
    }

    [Fact]
    public void APairStaysWhileAnyUserHoldsIt()
    {
        var table = new JoltLayerTable();
        var a = table.Acquire(8, 8);
        table.Acquire(8, 8);
        table.Release(a);
        Assert.Equal(1, table.Count);
        table.Release(a);
        Assert.Equal(0, table.Count);
    }
}
```

`JoltRuntimeTests.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace MainframeEngine.Tests.Physics;

public sealed unsafe class JoltRuntimeTests
{
    [Fact]
    public void JoltIsAvailableOnTheShippedRidsAndInitialisesOnce()
    {
        var available = JoltRuntime.TryInitialize(out var reason);
        Assert.Equal(available, JoltRuntime.TryInitialize(out var again));
        Assert.Equal(reason, again);
        var shipped = RuntimeInformation.RuntimeIdentifier is "osx-arm64" or "linux-x64" or "win-x64";
        Assert.Equal(shipped, available);
        if (!available)
            Assert.Contains(RuntimeInformation.RuntimeIdentifier, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSharedBroadPhaseObjectsExistOnceInitialised()
    {
        RawJoltWorld.SkipUnlessAvailable();
        Assert.False(JoltRuntime.BroadPhaseLayers.IsNull());
        Assert.Equal(JoltLayerTable.BroadPhaseCount, JoltRuntime.BroadPhaseLayers.GetNumBroadPhaseLayers());
        Assert.Equal(JoltLayerTable.Area, JoltRuntime.BroadPhaseLayers.GetBroadPhaseLayer(JoltLayerTable.ObjectLayer(JoltLayerTable.Area, 9)));
        Assert.Null(JoltRuntime.TakeSharedCallbackException());
    }

    [Fact]
    public void JoltTracesReachTheLogAsWarnings()
    {
        RawJoltWorld.SkipUnlessAvailable();
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            var text = Encoding.UTF8.GetBytes("TempAllocator: out of memory (test)\0");
            fixed (byte* p = text)
                ((delegate* unmanaged<byte*, void>)&JoltRuntime.OnTrace)(p);
            Assert.Contains(sink.Snapshot(), e => e.Message == "[Jolt] TempAllocator: out of memory (test)");
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }
}
```

- [ ] **Step 2: Run them to see them fail** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltMathTests|FullyQualifiedName~JoltLayerTableTests|FullyQualifiedName~JoltRuntimeTests"` → build errors (`JoltMath`, `JoltLayerTable` missing).

- [ ] **Step 3: Write `JoltMath.cs`**

```csharp
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace MainframeEngine;

/// <summary>
/// <c>System.Numerics</c> ↔ <c>Brutal.Numerics</c> by bit cast (same sizes and field order; a <c>Matrix4x4</c> has the
/// memory of Jolt's column-major <c>Mat44</c>: <see cref="JoltApiFacts"/>). Names differ from <c>JitterMath</c>'s
/// (<c>ToJ</c>/<c>ToNumerics</c>) so both extension sets can be in scope.
/// </summary>
internal static class JoltMath
{
    public static float3 ToJolt(this Vector3 v) => Unsafe.BitCast<Vector3, float3>(v);

    public static floatQuat ToJolt(this Quaternion q) => Unsafe.BitCast<Quaternion, floatQuat>(q);

    public static Vector3 FromJolt(this float3 v) => Unsafe.BitCast<float3, Vector3>(v);

    public static Quaternion FromJolt(this floatQuat q) => Unsafe.BitCast<floatQuat, Quaternion>(q);

    /// <summary>A rigid transform as Jolt's <c>Mat44</c>.</summary>
    public static float4x4 Transform(Vector3 position, Quaternion rotation)
    {
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        m.Translation = position;
        return Unsafe.BitCast<Matrix4x4, float4x4>(m);
    }

    /// <summary>
    /// The centre-of-mass transform Jolt's queries take for a shape whose origin is at <paramref name="position"/>:
    /// the shape's local <paramref name="centerOfMass"/> rotated into place and added to the translation.
    /// </summary>
    public static float4x4 CenterOfMassTransform(Vector3 position, Quaternion rotation, Vector3 centerOfMass) =>
        Transform(position + Vector3.Transform(centerOfMass, rotation), rotation);

    /// <summary>A view of <paramref name="points"/> as Jolt vectors (no copy).</summary>
    public static ReadOnlySpan<float3> AsJolt(ReadOnlySpan<Vector3> points) => MemoryMarshal.Cast<Vector3, float3>(points);
}
```

- [ ] **Step 4: Write `JoltLayers.cs`**

```csharp
using Brutal.JoltApi;

namespace MainframeEngine;

/// <summary>
/// Jolt object layers for one space (B3). Jolt layers are 16-bit, too small for a 32-bit layer and a 32-bit mask, so each
/// distinct <c>(layer, mask)</c> pair in use gets an index (1..16 383) and a body's object layer is
/// <c>(broadPhase &lt;&lt; 14) | index</c>, with three broad-phase layers: <see cref="Static"/>, <see cref="Moving"/>
/// (dynamic, kinematic, character) and <see cref="Area"/> (sensors). Indices are reference counted and reused once free,
/// so the limit is on pairs in use at once. The arrays are written only between steps, on the main thread; Jolt reads
/// them from its worker threads inside <c>Update</c> (whose job barriers order the writes) and from queries on the main
/// thread, so no lock and no copy is needed.
/// </summary>
internal sealed class JoltLayerTable
{
    public const byte Static = 0;
    public const byte Moving = 1;
    public const byte Area = 2;
    public const uint BroadPhaseCount = 3;
    public const uint ObjectLayerCount = 1u << 16;
    public const int MaxPairs = (1 << IndexBits) - 1;
    private const int IndexBits = 14;
    private const int IndexMask = (1 << IndexBits) - 1;

    private readonly uint[] _layers = new uint[MaxPairs + 1];
    private readonly uint[] _masks = new uint[MaxPairs + 1];
    private readonly int[] _users = new int[MaxPairs + 1];
    private readonly Dictionary<ulong, int> _indices = [];
    private readonly List<int> _free = [];
    private int _next = 1; // index 0 is never handed out

    /// <summary>Distinct pairs in use.</summary>
    public int Count => _indices.Count;

    public static ushort ObjectLayer(byte broadPhase, int index) => (ushort)((broadPhase << IndexBits) | index);

    public static byte BroadPhaseOf(ushort objectLayer) => (byte)(objectLayer >> IndexBits);

    public uint LayerOf(int index) => _layers[index];

    public uint MaskOf(int index) => _masks[index];

    /// <summary>The pair's index (one more user); throws once <see cref="MaxPairs"/> distinct pairs are in use.</summary>
    public int Acquire(uint layer, uint mask)
    {
        var key = ((ulong)layer << 32) | mask;
        if (_indices.TryGetValue(key, out var index))
        {
            _users[index]++;
            return index;
        }

        if (_free.Count > 0)
        {
            index = _free[^1];
            _free.RemoveAt(_free.Count - 1);
        }
        else if (_next <= MaxPairs)
        {
            index = _next++;
        }
        else
        {
            throw new InvalidOperationException(
                $"Jolt supports at most {MaxPairs} distinct collision layer/mask combinations in use at once in one physics space; " +
                "use fewer combinations or set physics.3d.backend to \"jitter2\".");
        }

        _layers[index] = layer;
        _masks[index] = mask;
        _users[index] = 1;
        _indices.Add(key, index);
        return index;
    }

    /// <summary>One user fewer; the index is freed for reuse with its last user.</summary>
    public void Release(int index)
    {
        if (--_users[index] > 0)
            return;
        _indices.Remove(((ulong)_layers[index] << 32) | _masks[index]);
        _free.Add(index);
    }

    /// <summary>The engine's layer rule (<see cref="PhysicsSpace3D.Collides"/>) on two encoded object layers.</summary>
    public bool Collides(ushort a, ushort b)
    {
        int ia = a & IndexMask, ib = b & IndexMask;
        return PhysicsSpace3D.Collides(_layers[ia], _masks[ia], BroadPhaseOf(a) == Area, _layers[ib], _masks[ib], BroadPhaseOf(b) == Area);
    }

    /// <summary>A query with <paramref name="mask"/> reports bodies on this object layer (never areas).</summary>
    public bool InQueryMask(ushort objectLayer, uint mask) => BroadPhaseOf(objectLayer) != Area && (_layers[objectLayer & IndexMask] & mask) != 0;
}

/// <summary>Jolt's object-pair filter over a space's <see cref="JoltLayerTable"/> (worker threads, during <c>Update</c>).</summary>
internal sealed class JoltObjectLayerPairFilter(JoltLayerTable table) : ManagedObjectLayerPairFilter
{
    /// <summary>Test hook (Review Focus 5): throws from the worker thread that evaluates the next pair.</summary>
    internal volatile bool FaultForTesting;

    protected override bool ShouldCollide(ushort layer1, ushort layer2)
    {
        if (FaultForTesting)
            throw new InvalidOperationException("Injected layer filter fault (test hook).");
        return table.Collides(layer1, layer2);
    }
}

/// <summary>Object vs broad-phase layer: static never meets static, areas never meet areas; stateless, one per process.</summary>
internal sealed class JoltObjectVsBroadPhaseFilter : ManagedObjectVsBroadPhaseLayerFilter
{
    protected override bool ShouldCollide(ushort layer1, byte layer2) => JoltLayerTable.BroadPhaseOf(layer1) switch
    {
        JoltLayerTable.Static => layer2 != JoltLayerTable.Static,
        JoltLayerTable.Area => layer2 != JoltLayerTable.Area,
        _ => true,
    };
}

/// <summary>The broad-phase filter of every query: areas are never reported.</summary>
internal sealed class JoltNoAreasFilter : ManagedBroadPhaseLayerFilter
{
    protected override bool ShouldCollide(byte layer) => layer != JoltLayerTable.Area;
}
```

- [ ] **Step 5: Shared objects in `JoltRuntime.cs`** — replace the empty `InitializeShared` and add the members:

```csharp
    private static BroadPhaseLayerInterfaceTable s_broadPhaseLayers;
    private static JoltObjectVsBroadPhaseFilter? s_objectVsBroadPhase;
    private static JoltNoAreasFilter? s_noAreas;

    /// <summary>All 65 536 object layers mapped to their broad-phase layer (the top two bits; the unused fourth value to Moving).</summary>
    public static BroadPhaseLayerInterface BroadPhaseLayers => s_broadPhaseLayers;

    public static JoltObjectVsBroadPhaseFilter ObjectVsBroadPhase => s_objectVsBroadPhase ?? throw NotInitialized();

    public static JoltNoAreasFilter NoAreas => s_noAreas ?? throw NotInitialized();

    /// <summary>An exception a shared filter caught on a worker thread since the last call, or null.</summary>
    public static Exception? TakeSharedCallbackException() =>
        s_objectVsBroadPhase?.TakeCallbackException() ?? s_noAreas?.TakeCallbackException();

    private static InvalidOperationException NotInitialized() => new("JoltRuntime.TryInitialize has not succeeded.");

    private static void InitializeShared()
    {
        var layers = BroadPhaseLayerInterfaceTable.Create(JoltLayerTable.ObjectLayerCount, JoltLayerTable.BroadPhaseCount);
        for (var layer = 0; layer < JoltLayerTable.ObjectLayerCount; layer++)
        {
            var broadPhase = JoltLayerTable.BroadPhaseOf((ushort)layer);
            layers.MapObjectToBroadPhaseLayer((ushort)layer, broadPhase <= JoltLayerTable.Area ? broadPhase : JoltLayerTable.Moving);
        }

        s_broadPhaseLayers = layers;
        s_objectVsBroadPhase = new JoltObjectVsBroadPhaseFilter();
        s_noAreas = new JoltNoAreasFilter();
    }
```

Delete `RawJoltMath` from `RawJoltWorld.cs` (the tests now use `JoltMath`). In `BackendSourceRulesTests.Rules` add
`{ "Brutal.Numerics", "MainframeEngine/Src/Physics/3D/Jolt/", true },`.

- [ ] **Step 6: Run** `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltMathTests|FullyQualifiedName~JoltLayerTableTests|FullyQualifiedName~JoltRuntimeTests|FullyQualifiedName~JoltApiFactsTests|FullyQualifiedName~BackendSourceRulesTests"` → all pass (Jolt ones skipped on osx-x64); `just build` → 0 warnings; physics suite and trace gate → pass.

- [ ] **Step 7: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Jolt/ Tests/MainframeEngine.Tests/Physics/Jolt/ Tests/MainframeEngine.Tests/Physics/BackendSourceRulesTests.cs
git commit -F- <<'EOF'
Add Jolt math conversions, interned object layers and the shared broad-phase objects

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---
### Task 3: Shapes

**Files:**
- Create: `MainframeEngine/Src/Physics/3D/Jolt/JoltShapes.cs`, `MainframeEngine/Src/Physics/3D/Jolt/JoltNativeLedger.cs`
- Create: `Tests/MainframeEngine.Tests/Physics/Jolt/JoltShapeTests.cs`

**Interfaces:**
- Consumes: `ShapeDescription3D`/`ShapeKind3D` (B2 Task 6), `Shape3D.Describe`/`DescribeQuery`/`ChangeCount`, `HeightMapShape3D.MapWidth/MapDepth/MapData`, `Basis` (`X`, `Y`, `Z` columns, `Transform`, `GetRotation`, `==`), `JoltApiFacts.HeightFieldBlockSize`/`HeightFieldDiagonalMatchesEngine`, `JoltMath`; binding `BoxShapeSettings.Create(float3, float, PhysicsMaterial)`, `SphereShapeSettings.Create(float, PhysicsMaterial)`, `CapsuleShapeSettings.Create(float, float, PhysicsMaterial)`, `CylinderShapeSettings.Create(float, float, float, PhysicsMaterial)`, `ConvexHullShapeSettings.Create(ReadOnlySpan<float3>, float, PhysicsMaterial)`, `MeshShapeSettings.Create(ReadOnlySpan<Triangle>, ReadOnlySpan<PhysicsMaterial>)`, `HeightFieldShapeSettings.Create(Ptr<float>, float3, float3, uint, Ptr<byte>, ReadOnlySpan<PhysicsMaterial>)` + `SetBlockSize(uint)`, `*.CreateShape()` (throws `JoltException`).
- Produces: `internal static class JoltShapeBuilder` (`MaxConvexRadius`, `ConvexRadius(float)`, `TryDecompose(in Basis, out Quaternion, out Vector3, out bool)`, `Build(in ShapeDescription3D, out Quaternion, Action<Shape3D, string>) → Shape?`); `internal readonly record struct ShapeKey(Shape3D Resource, int ChangeCount, Basis Linear)`; `internal sealed class JoltShapeCache` (`Entry` with `Key`, `Shape`, `Rotation`, `Users`; `Acquire(in ShapeDescription3D) → Entry?`, `Release(Entry)`, `QueryShape(in ShapeDescription3D) → Shape`, `Count`, `QueryCount`, `MaxQueryShapes`, `Dispose()`); `internal sealed class JoltNativeLedger` (`Created(string)`, `Disposed(string)`, `Count(string)`, `Outstanding`, `Describe()`, kind constants).

Mapping (spec table, as built): box → `BoxShape(half extents × |scale|, convex radius min(0.05, 0.1 · smallest half extent))`;
sphere → `SphereShape(r × largest |scale| axis)`; capsule → `CapsuleShape(MidHeight / 2 × s, r × s)` with `s` the largest
axis (a zero `MidHeight` is a sphere); cylinder → `CylinderShape(Height / 2 × |scale.Y|, r × max(|scale.X|, |scale.Z|),
convex radius as for boxes)`; hull → `ConvexHullShape` of the points with the description's whole linear map baked in;
concave mesh → `MeshShape` of the triangles with the linear map baked in (degenerate triangles skipped, as Jitter2's
`ignoreDegenerated`; no triangles → nothing built, no error, as Jitter2); height map → `HeightFieldShape` when square, the
sample count has a block size (`JoltApiFacts.HeightFieldBlockSize`), the linear map is rotation × scale without shear and
Jolt accepts it, otherwise the mesh of its triangles. Scales are baked into the primitive (exact for boxes, cylinders,
hulls, meshes and height fields, so no `ScaledShape` decorator is needed: a recorded deviation from the spec's
"`ScaledShape`" row); the rotation goes to the compound child or a `RotatedTranslatedShape` (Task 4). Non-uniform scale on
spheres and capsules (and X ≠ Z on cylinders) uses the largest axis and logs once per resource and space; a sheared
linear map on a primitive uses the nearest rotation and scale and logs once.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>Neutral shape descriptions → Jolt shapes, and the per-space cache (B3).</summary>
public sealed class JoltShapeTests : IDisposable
{
    private readonly JoltNativeLedger _ledger = new();
    private readonly JoltShapeCache _cache;
    private readonly List<ShapeDescription3D> _descriptions = [];

    public JoltShapeTests()
    {
        RawJoltWorld.SkipUnlessAvailable();
        _cache = new JoltShapeCache(_ledger);
    }

    public void Dispose() => _cache.Dispose();

    private ShapeDescription3D Describe(Shape3D shape, Basis linear)
    {
        _descriptions.Clear();
        shape.Describe(_descriptions, linear, Vector3.Zero);
        return Assert.Single(_descriptions);
    }

    private static Basis Scaled(float x, float y, float z) => new(new Vector3(x, 0, 0), new Vector3(0, y, 0), new Vector3(0, 0, z));

    [Fact]
    public void BoxesBakeTheirScaleAndFollowTheConvexRadiusRule()
    {
        var entry = _cache.Acquire(Describe(new BoxShape3D { Size = new Vector3(2, 0.2f, 4) }, Scaled(1, 2, 1)))!;
        var box = entry.Shape.AsBoxShape();
        Assert.Equal(new Vector3(1, 0.2f, 2), box.GetHalfExtent().FromJolt());
        Assert.Equal(0.02f, box.GetConvexRadius(), 6); // 0.1 × the smallest half extent, under 0.05
        Assert.Equal(Quaternion.Identity, entry.Rotation);
        Assert.Equal(0.05f, JoltShapeBuilder.ConvexRadius(10f));
    }

    [Fact]
    public void RotationsAreSplitOffForTheCompound()
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        var entry = _cache.Acquire(Describe(new BoxShape3D(), Basis.FromRotationScale(turn, new Vector3(2, 3, 4))))!;
        Assert.True(MathF.Abs(Quaternion.Dot(turn, entry.Rotation)) > 0.99999f);
        Assert.Equal(new Vector3(1, 1.5f, 2), entry.Shape.AsBoxShape().GetHalfExtent().FromJolt());
    }

    [Fact]
    public void SpheresAndCapsulesUseTheLargestAxisOfANonUniformScaleAndWarnOnce()
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            var sphere = new SphereShape3D { Radius = 0.5f };
            var a = _cache.Acquire(Describe(sphere, Scaled(1, 3, 2)))!;
            Assert.Equal(1.5f, a.Shape.AsSphereShape().GetRadius(), 5);
            _cache.Acquire(Describe(sphere, Scaled(1, 4, 2))); // another scale, same resource: no second warning
            var capsule = _cache.Acquire(Describe(new CapsuleShape3D { Radius = 0.5f, Height = 3 }, Scaled(2, 1, 1)))!;
            Assert.Equal(1f, capsule.Shape.AsCapsuleShape().GetRadius(), 5);
            Assert.Equal(2f, capsule.Shape.AsCapsuleShape().GetHalfHeightOfCylinder(), 5); // MidHeight 2 / 2 × 2
            Assert.Equal(2, sink.Snapshot().Count(e => e.Message.Contains("non-uniform scale", StringComparison.Ordinal)));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void ACapsuleWithoutACylinderIsASphere()
    {
        var entry = _cache.Acquire(Describe(new CapsuleShape3D { Radius = 1f, Height = 2f }, Basis.Identity))!;
        Assert.False(entry.Shape.AsSphereShape().IsNull());
    }

    [Fact]
    public void CylindersScaleHeightAndRadiusSeparately()
    {
        var entry = _cache.Acquire(Describe(new CylinderShape3D { Radius = 0.5f, Height = 2 }, Scaled(2, 3, 2)))!;
        var cylinder = entry.Shape.AsCylinderShape();
        Assert.Equal(3f, cylinder.GetHalfHeight(), 5);
        Assert.Equal(1f, cylinder.GetRadius(), 5);
        Assert.Equal(0.05f, cylinder.GetConvexRadius(), 5);
    }

    [Fact]
    public void ConvexHullsBakeTheWholeLinearMapIncludingShear()
    {
        var shear = new Basis(Vector3.UnitX, new Vector3(1, 1, 0), Vector3.UnitZ);
        var hull = new ConvexPolygonShape3D { Points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)] };
        var entry = _cache.Acquire(Describe(hull, shear))!;
        var max = entry.Shape.GetLocalBounds().Max.FromJolt() + entry.Shape.GetCenterOfMass().FromJolt(); // local bounds are about the centre of mass
        Assert.Equal(1f, max.X, 3); // (0,1,0) sheared to (1,1,0)
        Assert.Equal(1f, max.Y, 3);
        Assert.Equal(Quaternion.Identity, entry.Rotation);
    }

    [Fact]
    public void MeshesBakeTheirLinearMapAndBuildNothingWithoutTriangles()
    {
        var mesh = new ConcavePolygonShape3D { Faces = [new(-1, 0, -1), new(-1, 0, 1), new(1, 0, -1)] };
        var entry = _cache.Acquire(Describe(mesh, Scaled(3, 1, 3)))!;
        Assert.Equal(3f, entry.Shape.GetLocalBounds().Max.X + entry.Shape.GetCenterOfMass().X, 4);
        Assert.Null(_cache.Acquire(Describe(new ConcavePolygonShape3D { Faces = [] }, Basis.Identity)));
        Assert.Null(_cache.Acquire(Describe(new ConcavePolygonShape3D { Faces = [Vector3.Zero, Vector3.Zero, Vector3.UnitX] }, Basis.Identity)));
    }

    [Fact]
    public void ZeroScaleIsRefused()
    {
        var e = Assert.Throws<InvalidOperationException>(() => _cache.Acquire(Describe(new BoxShape3D(), Scaled(1, 0, 1))));
        Assert.Contains("zero or non-finite", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 4, true)]   // a height field (block size 2)
    [InlineData(3, 3, false)]  // no block size: the mesh
    [InlineData(4, 3, false)]  // not square: the mesh
    public void HeightMapsKeepTheEnginesSurfaceAsHeightFieldOrMesh(int width, int depth, bool heightField)
    {
        var data = new float[width * depth];
        for (var i = 0; i < data.Length; i++)
            data[i] = (i * 7 % 5) * 0.3f;
        var map = new HeightMapShape3D { MapWidth = width, MapDepth = depth, MapData = data };
        var linear = Basis.FromRotationScale(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), new Vector3(2, 1.5f, 2));
        var entry = _cache.Acquire(Describe(map, linear))!;
        Assert.Equal(heightField, !entry.Shape.AsHeightFieldShape().IsNull());

        using var w = new RawJoltWorld();
        using var placed = RotatedTranslatedShape.Create(new float3(0), entry.Rotation.ToJolt(), entry.Shape);
        w.Add(placed, Vector3.Zero, Quaternion.Identity, MotionType.Static, RawJoltWorld.StaticLayer);
        using var settings = RayCastSettings.Create();
        var faces = map.GetFaces().ToArray();
        for (var x = -1.3f; x <= 1.3f; x += 0.37f)
            for (var z = -1.1f; z <= 1.1f; z += 0.41f)
            {
                var from = new Vector3(x, 20, z);
                var expected = EngineSurface(faces, linear, from);
                var ray = new RRayCast { Origin = from.ToJolt(), Direction = new float3(0, -40, 0) };
                var hit = w.Query.CastRayClosest(in ray, settings, out var result);
                Assert.Equal(expected.HasValue, hit);
                if (hit)
                    Assert.Equal(expected!.Value, from.Y - 40 * result.Fraction, 3);
            }
    }

    // The engine's triangles (as Jitter2 and debug draw use them) under the linear map, hit by a ray straight down.
    private static float? EngineSurface(Vector3[] faces, Basis linear, Vector3 from)
    {
        float? best = null;
        for (var i = 0; i < faces.Length; i += 3)
        {
            Vector3 a = linear.Transform(faces[i]), b = linear.Transform(faces[i + 1]), c = linear.Transform(faces[i + 2]);
            var d = new Vector3(0, -1, 0);
            Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f)
                continue;
            var t0 = from - a;
            var u = Vector3.Dot(t0, p) / det;
            var q = Vector3.Cross(t0, e1);
            var v = Vector3.Dot(d, q) / det;
            if (u < 0 || v < 0 || u + v > 1)
                continue;
            var y = from.Y - Vector3.Dot(e2, q) / det;
            best = best is null ? y : MathF.Max(best.Value, y);
        }

        return best;
    }

    [Fact]
    public void TheCacheSharesOneShapePerResourceScaleAndChangeAndReleasesUnusedOnes()
    {
        var box = new BoxShape3D();
        var a = _cache.Acquire(Describe(box, Basis.Identity))!;
        var b = _cache.Acquire(Describe(box, Basis.Identity))!;
        Assert.Same(a, b);
        Assert.Equal(2, a.Users);
        Assert.NotSame(a, _cache.Acquire(Describe(box, Scaled(2, 2, 2))));
        box.Size = new Vector3(3);
        var changed = _cache.Acquire(Describe(box, Basis.Identity))!;
        Assert.NotSame(a, changed);
        Assert.Equal(3, _cache.Count);

        _cache.Release(a);
        _cache.Release(b);
        Assert.Equal(2, _cache.Count);
#if DEBUG
        Assert.Equal(2, _ledger.Count(JoltNativeLedger.Shape));
#endif
    }

    [Fact]
    public void QueryShapesAreCachedPerResourceUntilTheyChangeAndBounded()
    {
        var probe = new SphereShape3D { Radius = 0.5f };
        var first = _cache.QueryShape(probe.DescribeQuery());
        Assert.Equal(first.Pointer, _cache.QueryShape(probe.DescribeQuery()).Pointer);
        probe.Radius = 1f;
        Assert.Equal(1f, _cache.QueryShape(probe.DescribeQuery()).AsSphereShape().GetRadius(), 5);
        Assert.Equal(1, _cache.QueryCount);
        for (var i = 0; i < JoltShapeCache.MaxQueryShapes + 10; i++)
            _cache.QueryShape(new SphereShape3D { Radius = 0.1f + i * 0.001f }.DescribeQuery());
        Assert.True(_cache.QueryCount <= JoltShapeCache.MaxQueryShapes);
    }
}
```

- [ ] **Step 2: Run them to see them fail** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltShapeTests"` → build errors (`JoltShapeCache` missing).

- [ ] **Step 3: Write `JoltNativeLedger.cs`**

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// The Jolt backend's live native handles by kind, counted in Debug builds (BRUTAL handles have no finalizers, so a
/// missed <c>Dispose</c> leaks for the process lifetime). <see cref="JoltBackend.Dispose"/> logs an error if any is left.
/// </summary>
internal sealed class JoltNativeLedger
{
    public const string World = "physics system";
    public const string Allocator = "temp allocator";
    public const string JobSystem = "job system";
    public const string Listener = "contact listener";
    public const string Filter = "filter";
    public const string Settings = "query settings";
    public const string Shape = "shape";
    public const string Helper = "creation helper";

#if DEBUG
    private readonly Dictionary<string, int> _live = [];
#endif

    [Conditional("DEBUG")]
    public void Created(string kind)
    {
#if DEBUG
        CollectionsMarshal.GetValueRefOrAddDefault(_live, kind, out _)++;
#endif
    }

    [Conditional("DEBUG")]
    public void Disposed(string kind)
    {
#if DEBUG
        CollectionsMarshal.GetValueRefOrAddDefault(_live, kind, out _)--;
#endif
    }

    /// <summary>Live handles of one kind (0 in Release builds).</summary>
    public int Count(string kind)
    {
#if DEBUG
        return _live.GetValueOrDefault(kind);
#else
        return 0;
#endif
    }

    /// <summary>Live handles of every kind (0 in Release builds).</summary>
    public int Outstanding
    {
        get
        {
            var total = 0;
#if DEBUG
            foreach (var count in _live.Values)
                total += count;
#endif
            return total;
        }
    }

    public string Describe()
    {
#if DEBUG
        return string.Join(", ", _live.Where(p => p.Value != 0).Select(p => $"{p.Value} {p.Key}"));
#else
        return "";
#endif
    }
}
```

- [ ] **Step 4: Write `JoltShapes.cs`**

```csharp
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal.JoltApi;
using Brutal.Numerics;
using Brutal.Pointers;

namespace MainframeEngine;

/// <summary>
/// Builds one Jolt shape from a <see cref="ShapeDescription3D"/> (B3; mapping in the physics doc's Backends section).
/// Scales are baked into the shape; the rotation of a primitive is returned for its compound child; hulls and meshes
/// bake the whole linear map. Height maps become height fields with the engine's cell diagonal (a quarter turn of the
/// grid when Jolt's split differs, <see cref="JoltApiFacts.HeightFieldDiagonalMatchesEngine"/>), or meshes.
/// Main thread only (called from a space's flush).
/// </summary>
internal static unsafe class JoltShapeBuilder
{
    public const float MaxConvexRadius = 0.05f;
    private const float MinScale = 1e-6f;
    private static readonly Quaternion QuarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);

    /// <summary>The spec's rule: min(0.05, 0.1 × the smallest half extent).</summary>
    public static float ConvexRadius(float smallestHalfExtent) => MathF.Min(MaxConvexRadius, 0.1f * smallestHalfExtent);

    /// <summary>
    /// <paramref name="linear"/> as rotation × scale (mirrors folded into the rotation's handedness: every primitive is
    /// symmetric); false for a zero or non-finite axis. <paramref name="sheared"/> when the columns are not orthogonal.
    /// </summary>
    public static bool TryDecompose(in Basis linear, out Quaternion rotation, out Vector3 scale, out bool sheared)
    {
        Vector3 x = linear.X, y = linear.Y, z = linear.Z;
        scale = new Vector3(x.Length(), y.Length(), z.Length());
        rotation = Quaternion.Identity;
        sheared = false;
        if (!(scale.X > MinScale && scale.Y > MinScale && scale.Z > MinScale) || !float.IsFinite(scale.X + scale.Y + scale.Z))
            return false;
        x /= scale.X;
        y /= scale.Y;
        z /= scale.Z;
        if (Vector3.Dot(Vector3.Cross(x, y), z) < 0)
            x = -x;
        sheared = MathF.Abs(Vector3.Dot(x, y)) > 1e-4f || MathF.Abs(Vector3.Dot(x, z)) > 1e-4f || MathF.Abs(Vector3.Dot(y, z)) > 1e-4f;
        rotation = Quaternion.Normalize(new Basis(x, y, z).GetRotation());
        return true;
    }

    /// <summary>The shape, or null when there is nothing to build (a mesh without triangles). Throws for invalid data.</summary>
    public static Shape? Build(in ShapeDescription3D d, out Quaternion rotation, Action<Shape3D, string> warn)
    {
        if (!TryDecompose(d.Linear, out rotation, out var s, out var sheared))
            throw new InvalidOperationException("its scale has a zero or non-finite axis");
        switch (d.Kind)
        {
            case ShapeKind3D.ConvexHull:
                rotation = Quaternion.Identity;
                return Hull(d.Points!, d.Linear);
            case ShapeKind3D.ConcaveMesh:
                rotation = Quaternion.Identity;
                return Mesh(d.Points!, d.Linear);
            case ShapeKind3D.HeightMap:
                if (!sheared && HeightField((HeightMapShape3D)d.Resource, rotation, s, out var turned) is { } field)
                {
                    rotation = turned;
                    return field;
                }

                rotation = Quaternion.Identity;
                return Mesh(d.Points!, d.Linear);
        }

        if (sheared)
            warn(d.Resource, $"{d.Resource.GetType().Name} has a sheared transform: Jolt uses the nearest rotation and scale");
        switch (d.Kind)
        {
            case ShapeKind3D.Box:
            {
                var half = d.Size * 0.5f * s;
                using var settings = BoxShapeSettings.Create(half.ToJolt(), ConvexRadius(MathF.Min(half.X, MathF.Min(half.Y, half.Z))), default);
                return settings.CreateShape();
            }
            case ShapeKind3D.Sphere:
            {
                using var settings = SphereShapeSettings.Create(d.Radius * Uniform(d.Resource, s, warn), default);
                return settings.CreateShape();
            }
            case ShapeKind3D.Capsule:
            {
                var k = Uniform(d.Resource, s, warn);
                if (d.MidHeight <= 0f)
                {
                    using var sphere = SphereShapeSettings.Create(d.Radius * k, default);
                    return sphere.CreateShape();
                }

                using var settings = CapsuleShapeSettings.Create(d.MidHeight * 0.5f * k, d.Radius * k, default);
                return settings.CreateShape();
            }
            case ShapeKind3D.Cylinder:
            {
                var radius = d.Radius * Round(d.Resource, s, warn);
                var half = d.Height * 0.5f * s.Y;
                using var settings = CylinderShapeSettings.Create(half, radius, ConvexRadius(MathF.Min(half, radius)), default);
                return settings.CreateShape();
            }
            default:
                throw new NotSupportedException($"{d.Resource.GetType().Name} has no Jolt shape.");
        }
    }

    private static float Uniform(Shape3D resource, Vector3 s, Action<Shape3D, string> warn)
    {
        var max = MathF.Max(s.X, MathF.Max(s.Y, s.Z));
        var min = MathF.Min(s.X, MathF.Min(s.Y, s.Z));
        if (max - min > 1e-4f * max)
            warn(resource, $"{resource.GetType().Name} has a non-uniform scale {s}: Jolt scales spheres and capsules uniformly, by the largest axis");
        return max;
    }

    private static float Round(Shape3D resource, Vector3 s, Action<Shape3D, string> warn)
    {
        var max = MathF.Max(s.X, s.Z);
        if (MathF.Abs(s.X - s.Z) > 1e-4f * max)
            warn(resource, $"{resource.GetType().Name} has a non-uniform scale {s}: Jolt scales a cylinder's radius uniformly, by the larger of X and Z");
        return max;
    }

    private static Shape Hull(Vector3[] points, in Basis linear)
    {
        var baked = ArrayPool<Vector3>.Shared.Rent(points.Length);
        try
        {
            for (var i = 0; i < points.Length; i++)
                baked[i] = linear.Transform(points[i]);
            using var settings = ConvexHullShapeSettings.Create(JoltMath.AsJolt(baked.AsSpan(0, points.Length)), MaxConvexRadius, default);
            return settings.CreateShape();
        }
        finally
        {
            ArrayPool<Vector3>.Shared.Return(baked);
        }
    }

    private static Shape? Mesh(Vector3[] faces, in Basis linear)
    {
        var count = faces.Length / 3;
        if (count == 0)
            return null;
        var triangles = ArrayPool<Triangle>.Shared.Rent(count);
        try
        {
            var valid = 0;
            for (var i = 0; i < count; i++)
            {
                var a = linear.Transform(faces[i * 3]);
                var b = linear.Transform(faces[i * 3 + 1]);
                var c = linear.Transform(faces[i * 3 + 2]);
                if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-24f)
                    continue; // degenerate, as Jitter2's TriangleMesh(ignoreDegenerated: true)
                ref var t = ref triangles[valid++];
                t.V[0] = a.ToJolt();
                t.V[1] = b.ToJolt();
                t.V[2] = c.ToJolt();
                t.MaterialIndex = 0;
                t.UserData = 0;
            }

            if (valid == 0)
                return null;
            using var settings = MeshShapeSettings.Create(triangles.AsSpan(0, valid), default);
            return settings.CreateShape();
        }
        finally
        {
            ArrayPool<Triangle>.Shared.Return(triangles);
        }
    }

    private static float Height(float[] data, int width, int x, int z)
    {
        var index = z * width + x;
        return index < data.Length ? data[index] : 0f;
    }

    private static Shape? HeightField(HeightMapShape3D map, Quaternion rotation, Vector3 s, out Quaternion placed)
    {
        placed = rotation;
        var n = map.MapWidth;
        var block = JoltApiFacts.HeightFieldBlockSize(n);
        if (map.MapDepth != n || block == 0)
            return null;
        var data = map.MapData;
        var c = (n - 1) * 0.5f;
        var samples = ArrayPool<float>.Shared.Rent(n * n);
        try
        {
            float3 offset, scale;
            if (JoltApiFacts.HeightFieldDiagonalMatchesEngine)
            {
                for (var z = 0; z < n; z++)
                    for (var x = 0; x < n; x++)
                        samples[z * n + x] = Height(data, n, x, z);
                scale = new float3(s.X, s.Y, s.Z);
                offset = new float3(-c * s.X, 0, -c * s.Z);
            }
            else
            {
                // Jolt sample (x', y') = engine sample (n − 1 − y', x'); a quarter turn about Y maps Jolt's local (a, h, b)
                // back to the engine's (−b, h, a), and Jolt's (x, y)–(x+1, y+1) split onto the engine's (x+1, z)–(x, z+1).
                for (var y = 0; y < n; y++)
                    for (var x = 0; x < n; x++)
                        samples[y * n + x] = Height(data, n, n - 1 - y, x);
                scale = new float3(s.Z, s.Y, s.X);
                offset = new float3(-c * s.Z, 0, -c * s.X);
                placed = rotation * QuarterTurn;
            }

            fixed (float* p = samples)
            {
                using var settings = HeightFieldShapeSettings.Create(new Ptr<float>(p), offset, scale, (uint)n, default, default);
                settings.SetBlockSize((uint)block);
                try
                {
                    return settings.CreateShape();
                }
                catch (JoltException)
                {
                    placed = rotation;
                    return null; // Jolt refused the grid: the caller builds the mesh
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
        }
    }
}

/// <summary>A native shape's cache key: the resource (by reference), its change count and the baked linear map.</summary>
internal readonly record struct ShapeKey(Shape3D Resource, int ChangeCount, Basis Linear);

/// <summary>
/// One space's native shapes (B3). Body shapes are shared by every body of the space using the same resource, change
/// count and linear map, reference counted, and released when the last user lets go (so an edited resource's old shape
/// goes once its bodies rebuild). Query shapes (shape casts and overlaps, scale 1) are kept per resource until the
/// resource changes, at most <see cref="MaxQueryShapes"/> (beyond that the query cache starts over). Resources stay
/// referenced by the cache while in use. Main thread only.
/// </summary>
internal sealed class JoltShapeCache : IDisposable
{
    public const int MaxQueryShapes = 256;

    private readonly Dictionary<ShapeKey, Entry> _entries = new(KeyComparer.Instance);
    private readonly Dictionary<Shape3D, QueryEntry> _queries = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Shape3D> _warned = new(ReferenceEqualityComparer.Instance);
    private readonly JoltNativeLedger _ledger;
    private readonly Action<Shape3D, string> _warn;

    public JoltShapeCache(JoltNativeLedger ledger)
    {
        _ledger = ledger;
        _warn = WarnOnce;
    }

    /// <summary>Body shapes held.</summary>
    public int Count => _entries.Count;

    /// <summary>Query shapes held.</summary>
    public int QueryCount => _queries.Count;

    /// <summary>A body shape for <paramref name="d"/> (one more user), or null when there is nothing to build.</summary>
    public Entry? Acquire(in ShapeDescription3D d)
    {
        var key = new ShapeKey(d.Resource, d.ChangeCount, d.Linear);
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.Users++;
            return entry;
        }

        if (JoltShapeBuilder.Build(d, out var rotation, _warn) is not { } shape)
            return null;
        _ledger.Created(JoltNativeLedger.Shape);
        entry = new Entry(key, shape, rotation) { Users = 1 };
        _entries.Add(key, entry);
        return entry;
    }

    public void Release(Entry entry)
    {
        if (--entry.Users > 0)
            return;
        _entries.Remove(entry.Key);
        entry.Shape.Dispose();
        _ledger.Disposed(JoltNativeLedger.Shape);
    }

    /// <summary>The query shape of a convex description at scale 1 (<see cref="Shape3D.DescribeQuery"/>).</summary>
    public Shape QueryShape(in ShapeDescription3D d)
    {
        if (_queries.TryGetValue(d.Resource, out var cached))
        {
            if (cached.ChangeCount == d.ChangeCount)
                return cached.Shape;
            _queries.Remove(d.Resource);
            cached.Shape.Dispose();
            _ledger.Disposed(JoltNativeLedger.Shape);
        }

        if (_queries.Count >= MaxQueryShapes)
            ClearQueries();
        var shape = JoltShapeBuilder.Build(d, out _, _warn) ?? throw new NotSupportedException($"{d.Resource.GetType().Name} cannot be used as a query shape.");
        _ledger.Created(JoltNativeLedger.Shape);
        _queries.Add(d.Resource, new QueryEntry(d.ChangeCount, shape));
        return shape;
    }

    private void ClearQueries()
    {
        foreach (var query in _queries.Values)
        {
            query.Shape.Dispose();
            _ledger.Disposed(JoltNativeLedger.Shape);
        }

        _queries.Clear();
    }

    private void WarnOnce(Shape3D resource, string message)
    {
        if (_warned.Add(resource))
            Log.Warning($"[Physics] {message}.");
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Shape.Dispose();
            _ledger.Disposed(JoltNativeLedger.Shape);
        }

        _entries.Clear();
        ClearQueries();
        _warned.Clear();
    }

    internal sealed class Entry(ShapeKey key, Shape shape, Quaternion rotation)
    {
        public readonly ShapeKey Key = key;
        public readonly Shape Shape = shape;

        /// <summary>The rotation split off the description's linear map (identity for hulls and meshes).</summary>
        public readonly Quaternion Rotation = rotation;

        public int Users;
    }

    private readonly record struct QueryEntry(int ChangeCount, Shape Shape);

    private sealed class KeyComparer : IEqualityComparer<ShapeKey>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(ShapeKey a, ShapeKey b) => ReferenceEquals(a.Resource, b.Resource) && a.ChangeCount == b.ChangeCount && a.Linear == b.Linear;

        public int GetHashCode(ShapeKey key) => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Resource), key.ChangeCount, key.Linear);
    }
}
```

- [ ] **Step 5: Run** `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltShapeTests"` → 13 pass (skipped on osx-x64). If `HeightMapsKeepTheEnginesSurfaceAsHeightFieldOrMesh(4, 4, true)` fails on heights only, the quarter-turn mapping is wrong for this Jolt: check `JoltApiFacts.HeightFieldDiagonalMatchesEngine` against Task 1's fact test first; never loosen the 1 mm comparison. Then `just build` (0 warnings), physics suite and trace gate → pass.

- [ ] **Step 6: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Jolt/JoltShapes.cs MainframeEngine/Src/Physics/3D/Jolt/JoltNativeLedger.cs Tests/MainframeEngine.Tests/Physics/Jolt/JoltShapeTests.cs
git commit -F- <<'EOF'
Build Jolt shapes from neutral descriptions with a per-space refcounted cache

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 4: `JoltBackend`: world, bodies, state and teardown

**Files:**
- Create: `MainframeEngine/Src/Physics/3D/Jolt/JoltBodies.cs`, `MainframeEngine/Src/Physics/3D/Jolt/JoltBackend.cs`
- Create: `Tests/MainframeEngine.Tests/Physics/Jolt/JoltBackendTests.cs`

**Interfaces:**
- Consumes: the B2 contract (`PhysicsBackend3D` members, `BackendBody`, `BodyDescription3D`, `BodyProperties3D(Simulated, LinearDampingPerStep, AngularDampingPerStep, GravityScale, AxisLocks, Ccd, CanSleep)`, `ShapeError3D`), `PhysicsSettings3D` (`Gravity`, `SubstepCount`, `AllowDeactivation`, `MultiThreaded`, `Jolt.*`), `PhysicsSpace3D.MeasureLibraryAllocations`, Tasks 1–3; binding `PhysicsSystem` (`Create`, `Init`, `GetPhysicsSettings`, `SetPhysicsSettings(in)`, `SetGravity/GetGravity`, `SetCombine*`, `GetBodyInterfaceNoLock`, `GetNumActiveBodies(BodyType)`, `Update`, `GetMaxBodiesLimit/GetMaxBodyPairsLimit/GetMaxContactConstraintsLimit`), `BodyInterface` (`CreateBody`, `AddBody`, `RemoveBody(ies)`, `DestroyBody(ies)`, `SetShape`, `SetObjectLayer`, `SetMotionType`, `SetMotionQuality`, `SetGravityFactor`, `SetFriction`, `SetRestitution`, `GetPositionAndRotation`, `SetPositionAndRotation`, `MoveKinematic`, `Get/SetLinearVelocity`, `Get/SetAngularVelocity`, `AddForce`, `AddImpulse`, `AddTorque`, `IsActive`, `ActivateBody`, `InvalidateContactCache`), `Body` (`GetID`, `GetMotionProperties`, `SetAllowSleeping`), `MotionProperties` (`SetLinearDamping`, `SetAngularDamping`, `SetMassProperties`), `MassProperties` (`Create`, `GetMass`, `SetMass`, `SetInertia`, `ScaleToMass`), `StaticCompoundShapeSettings`, `RotatedTranslatedShape.Create`, `EmptyShape.Create`.
- Produces: `internal readonly record struct JoltChild(JoltShapeCache.Entry Entry, Vector3 Translation, Quaternion Rotation, int Description)`; `internal sealed class JoltBody` (fields below, `Handle`, `IsArea`, `BroadPhase`, `ObjectLayer`, `ShapeIndexOf(uint)`); `internal sealed class JoltBodyTable` (`Allocate`, `Map`, `Free`, `At`, `Get`, `Find`, `FromId`, `Capacity`, `Clear`); `internal sealed unsafe class JoltBackend : PhysicsBackend3D` (`public JoltBackend(PhysicsSettings3D)`, every override, `internal` test surface: `Ledger`, `PairFilter`, `Bodies`, `Shapes`, `Layers`, `static float DampingRate(float perStep, float dt, int substeps)`).

Behaviour (the spec's body table as built; B2's contract decides the shape of each call):

| Engine | Jolt (this task) |
|---|---|
| `CreateBody(desc)` (no shapes yet) | one reused `BodyCreationSettings` with the space's `EmptyShape`, motion type, object layer `(broadPhase << 14) | pair`, user data = handle index, `AllowDynamicOrKinematic` (static bodies may become kinematic or dynamic), friction/restitution, the properties below, mass `MassAndInertiaProvided` 1 kg / unit inertia until the first `SetShapes`; `CreateBody` (null → "space full" error), `AddBody` (activated unless static) |
| `Area3D` | kinematic sensor: `SetIsSensor(true)`, `SetCollideKinematicVsNonDynamic(true)`, `SetAllowSleeping(false)`, broad phase `Area`, activated |
| `SetShapes` | per description a cached shape (`JoltShapeCache.Acquire`; failures → `ShapeError3D(i, message)`), the root: none → `EmptyShape`; one child at the origin → that shape; one placed child → `RotatedTranslatedShape`; several → `StaticCompoundShape` (compound user data = description index); `SetShape(id, root, false, …)`; dynamic: mass properties from the root scaled to `dynamicMass`, unit inertia + `ShapeError3D(-1, …)` when the root has no volume, `AllowedDOFs` from the axis locks |
| `LinearDampingPerStep`/`AngularDampingPerStep` `p` | Jolt rate `c = n · (1 − (1 − p)^(1/n)) / dt` for `n = SubstepCount` (with `n = 1`: the spec's `c = (1 − e^(−d·dt))/dt`), re-applied when `dt` changes |
| `GravityScale` | `GravityFactor` |
| `AxisLocks` | `AllowedDOFs` = `63 & ~locks` through `MotionProperties.SetMassProperties`, no re-creation (the binding allows it: deviation from the spec's "recreates the body"); all six locked → held as a kinematic body (Jolt cannot lock every axis) |
| `Ccd` / `CanSleep` | `MotionQuality.LinearCast` / `Body.SetAllowSleeping` |
| `SetPose` / `Teleport` / `MoveKinematic` | `SetPositionAndRotation(DontActivate)` / `(Activate unless static)`, velocity kept / `MoveKinematic(id, p, q, dt)` |
| `WakeTouching` / `ClearContacts` | `ActivateBody` on every non-static body in the touching set (filled in Task 7) / `InvalidateContactCache` |
| `PhysicsSettings` | `NumVelocitySteps`/`NumPositionSteps` from `jolt.*`, `AllowSleeping = AllowDeactivation`, `TimeBeforeSleep = 1` s (Jitter2's `DeactivationTime`), `DeterministicSimulation = true` |
| `Step(dt)` | re-apply damping if `dt` changed, `Update(dt, SubstepCount, temp, jobs)` with `JobSystemSingleThreaded` or (when `MultiThreaded` and there is more than one core) a lazily created `JobSystemThreadPool(2048, 8, min(cores − 1, 8))`, then `TakeCallbackException` on every managed callback (rethrown), `PhysicsUpdateError` flags logged once each, then the contact drain (Task 7) |

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Physics;

/// <summary>The Jolt backend on its own, through the members the space uses (B3): bodies, state, steps, teardown.</summary>
public sealed class JoltBackendTests : IDisposable
{
    private const float Dt = 1f / 60f;
    private static readonly BodyProperties3D Simulated = new(true, 0f, 0f, 1f, AxisLock.None, false, true);
    private readonly JoltBackend _backend;
    private readonly List<ShapeDescription3D> _shapes = [];
    private readonly List<ShapeError3D> _errors = [];

    public JoltBackendTests()
    {
        RawJoltWorld.SkipUnlessAvailable();
        _backend = new JoltBackend(new PhysicsSettings3D { MultiThreaded = false });
    }

    public void Dispose() => _backend?.Dispose();

    internal static BackendBody Create(JoltBackend backend, PhysicsBodyKind kind, Vector3 position, uint layer = 1, uint mask = 1, BodyProperties3D? props = null) =>
        backend.CreateBody(new BodyDescription3D(kind, position, Quaternion.Identity, layer, mask,
            props ?? (kind == PhysicsBodyKind.Dynamic ? Simulated : default), PhysicsMaterial.DefaultFriction, 0f));

    private BackendBody Create(PhysicsBodyKind kind, Vector3 position, uint layer = 1, uint mask = 1, BodyProperties3D? props = null) =>
        Create(_backend, kind, position, layer, mask, props);

    private void SetShapes(BackendBody body, PhysicsBodyKind kind, float mass = 1f)
    {
        _errors.Clear();
        _backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), kind == PhysicsBodyKind.Dynamic ? mass : 0f, _errors);
        _shapes.Clear();
    }

    private BackendBody Add(PhysicsBodyKind kind, Vector3 position, Shape3D shape, uint layer = 1, uint mask = 1, BodyProperties3D? props = null)
    {
        var body = Create(kind, position, layer, mask, props);
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

    private Vector3 Position(BackendBody body)
    {
        _backend.GetPose(body, out var position, out _);
        return position;
    }

    [Fact]
    public void HandlesStartAtOneAndFreedOnesAreReused()
    {
        Assert.Equal(PhysicsBackendKind.Jolt, _backend.Kind);
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
        Assert.InRange(Position(box).Y, 0.45f, 0.55f);
        Assert.True(_backend.IsSleeping(box));
        var asleep = _backend.ActiveBodyCount;
        _backend.Wake(box);
        Assert.False(_backend.IsSleeping(box));
        Assert.Equal(asleep + 1, _backend.ActiveBodyCount);
    }

    [Fact]
    public void TheBroadPhaseUsesTheEnginesLayerRule()
    {
        Floor(); // layer 1, mask 1
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 1, 0), new BoxShape3D(), layer: 2, mask: 0);
        Run(60);
        Assert.True(Position(box).Y < -1f, $"fell through: {Position(box)}");

        _backend.SetFilter(box, 2, 1);
        _backend.Teleport(box, new Vector3(0, 1, 0), Quaternion.Identity);
        _backend.SetLinearVelocity(box, Vector3.Zero);
        Run(120);
        Assert.InRange(Position(box).Y, 0.45f, 0.55f);
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
    }

    [Fact]
    public void ADynamicBodyWithoutVolumeGetsAUnitInertiaAndAnError()
    {
        var body = Create(PhysicsBodyKind.Dynamic, new Vector3(0, 5, 0));
        new BoxShape3D().Describe(_shapes, new Basis(Vector3.UnitX, Vector3.Zero, Vector3.UnitZ), Vector3.Zero); // zero scale on Y
        SetShapes(body, PhysicsBodyKind.Dynamic);
        Assert.Equal([0, -1], _errors.Select(e => e.ShapeIndex));
        Run(10); // still steps: falls with a unit inertia
        Assert.True(Position(body).Y < 5f);
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

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void DampingPerStepMatchesTheJitter2Factor(int substeps)
    {
        using var backend = new JoltBackend(new PhysicsSettings3D { MultiThreaded = false, SubstepCount = substeps });
        var props = Simulated with { GravityScale = 0f, LinearDampingPerStep = 0.1f, AngularDampingPerStep = 0.2f };
        var body = Create(backend, PhysicsBodyKind.Dynamic, Vector3.Zero, props: props);
        new SphereShape3D().Describe(_shapes, Basis.Identity, Vector3.Zero);
        backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), 1f, _errors);
        _shapes.Clear();
        backend.SetLinearVelocity(body, new Vector3(10, 0, 0));
        backend.SetAngularVelocity(body, new Vector3(0, 5, 0));
        backend.Step(Dt);
        Assert.Equal(9f, backend.GetLinearVelocity(body).X, 3);  // × (1 − 0.1), as Jitter2's Damping
        Assert.Equal(4f, backend.GetAngularVelocity(body).Y, 3); // × (1 − 0.2)
        Assert.Equal(0.1f / Dt, JoltBackend.DampingRate(0.1f, Dt, 1), 4);
        Assert.Equal(60f, JoltBackend.DampingRate(1f, Dt, 1), 3);
    }

    [Fact]
    public void GravityScaleIsJoltsGravityFactor()
    {
        var body = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 100, 0), new SphereShape3D(), props: Simulated with { GravityScale = 0.5f });
        Run(60);
        Assert.Equal(-9.81f * 0.5f, _backend.GetLinearVelocity(body).Y, 1);
    }

    [Fact]
    public void AxisLocksChangeWithoutRecreatingTheBody()
    {
        var body = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 10, 0), new BoxShape3D(), props: Simulated with { GravityScale = 0f });
        var id = _backend.Bodies.Get(body).Id;
        _backend.SetProperties(body, Simulated with { GravityScale = 0f, AxisLocks = AxisLock.LinearX });
        _backend.ApplyImpulse(body, new Vector3(3, 3, 0), null);
        Run(10);
        Assert.Equal(id, _backend.Bodies.Get(body).Id);
        Assert.Equal(0f, Position(body).X, 5);
        Assert.True(Position(body).Y > 10.1f);
    }

    [Fact]
    public void ABodyWithEveryAxisLockedStaysPut()
    {
        var all = AxisLock.LinearX | AxisLock.LinearY | AxisLock.LinearZ | AxisLock.AngularX | AxisLock.AngularY | AxisLock.AngularZ;
        var body = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 10, 0), new BoxShape3D(), props: Simulated with { AxisLocks = all });
        _backend.ApplyImpulse(body, new Vector3(5, 5, 5), null);
        Run(30);
        Assert.Equal(new Vector3(0, 10, 0), Position(body));
        _backend.SetProperties(body, Simulated); // unlocked: falls again
        Run(30);
        Assert.True(Position(body).Y < 10f);
    }

    [Fact]
    public void KindsChangeBetweenStaticKinematicAndDynamic()
    {
        Floor();
        var body = Add(PhysicsBodyKind.Static, new Vector3(0, 3, 0), new BoxShape3D());
        Run(30);
        Assert.Equal(3f, Position(body).Y);
        _backend.SetKind(body, PhysicsBodyKind.Dynamic);
        _backend.SetProperties(body, Simulated);
        new BoxShape3D().Describe(_shapes, Basis.Identity, Vector3.Zero);
        SetShapes(body, PhysicsBodyKind.Dynamic);
        Run(120);
        Assert.InRange(Position(body).Y, 0.45f, 0.55f);
        _backend.SetKind(body, PhysicsBodyKind.Kinematic);
        _backend.SetProperties(body, default);
        _backend.SetLinearVelocity(body, new Vector3(1, 0, 0));
        Run(60);
        Assert.InRange(Position(body).X, 0.95f, 1.05f);
    }

    [Fact]
    public void TeleportKeepsTheVelocityAndWakesTheBody()
    {
        var body = Add(PhysicsBodyKind.Dynamic, Vector3.Zero, new SphereShape3D(), props: Simulated with { GravityScale = 0f });
        _backend.SetLinearVelocity(body, new Vector3(2, 0, 0));
        _backend.Teleport(body, new Vector3(0, 50, 0), Quaternion.Identity);
        Assert.False(_backend.IsSleeping(body));
        Assert.Equal(new Vector3(2, 0, 0), _backend.GetLinearVelocity(body));
        Assert.Equal(new Vector3(0, 50, 0), Position(body));
    }

    [Fact]
    public void MaterialsCombineWithTheMaximumLikeJitter2()
    {
        var floor = Floor();
        _backend.SetMaterial(floor, 1f, 0f);
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 0.5f, 0), new BoxShape3D());
        _backend.SetMaterial(box, 0f, 0f);
        _backend.SetLinearVelocity(box, new Vector3(4, 0, 0));
        Run(60);
        Assert.True(_backend.GetLinearVelocity(box).X < 0.5f, "max(0, 1) = 1: the box stops");
    }

    [Fact]
    public void AFullSpaceRefusesNewBodiesWithItsSetting()
    {
        var settings = new PhysicsSettings3D { MultiThreaded = false };
        settings.Jolt.MaxBodies = 2;
        using var backend = new JoltBackend(settings);
        Create(backend, PhysicsBodyKind.Static, Vector3.Zero);
        Create(backend, PhysicsBodyKind.Static, Vector3.Zero);
        var e = Assert.Throws<InvalidOperationException>(() => Create(backend, PhysicsBodyKind.Static, Vector3.Zero));
        Assert.Contains("physics.3d.jolt.maxBodies", e.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Create(backend, PhysicsBodyKind.Static, Vector3.Zero)); // a refusal leaks no slot and frees nothing
    }

    [Fact]
    public void DisposeIsIdempotentAndReleasesEveryNativeHandle()
    {
        var backend = new JoltBackend(new PhysicsSettings3D { MultiThreaded = true });
        var body = Create(backend, PhysicsBodyKind.Dynamic, new Vector3(0, 2, 0));
        new BoxShape3D().Describe(_shapes, Basis.Identity, new Vector3(1, 0, 0));
        new SphereShape3D().Describe(_shapes, Basis.FromRotationScale(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1), Vector3.One), Vector3.Zero);
        new ConvexPolygonShape3D { Points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)] }.Describe(_shapes, Basis.Identity, Vector3.Zero);
        backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), 2f, _errors);
        _shapes.Clear();
        var area = Create(backend, PhysicsBodyKind.Area, Vector3.Zero);
        new CapsuleShape3D().Describe(_shapes, Basis.Identity, Vector3.Zero);
        backend.SetShapes(area, CollectionsMarshal.AsSpan(_shapes), 0f, _errors);
        _shapes.Clear();
        for (var i = 0; i < 5; i++)
            backend.Step(Dt);
        backend.Dispose();
        backend.Dispose();
#if DEBUG
        Assert.True(backend.Ledger.Outstanding == 0, backend.Ledger.Describe());
#endif
    }

    // ---- Review Focus ------------------------------------------------------------------------------------------

    [Fact]
    public void DisposingMultiThreadedSpacesRightAfterAStepJoinsTheirWorkers()
    {
        var threads = Process.GetCurrentProcess().Threads.Count;
        for (var round = 0; round < 40; round++)
        {
            using var backend = new JoltBackend(new PhysicsSettings3D { MultiThreaded = true });
            var floor = Create(backend, PhysicsBodyKind.Static, new Vector3(0, -0.5f, 0));
            new BoxShape3D { Size = new Vector3(20, 1, 20) }.Describe(_shapes, Basis.Identity, Vector3.Zero);
            backend.SetShapes(floor, CollectionsMarshal.AsSpan(_shapes), 0f, _errors);
            _shapes.Clear();
            for (var i = 0; i < 20; i++)
            {
                var box = Create(backend, PhysicsBodyKind.Dynamic, new Vector3(i % 5, 1 + i / 5, 0));
                new BoxShape3D { Size = new Vector3(0.5f) }.Describe(_shapes, Basis.Identity, Vector3.Zero);
                backend.SetShapes(box, CollectionsMarshal.AsSpan(_shapes), 1f, _errors);
                _shapes.Clear();
            }

            backend.Step(Dt); // workers ran; disposed straight after
        }

        Process.GetCurrentProcess().Refresh();
        Assert.InRange(Process.GetCurrentProcess().Threads.Count, 0, threads + 4);
    }

    [Fact]
    public void ABodyBeyondTheLayerPairLimitIsRefusedAndTheSpaceKeepsWorking()
    {
        var floor = Floor();
        for (var i = 1; i < JoltLayerTable.MaxPairs; i++) // the floor holds pair (1, 1)
            Create(PhysicsBodyKind.Static, new Vector3(1000, 0, 0), layer: 1, mask: (uint)i + 1);
        Assert.Equal(JoltLayerTable.MaxPairs, _backend.Layers.Count);
        var before = _backend.Bodies.Capacity;
        Assert.Throws<InvalidOperationException>(() => Create(PhysicsBodyKind.Dynamic, Vector3.Zero, layer: 7, mask: 7));
        Assert.Throws<InvalidOperationException>(() => _backend.SetFilter(floor, 7, 7));
        Assert.Equal(before, _backend.Bodies.Capacity); // no slot leaked: the next body reuses it
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 2, 0), new BoxShape3D());
        Run(90);
        Assert.InRange(Position(box).Y, 0.45f, 0.55f);
    }

    [Fact]
    public void EditingASharedResourceRebuildsInBothSpacesAndReleasesTheOldShapes()
    {
        using var other = new JoltBackend(new PhysicsSettings3D { MultiThreaded = false });
        var shared = new BoxShape3D { Size = new Vector3(2) };
        BackendBody Build(JoltBackend backend)
        {
            var body = Create(backend, PhysicsBodyKind.Static, Vector3.Zero);
            shared.Describe(_shapes, Basis.Identity, Vector3.Zero);
            backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), 0f, _errors);
            _shapes.Clear();
            return body;
        }

        var here = Build(_backend);
        var there = Build(other);
        var shapesHere = _backend.Ledger.Count(JoltNativeLedger.Shape);
        shared.Size = new Vector3(4);
        foreach (var (backend, body) in new[] { (_backend, here), (other, there) })
        {
            shared.Describe(_shapes, Basis.Identity, Vector3.Zero);
            backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), 0f, _errors);
            _shapes.Clear();
            Assert.Equal(1, backend.Shapes.Count); // the old box is gone
        }

#if DEBUG
        Assert.Equal(shapesHere, _backend.Ledger.Count(JoltNativeLedger.Shape));
#endif
        shared.Describe(_shapes, Basis.Identity, Vector3.Zero);
        var current = _backend.Shapes.Acquire(_shapes[0])!; // the entry the body uses now: 2 users with this one
        _shapes.Clear();
        Assert.Equal(2, current.Users);
        Assert.Equal(new Vector3(2), current.Shape.AsBoxShape().GetHalfExtent().FromJolt()); // the 4 m box
        _backend.Shapes.Release(current);
    }

    [Fact]
    public void AThrowingLayerFilterRethrowsOnTheMainThreadAfterTheStep()
    {
        using var backend = new JoltBackend(new PhysicsSettings3D { MultiThreaded = true });
        var floor = Create(backend, PhysicsBodyKind.Static, new Vector3(0, -0.5f, 0));
        new BoxShape3D { Size = new Vector3(20, 1, 20) }.Describe(_shapes, Basis.Identity, Vector3.Zero);
        backend.SetShapes(floor, CollectionsMarshal.AsSpan(_shapes), 0f, _errors);
        _shapes.Clear();
        var box = Create(backend, PhysicsBodyKind.Dynamic, new Vector3(0, 0.5f, 0));
        new BoxShape3D().Describe(_shapes, Basis.Identity, Vector3.Zero);
        backend.SetShapes(box, CollectionsMarshal.AsSpan(_shapes), 1f, _errors);
        _shapes.Clear();

        backend.PairFilter.FaultForTesting = true;
        var e = Assert.Throws<InvalidOperationException>(() => backend.Step(Dt));
        Assert.Equal("A Jolt collision filter threw during the physics step; see the inner exception.", e.Message);
        Assert.Equal("Injected layer filter fault (test hook).", e.InnerException!.Message);

        backend.PairFilter.FaultForTesting = false;
        backend.Step(Dt); // usable again
    }
}
```

- [ ] **Step 2: Run them to see them fail** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltBackendTests"` → build errors (`JoltBackend` missing).

- [ ] **Step 3: Write `JoltBodies.cs`**

```csharp
using System.Numerics;
using Brutal.JoltApi;

namespace MainframeEngine;

/// <summary>One built shape of a body: a cached native shape at <see cref="Translation"/>/<see cref="Rotation"/> in the body's frame, from description <see cref="Description"/>.</summary>
internal readonly record struct JoltChild(JoltShapeCache.Entry Entry, Vector3 Translation, Quaternion Rotation, int Description);

/// <summary>A Jolt backend body: one handle's slot. Main thread only.</summary>
internal sealed class JoltBody(int index)
{
    public readonly int Index = index;
    public uint Id = JoltApiFacts.InvalidBodyId;
    public Body Body;
    public PhysicsBodyKind Kind;
    public uint Layer;
    public uint Mask;
    public int PairIndex;
    public BodyProperties3D Properties;
    public float Mass;

    /// <summary>Every axis locked: held kinematic (Jolt cannot lock all six degrees of freedom).</summary>
    public bool AllLocked;

    /// <summary>The body's shape; ours to dispose when <see cref="RootOwned"/>.</summary>
    public Shape Root;

    public bool RootOwned;

    /// <summary>A view of <see cref="Root"/> when it is a compound (never disposed); null otherwise.</summary>
    public StaticCompoundShape Compound;

    public readonly List<JoltChild> Children = [];

    /// <summary>Other body index → touching sub-shape pairs (Task 7: from the contact events).</summary>
    public readonly Dictionary<int, int> Contacts = [];

    public BackendBody Handle => new(Index);

    public bool IsArea => Kind == PhysicsBodyKind.Area;

    public byte BroadPhase => Kind switch
    {
        PhysicsBodyKind.Static => JoltLayerTable.Static,
        PhysicsBodyKind.Area => JoltLayerTable.Area,
        _ => JoltLayerTable.Moving,
    };

    public ushort ObjectLayer => JoltLayerTable.ObjectLayer(BroadPhase, PairIndex);

    /// <summary>The description index of the sub-shape <paramref name="subShapeId"/> hit (−1 when unknown).</summary>
    public int ShapeIndexOf(uint subShapeId)
    {
        if (Children.Count == 0)
            return -1;
        if (Compound.IsNull())
            return Children[0].Description;
        var child = Compound.GetSubShapeIndexFromID(subShapeId, out _);
        return child < (uint)Children.Count ? (int)Compound.GetCompoundUserData(child) : -1;
    }
}

/// <summary>Backend handles (from 1, freed ones reused) and the Jolt body id → slot map the contact events need.</summary>
internal sealed class JoltBodyTable
{
    private readonly List<JoltBody?> _slots = [null]; // index 0 is BackendBody.None
    private readonly List<int> _free = [];
    private int[] _byJoltIndex = new int[256];

    /// <summary>Slots 1..Capacity−1 exist (some may be free).</summary>
    public int Capacity => _slots.Count;

    public JoltBody? At(int index) => (uint)index < (uint)_slots.Count ? _slots[index] : null;

    public JoltBody? Find(BackendBody body) => At(body.Index);

    public JoltBody Get(BackendBody body) => At(body.Index) ?? throw new InvalidOperationException($"No Jolt backend body {body.Index}.");

    public JoltBody Allocate()
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free[^1];
            _free.RemoveAt(_free.Count - 1);
        }
        else
        {
            index = _slots.Count;
            _slots.Add(null);
        }

        var slot = new JoltBody(index);
        _slots[index] = slot;
        return slot;
    }

    public void Map(JoltBody slot)
    {
        var i = (int)(slot.Id & JoltApiFacts.BodyIdIndexMask);
        if (i >= _byJoltIndex.Length)
            Array.Resize(ref _byJoltIndex, Math.Max(i + 1, _byJoltIndex.Length * 2));
        _byJoltIndex[i] = slot.Index;
    }

    public void Free(JoltBody slot)
    {
        if (slot.Id != JoltApiFacts.InvalidBodyId)
        {
            var i = (int)(slot.Id & JoltApiFacts.BodyIdIndexMask);
            if (i < _byJoltIndex.Length && _byJoltIndex[i] == slot.Index)
                _byJoltIndex[i] = 0;
        }

        _slots[slot.Index] = null;
        _free.Add(slot.Index);
    }

    /// <summary>The slot of a live Jolt body id (a destroyed body's late events find nothing).</summary>
    public JoltBody? FromId(uint id)
    {
        var i = id & JoltApiFacts.BodyIdIndexMask;
        if (i >= (uint)_byJoltIndex.Length)
            return null;
        var slot = At(_byJoltIndex[i]);
        return slot is not null && slot.Id == id ? slot : null;
    }

    public void Clear()
    {
        _slots.Clear();
        _slots.Add(null);
        _free.Clear();
        Array.Clear(_byJoltIndex);
    }
}
```

- [ ] **Step 4: Write `JoltBackend.cs`** (queries, scans and character members throw `NotSupportedException` until Tasks 5, 7 and 8 replace them; the backend is not selectable before Task 6)

```csharp
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace MainframeEngine;

/// <summary>
/// The Jolt 5.5 backend (ADR 0192, B3) through <c>Ahwoo.BRUTAL.Jolt</c>: one <see cref="PhysicsSystem"/> per space with
/// its own temp allocator, job systems, pair filter, contact listener (Task 7), query objects (Task 5) and shape cache.
/// Engine behaviour (signals, interpolation, user moves, the layer rule, MoveAndSlide) stays in
/// <see cref="PhysicsSpace3D"/>; this class maps the seam onto Jolt (table in the physics doc's Backends section).
/// Main thread only, between steps; Jolt's workers run only inside <see cref="Step"/>.
/// </summary>
internal sealed unsafe class JoltBackend : PhysicsBackend3D
{
    private readonly PhysicsSettings3D _settings;
    private readonly int _substeps;
    private readonly JoltLayerTable _layers = new();
    private readonly JoltBodyTable _bodies = new();
    private readonly JoltShapeCache _shapes;
    private readonly JoltObjectLayerPairFilter _pairFilter;
    private readonly TempAllocatorImplWithMallocFallback _temp;
    private readonly JobSystemSingleThreaded _single;
    private readonly PhysicsSystem _system;
    private readonly BodyInterface _bi;
    private readonly Shape _empty;
    private readonly BodyCreationSettings _creation;
    private readonly MassProperties _unitMass;
    private readonly List<JoltChild> _children = [];
    private readonly List<uint> _ids = [];
    private JobSystemThreadPool _pool;
    private PhysicsUpdateError _reported;
    private float _dt = 1f / 60f;
    private long _step;
    private bool _disposed;

    public JoltBackend(PhysicsSettings3D settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!JoltRuntime.TryInitialize(out var reason))
            throw new PlatformNotSupportedException($"Jolt is not available: {reason}");
        _settings = settings;
        _substeps = settings.SubstepCount;
        var jolt = settings.Jolt;
        Ledger = new JoltNativeLedger();
        _shapes = new JoltShapeCache(Ledger);
        _pairFilter = new JoltObjectLayerPairFilter(_layers);
        Ledger.Created(JoltNativeLedger.Filter);
        _temp = TempAllocatorImplWithMallocFallback.Create(checked((uint)jolt.TempAllocatorMB * 1024u * 1024u));
        Ledger.Created(JoltNativeLedger.Allocator);
        _single = JobSystemSingleThreaded.Create(JoltApiFacts.MaxJobs);
        Ledger.Created(JoltNativeLedger.JobSystem);
        _system = PhysicsSystem.Create();
        Ledger.Created(JoltNativeLedger.World);
        _system.Init(
            Math.Min((uint)jolt.MaxBodies, PhysicsSystem.GetMaxBodiesLimit()), 0,
            Math.Min((uint)jolt.MaxBodyPairs, PhysicsSystem.GetMaxBodyPairsLimit()),
            Math.Min((uint)jolt.MaxContactConstraints, PhysicsSystem.GetMaxContactConstraintsLimit()),
            JoltRuntime.BroadPhaseLayers, JoltRuntime.ObjectVsBroadPhase, _pairFilter);
        var physics = _system.GetPhysicsSettings();
        physics.NumVelocitySteps = (uint)jolt.VelocitySteps;
        physics.NumPositionSteps = (uint)jolt.PositionSteps;
        physics.AllowSleeping = settings.AllowDeactivation;
        physics.TimeBeforeSleep = 1f; // Jitter2's DeactivationTime
        physics.DeterministicSimulation = true;
        _system.SetPhysicsSettings(in physics);
        _system.SetGravity(settings.Gravity.ToJolt());
        _system.SetCombineFriction(&JoltRuntime.CombineFriction);
        _system.SetCombineRestitution(&JoltRuntime.CombineRestitution);
        _bi = _system.GetBodyInterfaceNoLock();
        _empty = EmptyShape.Create();
        _creation = BodyCreationSettings.Create();
        _unitMass = MassProperties.Create();
        _unitMass.SetMass(1f);
        _unitMass.SetInertia(float4x4.Identity);
        Ledger.Created(JoltNativeLedger.Helper);
        Ledger.Created(JoltNativeLedger.Helper);
        Ledger.Created(JoltNativeLedger.Helper);
    }

    // ---- test surface --------------------------------------------------------------------------------------------

    internal JoltNativeLedger Ledger { get; }

    internal JoltObjectLayerPairFilter PairFilter => _pairFilter;

    internal JoltBodyTable Bodies => _bodies;

    internal JoltShapeCache Shapes => _shapes;

    internal JoltLayerTable Layers => _layers;

    // ---- world ---------------------------------------------------------------------------------------------------

    public override PhysicsBackendKind Kind => PhysicsBackendKind.Jolt;

    public override Vector3 Gravity
    {
        get => _system.GetGravity().FromJolt();
        set => _system.SetGravity(value.ToJolt());
    }

    public override int ActiveBodyCount => (int)_system.GetNumActiveBodies(BodyType.RigidBody);

    private static int Workers => Math.Min(Environment.ProcessorCount - 1, 8);

    public override void Step(float dt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dt != _dt)
        {
            _dt = dt;
            for (var i = 1; i < _bodies.Capacity; i++)
            {
                if (_bodies.At(i) is { Kind: not (PhysicsBodyKind.Static or PhysicsBodyKind.Area) } slot)
                    ApplyDamping(slot);
            }
        }

        var jobs = Jobs();
        var measure = PhysicsSpace3D.MeasureLibraryAllocations;
        var before = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var error = _system.Update(dt, _substeps, _temp, jobs);
        if (measure)
            LibraryAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        _step++;
        ThrowIfCallbacksFailed();
        if (error != PhysicsUpdateError.None)
            ReportUpdateError(error);
    }

    private JobSystem Jobs()
    {
        if (!_settings.MultiThreaded || Workers < 1)
            return _single;
        if (_pool.IsNull())
        {
            _pool = JobSystemThreadPool.Create(JoltApiFacts.MaxJobs, JoltApiFacts.MaxBarriers, Workers);
            Ledger.Created(JoltNativeLedger.JobSystem);
        }

        return _pool;
    }

    private void ThrowIfCallbacksFailed()
    {
        var e = _pairFilter.TakeCallbackException() ?? JoltRuntime.TakeSharedCallbackException();
        if (e is not null)
            throw new InvalidOperationException("A Jolt collision filter threw during the physics step; see the inner exception.", e);
    }

    private void ReportUpdateError(PhysicsUpdateError error)
    {
        foreach (var flag in (ReadOnlySpan<PhysicsUpdateError>)[PhysicsUpdateError.ManifoldCacheFull, PhysicsUpdateError.BodyPairCacheFull, PhysicsUpdateError.ContactConstraintsFull])
        {
            if ((error & flag) == 0 || (_reported & flag) != 0)
                continue;
            _reported |= flag;
            var key = flag == PhysicsUpdateError.BodyPairCacheFull ? "maxBodyPairs" : "maxContactConstraints";
            Log.Error($"[Physics] Jolt: {flag} in step {_step}; contacts were dropped (raise physics.3d.jolt.{key}).");
        }
    }

    // ---- bodies --------------------------------------------------------------------------------------------------

    private static bool IsAllLocked(in BodyProperties3D props) =>
        props.Simulated && (props.AxisLocks & (AxisLock)0b11_1111) == (AxisLock)0b11_1111;

    private static MotionType MotionTypeOf(JoltBody slot) => slot.Kind switch
    {
        PhysicsBodyKind.Static => MotionType.Static,
        PhysicsBodyKind.Dynamic when !slot.AllLocked => MotionType.Dynamic,
        _ => MotionType.Kinematic,
    };

    /// <summary>The Jolt damping rate for a per-step factor: <c>(1 − c·dt/n)^n = 1 − p</c> over <c>n</c> sub-steps.</summary>
    internal static float DampingRate(float perStep, float dt, int substeps)
    {
        if (perStep <= 0f)
            return 0f;
        if (perStep >= 1f)
            return substeps / dt;
        return substeps * (1f - MathF.Pow(1f - perStep, 1f / substeps)) / dt;
    }

    public override BackendBody CreateBody(in BodyDescription3D desc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pair = _layers.Acquire(desc.Layer, desc.Mask); // throws before anything is allocated
        var slot = _bodies.Allocate();
        slot.Kind = desc.Kind;
        slot.Layer = desc.Layer;
        slot.Mask = desc.Mask;
        slot.PairIndex = pair;
        slot.Properties = desc.Properties;
        slot.AllLocked = IsAllLocked(desc.Properties);
        slot.Root = _empty;
        var props = desc.Properties;
        var area = desc.Kind == PhysicsBodyKind.Area;
        var s = _creation;
        s.SetShape(_empty);
        s.SetPosition(desc.Position.ToJolt());
        s.SetRotation(desc.Rotation.ToJolt());
        s.SetMotionType(MotionTypeOf(slot));
        s.SetObjectLayer(slot.ObjectLayer);
        s.SetUserData((ulong)slot.Index);
        s.SetAllowDynamicOrKinematic(!area);
        s.SetIsSensor(area);
        s.SetCollideKinematicVsNonDynamic(area);
        s.SetAllowSleeping(!area && (!props.Simulated || props.CanSleep));
        s.SetFriction(desc.Friction);
        s.SetRestitution(desc.Bounce);
        s.SetGravityFactor(props.Simulated ? props.GravityScale : 0f);
        s.SetLinearDamping(DampingRate(props.LinearDampingPerStep, _dt, _substeps));
        s.SetAngularDamping(DampingRate(props.AngularDampingPerStep, _dt, _substeps));
        s.SetMotionQuality(props.Simulated && props.Ccd ? MotionQuality.LinearCast : MotionQuality.Discrete);
        s.SetAllowedDOFs(AllowedDOFs.All);
        s.SetOverrideMassProperties(OverrideMassProperties.MassAndInertiaProvided);
        s.SetMassPropertiesOverride(_unitMass);
        var body = _bi.CreateBody(s);
        if (body.IsNull())
        {
            _layers.Release(pair);
            _bodies.Free(slot);
            throw new InvalidOperationException(
                $"The Jolt physics space is full ({_settings.Jolt.MaxBodies} bodies); raise physics.3d.jolt.maxBodies.");
        }

        slot.Body = body;
        slot.Id = body.GetID();
        _bodies.Map(slot);
        _bi.AddBody(slot.Id, desc.Kind == PhysicsBodyKind.Static ? Activation.DontActivate : Activation.Activate);
        return slot.Handle;
    }

    public override void DestroyBody(BackendBody body)
    {
        var slot = _bodies.Get(body);
        ForgetContacts(slot);
        _bi.RemoveBody(slot.Id);
        _bi.DestroyBody(slot.Id);
        ReleaseShapes(slot);
        _layers.Release(slot.PairIndex);
        _bodies.Free(slot);
    }

    // Task 7 replaces this with JoltContactTracker.Forget.
    private void ForgetContacts(JoltBody slot)
    {
        foreach (var other in slot.Contacts.Keys)
            _bodies.At(other)?.Contacts.Remove(slot.Index);
        slot.Contacts.Clear();
    }

    public override void SetKind(BackendBody body, PhysicsBodyKind kind)
    {
        var slot = _bodies.Get(body);
        if (slot.Kind == kind)
            return;
        slot.Kind = kind;
        if (kind == PhysicsBodyKind.Dynamic && !slot.AllLocked)
            ApplyMass(slot, null); // Jolt needs valid mass properties before a body becomes dynamic
        _bi.SetMotionType(slot.Id, MotionTypeOf(slot), kind == PhysicsBodyKind.Static ? Activation.DontActivate : Activation.Activate);
        _bi.SetObjectLayer(slot.Id, slot.ObjectLayer); // static ↔ moving broad phase
    }

    public override void SetShapes(BackendBody body, ReadOnlySpan<ShapeDescription3D> shapes, float dynamicMass, List<ShapeError3D> errors)
    {
        var slot = _bodies.Get(body);
        _children.Clear();
        for (var i = 0; i < shapes.Length; i++)
        {
            try
            {
                if (_shapes.Acquire(shapes[i]) is { } entry)
                    _children.Add(new JoltChild(entry, shapes[i].Translation, entry.Rotation, i));
            }
            catch (Exception e) when (e is JoltException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                errors.Add(new ShapeError3D(i, e.Message));
            }
        }

        var root = BuildRoot(_children, out var owned, out var compound);
        _bi.SetShape(slot.Id, root, false, slot.Kind == PhysicsBodyKind.Static ? Activation.DontActivate : Activation.Activate);
        ReleaseShapes(slot); // the body holds its own reference to the new root now
        slot.Root = root;
        slot.RootOwned = owned;
        slot.Compound = compound;
        slot.Children.AddRange(_children);
        _children.Clear();
        slot.Mass = dynamicMass;
        if (slot.Kind == PhysicsBodyKind.Dynamic && dynamicMass > 0f && !slot.AllLocked)
            ApplyMass(slot, errors);
    }

    private Shape BuildRoot(List<JoltChild> children, out bool owned, out StaticCompoundShape compound)
    {
        compound = default;
        owned = children.Count > 0;
        if (children.Count == 0)
            return _empty;
        if (children.Count == 1)
        {
            var only = children[0];
            if (only.Translation == Vector3.Zero && only.Rotation == Quaternion.Identity)
            {
                owned = false; // the cache's shape itself
                return only.Entry.Shape;
            }

            Ledger.Created(JoltNativeLedger.Shape);
            return RotatedTranslatedShape.Create(only.Translation.ToJolt(), only.Rotation.ToJolt(), only.Entry.Shape);
        }

        using var settings = StaticCompoundShapeSettings.Create();
        foreach (var child in children)
            settings.AddShape(child.Translation.ToJolt(), child.Rotation.ToJolt(), child.Entry.Shape, (uint)child.Description);
        var root = settings.CreateShape();
        Ledger.Created(JoltNativeLedger.Shape);
        compound = root.AsStaticCompoundShape();
        return root;
    }

    private void ReleaseShapes(JoltBody slot)
    {
        foreach (var child in slot.Children)
            _shapes.Release(child.Entry);
        slot.Children.Clear();
        if (slot.RootOwned)
        {
            slot.Root.Dispose();
            Ledger.Disposed(JoltNativeLedger.Shape);
        }

        slot.Root = _empty;
        slot.RootOwned = false;
        slot.Compound = default;
    }

    private void ApplyMass(JoltBody slot, List<ShapeError3D>? errors)
    {
        var dofs = (AllowedDOFs)(0b11_1111 & ~(int)(slot.Properties.Simulated ? slot.Properties.AxisLocks : AxisLock.None));
        using var props = slot.Root.GetMassProperties();
        var mass = slot.Mass > 0f ? slot.Mass : 1f;
        var computed = props.GetMass();
        if (slot.Children.Count > 0 && computed > 0f && float.IsFinite(computed)) // no children: EmptyShape, whose mass means nothing
        {
            props.ScaleToMass(mass);
        }
        else
        {
            errors?.Add(new ShapeError3D(-1, "the shapes have no volume"));
            props.SetMass(mass);
            props.SetInertia(float4x4.Identity);
        }

        slot.Body.GetMotionProperties().SetMassProperties(dofs == AllowedDOFs.None ? AllowedDOFs.All : dofs, props);
    }

    private void ApplyDamping(JoltBody slot)
    {
        var motion = slot.Body.GetMotionProperties();
        motion.SetLinearDamping(DampingRate(slot.Properties.LinearDampingPerStep, _dt, _substeps));
        motion.SetAngularDamping(DampingRate(slot.Properties.AngularDampingPerStep, _dt, _substeps));
    }

    public override void SetFilter(BackendBody body, uint layer, uint mask)
    {
        var slot = _bodies.Get(body);
        if (slot.Layer == layer && slot.Mask == mask)
            return;
        var pair = _layers.Acquire(layer, mask); // before the release: never hand the same index back mid-change
        _layers.Release(slot.PairIndex);
        slot.Layer = layer;
        slot.Mask = mask;
        slot.PairIndex = pair;
        _bi.SetObjectLayer(slot.Id, slot.ObjectLayer);
    }

    public override void SetProperties(BackendBody body, in BodyProperties3D props)
    {
        var slot = _bodies.Get(body);
        if (slot.IsArea)
            return;
        var dofsChanged = slot.Properties.AxisLocks != props.AxisLocks || slot.Properties.Simulated != props.Simulated;
        slot.Properties = props;
        var allLocked = IsAllLocked(props);
        if (allLocked != slot.AllLocked)
        {
            slot.AllLocked = allLocked;
            if (slot.Kind == PhysicsBodyKind.Dynamic)
            {
                if (!allLocked)
                    ApplyMass(slot, null);
                _bi.SetMotionType(slot.Id, MotionTypeOf(slot), Activation.Activate);
                if (allLocked)
                    _bi.SetLinearAndAngularVelocity(slot.Id, default, default);
            }
        }

        if (slot.Kind == PhysicsBodyKind.Static)
            return;
        _bi.SetGravityFactor(slot.Id, props.Simulated ? props.GravityScale : 0f);
        ApplyDamping(slot);
        _bi.SetMotionQuality(slot.Id, props.Simulated && props.Ccd ? MotionQuality.LinearCast : MotionQuality.Discrete);
        slot.Body.SetAllowSleeping(!props.Simulated || props.CanSleep);
        if (dofsChanged && slot.Kind == PhysicsBodyKind.Dynamic && !slot.AllLocked)
            ApplyMass(slot, null);
    }

    public override void SetMaterial(BackendBody body, float friction, float bounce)
    {
        var slot = _bodies.Get(body);
        _bi.SetFriction(slot.Id, friction);
        _bi.SetRestitution(slot.Id, bounce);
    }

    // ---- state ---------------------------------------------------------------------------------------------------

    public override void GetPose(BackendBody body, out Vector3 position, out Quaternion rotation)
    {
        _bi.GetPositionAndRotation(_bodies.Get(body).Id, out var p, out var q);
        position = p.FromJolt();
        rotation = q.FromJolt();
    }

    public override void SetPose(BackendBody body, Vector3 position, Quaternion rotation) =>
        _bi.SetPositionAndRotation(_bodies.Get(body).Id, position.ToJolt(), rotation.ToJolt(), Activation.DontActivate);

    public override void Teleport(BackendBody body, Vector3 position, Quaternion rotation)
    {
        var slot = _bodies.Get(body);
        _bi.SetPositionAndRotation(slot.Id, position.ToJolt(), rotation.ToJolt(),
            slot.Kind == PhysicsBodyKind.Static ? Activation.DontActivate : Activation.Activate);
    }

    public override void MoveKinematic(BackendBody body, Vector3 position, Quaternion rotation, float dt) =>
        _bi.MoveKinematic(_bodies.Get(body).Id, position.ToJolt(), rotation.ToJolt(), dt);

    public override Vector3 GetLinearVelocity(BackendBody body) => _bi.GetLinearVelocity(_bodies.Get(body).Id).FromJolt();

    public override void SetLinearVelocity(BackendBody body, Vector3 velocity)
    {
        var slot = _bodies.Get(body);
        if (!slot.AllLocked && slot.Kind != PhysicsBodyKind.Static)
            _bi.SetLinearVelocity(slot.Id, velocity.ToJolt());
    }

    public override Vector3 GetAngularVelocity(BackendBody body) => _bi.GetAngularVelocity(_bodies.Get(body).Id).FromJolt();

    public override void SetAngularVelocity(BackendBody body, Vector3 velocity)
    {
        var slot = _bodies.Get(body);
        if (!slot.AllLocked && slot.Kind != PhysicsBodyKind.Static)
            _bi.SetAngularVelocity(slot.Id, velocity.ToJolt());
    }

    public override void ApplyForce(BackendBody body, Vector3 force, Vector3? position)
    {
        var slot = _bodies.Get(body);
        if (slot.AllLocked)
            return; // held kinematic: forces do not apply
        if (position is { } at)
            _bi.AddForce(slot.Id, force.ToJolt(), at.ToJolt(), Activation.Activate);
        else
            _bi.AddForce(slot.Id, force.ToJolt(), Activation.Activate);
    }

    public override void ApplyImpulse(BackendBody body, Vector3 impulse, Vector3? position)
    {
        var slot = _bodies.Get(body);
        if (slot.AllLocked)
            return;
        if (position is { } at)
            _bi.AddImpulse(slot.Id, impulse.ToJolt(), at.ToJolt());
        else
            _bi.AddImpulse(slot.Id, impulse.ToJolt());
    }

    public override void ApplyTorque(BackendBody body, Vector3 torque)
    {
        var slot = _bodies.Get(body);
        if (!slot.AllLocked)
            _bi.AddTorque(slot.Id, torque.ToJolt(), Activation.Activate);
    }

    public override bool IsSleeping(BackendBody body) => !_bi.IsActive(_bodies.Get(body).Id);

    public override void Wake(BackendBody body) => _bi.ActivateBody(_bodies.Get(body).Id);

    public override void WakeTouching(BackendBody body)
    {
        foreach (var other in _bodies.Get(body).Contacts.Keys)
        {
            if (_bodies.At(other) is { Kind: not PhysicsBodyKind.Static } slot)
                _bi.ActivateBody(slot.Id);
        }
    }

    // Jolt re-evaluates every active pair against the new object layer in the next step; drop the cached manifolds too.
    public override void ClearContacts(BackendBody body) => _bi.InvalidateContactCache(_bodies.Get(body).Id);

    // ---- scans, queries and character primitives: Tasks 5, 7 and 8 -----------------------------------------------

    public override void CollectTouching(BackendBody body, List<BackendBody> others) => throw new NotSupportedException("Jolt contact sets arrive in B3 Task 7.");

    public override bool CollectOverlaps(BackendBody area, List<BackendBody> others) => throw new NotSupportedException("Jolt area overlaps arrive in B3 Task 7.");

    public override bool RayCast(Vector3 from, Vector3 motion, uint mask, BackendBody exclude, bool hitFromInside, out BackendHit3D hit) =>
        throw new NotSupportedException("Jolt queries arrive in B3 Task 5.");

    public override bool ShapeCast(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, Vector3 motion, uint mask, BackendBody exclude,
        out BackendHit3D hit) => throw new NotSupportedException("Jolt queries arrive in B3 Task 5.");

    public override void IntersectShape(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, uint mask, BackendBody exclude,
        List<BackendBody> output) => throw new NotSupportedException("Jolt queries arrive in B3 Task 5.");

    public override void IntersectPoint(Vector3 point, uint mask, BackendBody exclude, List<BackendBody> output) =>
        throw new NotSupportedException("Jolt queries arrive in B3 Task 5.");

    public override bool CastBody(BackendBody body, Vector3 position, Quaternion rotation, Vector3 motion, out BackendHit3D hit) =>
        throw new NotSupportedException("Jolt character queries arrive in B3 Task 8.");

    public override int GetShapeCount(BackendBody body) => _bodies.Get(body).Children.Count;

    public override void CollectNearbyShapes(BackendBody body, Vector3 position, Quaternion rotation, float expand, List<BackendShapeRef> output) =>
        throw new NotSupportedException("Jolt character queries arrive in B3 Task 8.");

    public override bool Penetration(BackendBody body, int shape, Vector3 position, Quaternion rotation, BackendShapeRef other, float maxDistance,
        out BackendContact3D contact) => throw new NotSupportedException("Jolt character queries arrive in B3 Task 8.");

    public override bool Distance(BackendBody body, Vector3 position, Quaternion rotation, BackendBody other, out float distance, out Vector3 normal) =>
        throw new NotSupportedException("Jolt character queries arrive in B3 Task 8.");

    // ---- teardown ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The spec's order (the system holds raw pointers to the listener and the filters; handles have no finalizers):
    /// bodies → physics system → contact listener → filters and query settings → job systems → temp allocator → shapes
    /// and creation helpers. Idempotent. Debug builds log any handle left over (<see cref="Ledger"/>).
    /// </summary>
    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ids.Clear();
        for (var i = 1; i < _bodies.Capacity; i++)
        {
            if (_bodies.At(i) is { } slot)
                _ids.Add(slot.Id);
        }

        if (_ids.Count > 0)
        {
            _bi.RemoveBodies(CollectionsMarshal.AsSpan(_ids));
            _bi.DestroyBodies(CollectionsMarshal.AsSpan(_ids));
        }

        for (var i = 1; i < _bodies.Capacity; i++)
        {
            if (_bodies.At(i) is { } slot)
                ReleaseShapes(slot);
        }

        _bodies.Clear();
        _system.Dispose();
        Ledger.Disposed(JoltNativeLedger.World);
        // Task 7: the contact listener. Task 5: the query filters and settings.
        _pairFilter.Dispose();
        Ledger.Disposed(JoltNativeLedger.Filter);
        if (!_pool.IsNull())
        {
            _pool.Dispose();
            Ledger.Disposed(JoltNativeLedger.JobSystem);
        }

        _single.Dispose();
        Ledger.Disposed(JoltNativeLedger.JobSystem);
        _temp.Dispose();
        Ledger.Disposed(JoltNativeLedger.Allocator);
        _shapes.Dispose();
        _empty.Dispose();
        _creation.Dispose();
        _unitMass.Dispose();
        Ledger.Disposed(JoltNativeLedger.Helper);
        Ledger.Disposed(JoltNativeLedger.Helper);
        Ledger.Disposed(JoltNativeLedger.Helper);
        if (Ledger.Outstanding != 0)
            Log.Error($"[Physics] Jolt backend disposed with native handles left: {Ledger.Describe()}.");
    }
}
```

- [ ] **Step 5: Run the backend tests** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltBackendTests"` → 20 pass (skipped on osx-x64). Then `just build` (0 warnings; if the compiler rejects `&JoltRuntime.CombineFriction` with CS8757 the binding's parameter has a calling-convention modifier after all: add `CallConvs = [typeof(CallConvCdecl)]` to the four `[UnmanagedCallersOnly]` attributes in `JoltRuntime` and update `JoltApiFacts`'s remark), physics suite and trace gate → pass.

- [ ] **Step 6: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Jolt/JoltBodies.cs MainframeEngine/Src/Physics/3D/Jolt/JoltBackend.cs Tests/MainframeEngine.Tests/Physics/Jolt/JoltBackendTests.cs
git commit -F- <<'EOF'
Add JoltBackend bodies, state, steps and the strict teardown order

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 5: Queries

**Files:**
- Create: `MainframeEngine/Src/Physics/3D/Jolt/JoltQueries.cs`, `Tests/MainframeEngine.Tests/Physics/Jolt/JoltQueryTests.cs`
- Modify: `MainframeEngine/Src/Physics/3D/Jolt/JoltBackend.cs` (a `_queries` field, its construction and disposal, the four query overrides)

**Interfaces:**
- Consumes: `NarrowPhaseQuery.CastRayClosest/CastShapeClosest/CollideShape/CollidePoint` (signatures in Task 1), `RRayCast {Origin, Direction}`, `RShapeCast {Shape, Scale, CenterOfMassStart, Direction, ShapeWorldBounds}`, `RayCastResult {BodyID, Fraction, SubShapeID2}`, `ShapeCastResult {ContactPointOn2, PenetrationAxis, PenetrationDepth, SubShapeID2, BodyID2, Fraction}`, `CollideShapeResult {ContactPointOn2, PenetrationAxis, PenetrationDepth, SubShapeID2, BodyID2}`, `CollidePointResult {BodyID}`, `RayCastSettings`, `ShapeCastSettings`, `CollideShapeSettings`, `ManagedObjectLayerFilter`, `ManagedBodyFilter`, `Body.GetWorldSpaceSurfaceNormal(uint, float3)`, `Shape.GetCenterOfMass/GetWorldSpaceBounds`.
- Produces: `internal sealed class JoltQueries : IDisposable` (`RayCast`, `ShapeCast`, `IntersectShape`, `IntersectPoint` with the contract's signatures plus `ref`-free helpers `Cast(Shape, Vector3, Quaternion, Vector3, out BackendHit3D)`, `Collide(Shape, in float4x4, float) → int`, `Results`, `UseMask(uint)`, `UsePairOf(JoltBody)`, `Exclude(BackendBody)`, `Only(JoltBody)`), and the `JoltBackend` overrides delegating to it.

| Engine | Jolt (as built) |
|---|---|
| `RayCast(…, hitFromInside)` | `CastRayClosest`, `RayCastSettings.TreatConvexAsSolid = hitFromInside` and `BackFaceModeConvex = IgnoreBackFaces` (so a ray from inside skips that convex shape); triangles' back faces ignored (Jitter2: `CullMode.BackFacing`); normal from `Body.GetWorldSpaceSurfaceNormal`, zero when a solid hit starts inside (fraction 0) |
| `ShapeCast` | `CastShapeClosest` of the query shape (scale 1, cached per resource), `ReturnDeepestPoint = true`; position = contact point on the hit body, normal = −normalised penetration axis |
| `IntersectShape` | `CollideShape`, `MaxSeparationDistance = 0`, each body once; the result span grows to the next power of two when `totalHits` exceeds it (the query reruns; an allocation only on growth) |
| `IntersectPoint` | `CollidePoint`, each body once, same growth |
| filters | `JoltRuntime.NoAreas` (areas never reported), one `ManagedObjectLayerFilter` (query mask, or "collides with this body's object layer") and one `ManagedBodyFilter` (exclude one id, or only one id) per space, reconfigured per query; their callback exceptions are rethrown after the query |

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Physics;

/// <summary>Jolt backend queries on their own (B3), mirroring <c>Jitter2BackendTests</c>' query facts.</summary>
public sealed class JoltQueryTests : IDisposable
{
    private readonly JoltBackend _backend;
    private readonly List<ShapeDescription3D> _shapes = [];
    private readonly List<ShapeError3D> _errors = [];

    public JoltQueryTests()
    {
        RawJoltWorld.SkipUnlessAvailable();
        _backend = new JoltBackend(new PhysicsSettings3D { MultiThreaded = false });
    }

    public void Dispose() => _backend?.Dispose();

    private BackendBody Add(PhysicsBodyKind kind, Vector3 position, uint layer = 1, uint mask = 1, params Shape3D[] shapes)
    {
        var body = JoltBackendTests.Create(_backend, kind, position, layer, mask);
        foreach (var shape in shapes)
            shape.Describe(_shapes, Basis.Identity, Vector3.Zero);
        _backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), kind == PhysicsBodyKind.Dynamic ? 1f : 0f, _errors);
        _shapes.Clear();
        return body;
    }

    private BackendBody Floor() => Add(PhysicsBodyKind.Static, new Vector3(0, -0.5f, 0), shapes: new BoxShape3D { Size = new Vector3(40, 1, 40) });

    [Fact]
    public void RaysReportBodyAndShapeIndexAndHonourMaskAndExclude()
    {
        var floor = Floor();
        Assert.True(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 1, default, true, out var hit));
        Assert.Equal(floor, hit.Body);
        Assert.Equal(0, hit.ShapeIndex);
        Assert.Equal(0f, hit.Position.Y, 3);
        Assert.True(Vector3.Distance(Vector3.UnitY, hit.Normal) < 1e-4f, $"{hit.Normal}");
        Assert.Equal(0.5f, hit.Fraction, 3);
        Assert.False(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 2, default, true, out _));
        Assert.False(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), 1, floor, true, out _));

        var compound = JoltBackendTests.Create(_backend, PhysicsBodyKind.Static, new Vector3(5, 2, 0));
        new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, new Vector3(1, 0, 1)] }.Describe(_shapes, Basis.Identity, Vector3.Zero); // fails
        new BoxShape3D().Describe(_shapes, Basis.Identity, new Vector3(-1, 0, 0));
        new SphereShape3D().Describe(_shapes, Basis.Identity, new Vector3(1, 0, 0));
        _backend.SetShapes(compound, CollectionsMarshal.AsSpan(_shapes), 0f, _errors);
        _shapes.Clear();
        Assert.True(_backend.RayCast(new Vector3(6, 5, 0), new Vector3(0, -10, 0), 1, default, true, out hit));
        Assert.Equal((compound, 2), (hit.Body, hit.ShapeIndex)); // description index, not compound child index
        Assert.True(_backend.RayCast(new Vector3(4, 5, 0), new Vector3(0, -10, 0), 1, default, true, out hit));
        Assert.Equal((compound, 1), (hit.Body, hit.ShapeIndex));
    }

    [Fact]
    public void RaysFromInsideAConvexShapeHitItAtTheOriginOrSkipIt()
    {
        var floor = Floor();
        var box = Add(PhysicsBodyKind.Static, new Vector3(0, 2, 0), shapes: new BoxShape3D());
        Assert.True(_backend.RayCast(new Vector3(0, 2, 0), new Vector3(0, -10, 0), uint.MaxValue, default, true, out var hit));
        Assert.Equal((box, 0f, Vector3.Zero), (hit.Body, hit.Fraction, hit.Normal));
        Assert.True(_backend.RayCast(new Vector3(0, 2, 0), new Vector3(0, -10, 0), uint.MaxValue, default, false, out hit));
        Assert.Equal(floor, hit.Body);
        Assert.Equal(0f, hit.Position.Y, 3);
    }

    [Fact]
    public void QueriesNeverReportAreas()
    {
        Add(PhysicsBodyKind.Area, Vector3.Zero, shapes: new BoxShape3D { Size = new Vector3(4) });
        Assert.False(_backend.RayCast(new Vector3(0, 5, 0), new Vector3(0, -10, 0), uint.MaxValue, default, true, out _));
        var output = new List<BackendBody>();
        _backend.IntersectPoint(Vector3.Zero, uint.MaxValue, default, output);
        _backend.IntersectShape(new SphereShape3D().DescribeQuery(), Vector3.Zero, Quaternion.Identity, uint.MaxValue, default, output);
        Assert.Empty(output);
    }

    [Fact]
    public void ShapeCastsReportTheFractionPointAndSurfaceNormal()
    {
        var floor = Floor();
        Assert.True(_backend.ShapeCast(new SphereShape3D { Radius = 0.5f }.DescribeQuery(), new Vector3(0, 5, 0), Quaternion.Identity,
            new Vector3(0, -10, 0), 1, default, out var hit));
        Assert.Equal(floor, hit.Body);
        Assert.Equal(0.45f, hit.Fraction, 2);
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitY) > 0.999f, $"{hit.Normal}");
        Assert.Equal(0f, hit.Position.Y, 2);
    }

    [Fact]
    public void IntersectShapeAndPointReturnEachBodyOnceEvenPastTheResultBuffer()
    {
        var faces = new List<Vector3>();
        for (var i = 0; i < 200; i++) // 200 overlapping triangles in one mesh body: one body, many hits
            faces.AddRange([new(-1, i * 0.001f, -1), new(-1, i * 0.001f, 1), new(1, i * 0.001f, -1)]);
        var mesh = Add(PhysicsBodyKind.Static, Vector3.Zero, shapes: new ConcavePolygonShape3D { Faces = [.. faces] });
        var box = Add(PhysicsBodyKind.Static, new Vector3(0.75f, 0, 0), shapes: new BoxShape3D());
        var output = new List<BackendBody>();
        _backend.IntersectShape(new SphereShape3D { Radius = 0.5f }.DescribeQuery(), new Vector3(0, 0.1f, 0), Quaternion.Identity, uint.MaxValue, default, output);
        Assert.Equal([mesh, box], output.Order());
        output.Clear();
        _backend.IntersectPoint(new Vector3(0.75f, 0, 0), uint.MaxValue, default, output);
        Assert.Equal([box], output);
    }

    [Fact]
    public void QueryShapesAreCachedUntilTheResourceChangesAndQueriesDoNotAllocate()
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
        void Queries()
        {
            _backend.RayCast(new Vector3(0.3f, 10, 0.3f), new Vector3(0, -20, 0), 1, default, true, out _);
            _backend.ShapeCast(query, new Vector3(0, 5, 0), Quaternion.Identity, new Vector3(0, -10, 0), 1, default, out _);
            output.Clear();
            _backend.IntersectShape(query, new Vector3(0, 0.5f, 0), Quaternion.Identity, 1, default, output);
            _backend.IntersectPoint(new Vector3(0, -0.25f, 0), 1, default, output);
        }

        for (var i = 0; i < 3; i++)
            Queries(); // warm up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            Queries();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Single(output);
    }
}
```

- [ ] **Step 2: Run them to see them fail** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltQueryTests"` → `NotSupportedException: Jolt queries arrive in B3 Task 5.`

- [ ] **Step 3: Write `JoltQueries.cs`**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace MainframeEngine;

/// <summary>
/// One space's Jolt queries (B3): rays, shape casts, overlaps and points for <see cref="PhysicsDirectSpaceState3D"/>,
/// the shape queries contact rebuilds use (Task 7) and the character primitives (Task 8). One layer filter and one body
/// filter, reconfigured per query (no allocation); result spans owned here and grown only when a query overflows them.
/// Main thread only (Jolt runs these filters on the calling thread).
/// </summary>
internal sealed class JoltQueries : IDisposable
{
    private static readonly float3 One = new(1f);
    private readonly NarrowPhaseQuery _query;
    private readonly JoltBodyTable _bodies;
    private readonly JoltShapeCache _shapes;
    private readonly JoltNativeLedger _ledger;
    private readonly LayerFilter _layer;
    private readonly IdFilter _id;
    private readonly RayCastSettings _solid;
    private readonly RayCastSettings _hollow;
    private readonly ShapeCastSettings _cast;
    private readonly CollideShapeSettings _collide;
    private CollideShapeResult[] _results = new CollideShapeResult[64];
    private CollidePointResult[] _points = new CollidePointResult[64];

    public JoltQueries(NarrowPhaseQuery query, JoltBodyTable bodies, JoltLayerTable layers, JoltShapeCache shapes, JoltNativeLedger ledger)
    {
        _query = query;
        _bodies = bodies;
        _shapes = shapes;
        _ledger = ledger;
        _layer = new LayerFilter(layers);
        _id = new IdFilter();
        _ledger.Created(JoltNativeLedger.Filter);
        _ledger.Created(JoltNativeLedger.Filter);
        _solid = RayCastSettings.Create();
        _solid.SetTreatConvexAsSolid(true);
        _solid.SetBackFaceModeTriangles(BackFaceMode.IgnoreBackFaces);
        _hollow = RayCastSettings.Create();
        _hollow.SetTreatConvexAsSolid(false);
        _hollow.SetBackFaceModeConvex(BackFaceMode.IgnoreBackFaces);
        _hollow.SetBackFaceModeTriangles(BackFaceMode.IgnoreBackFaces);
        _cast = ShapeCastSettings.Create();
        _cast.SetReturnDeepestPoint(true);
        _cast.SetCollectFacesMode(CollectFacesMode.NoFaces);
        _collide = CollideShapeSettings.Create();
        _collide.SetCollectFacesMode(CollectFacesMode.NoFaces);
        for (var i = 0; i < 4; i++)
            _ledger.Created(JoltNativeLedger.Settings);
    }

    /// <summary>The results of the last <see cref="Collide"/>.</summary>
    public ReadOnlySpan<CollideShapeResult> Results => _results;

    // ---- filter set-up --------------------------------------------------------------------------------------------

    public void UseMask(uint mask) => _layer.Mask(mask);

    public void UsePairOf(JoltBody body) => _layer.Pair(body.ObjectLayer);

    public void Exclude(BackendBody body) => _id.Exclude(_bodies.Find(body)?.Id ?? JoltApiFacts.InvalidBodyId);

    public void Exclude(JoltBody body) => _id.Exclude(body.Id);

    public void Only(JoltBody body) => _id.Only(body.Id);

    private void ThrowIfCallbacksFailed()
    {
        var e = _layer.TakeCallbackException() ?? _id.TakeCallbackException() ?? JoltRuntime.TakeSharedCallbackException();
        if (e is not null)
            throw new InvalidOperationException("A Jolt query filter threw; see the inner exception.", e);
    }

    // ---- primitives -----------------------------------------------------------------------------------------------

    /// <summary>The closest hit of <paramref name="shape"/> swept along <paramref name="motion"/> (filters set by the caller).</summary>
    public bool Cast(Shape shape, Vector3 position, Quaternion rotation, Vector3 motion, out BackendHit3D hit)
    {
        hit = default;
        var start = JoltMath.CenterOfMassTransform(position, rotation, shape.GetCenterOfMass().FromJolt());
        var cast = new RShapeCast
        {
            Shape = shape,
            Scale = One,
            CenterOfMassStart = start,
            Direction = motion.ToJolt(),
            ShapeWorldBounds = shape.GetWorldSpaceBounds(in start, One),
        };
        var found = _query.CastShapeClosest(in cast, _cast, default, out var result, JoltRuntime.NoAreas, _layer, _id);
        ThrowIfCallbacksFailed();
        if (!found || _bodies.FromId(result.BodyID2) is not { } slot)
            return false;
        hit.Body = slot.Handle;
        hit.ShapeIndex = slot.ShapeIndexOf(result.SubShapeID2);
        hit.Fraction = result.Fraction;
        hit.Position = result.ContactPointOn2.FromJolt();
        var axis = result.PenetrationAxis.FromJolt(); // from the cast shape towards the hit one
        hit.Normal = axis.LengthSquared() > 1e-12f ? -Vector3.Normalize(axis) : Vector3.Zero;
        return true;
    }

    /// <summary>
    /// Every sub-shape pair of <paramref name="shape"/> at <paramref name="centerOfMass"/> within
    /// <paramref name="maxSeparation"/> (filters set by the caller), in <see cref="Results"/>; returns how many.
    /// </summary>
    public int Collide(Shape shape, in float4x4 centerOfMass, float maxSeparation)
    {
        _collide.SetMaxSeparationDistance(maxSeparation);
        while (true)
        {
            var count = _query.CollideShape(shape, One, in centerOfMass, _collide, default, _results, out var total, JoltRuntime.NoAreas, _layer, _id);
            ThrowIfCallbacksFailed();
            if (total <= _results.Length)
                return (int)count;
            _results = new CollideShapeResult[(int)System.Numerics.BitOperations.RoundUpToPowerOf2(total)];
        }
    }

    // ---- PhysicsDirectSpaceState3D --------------------------------------------------------------------------------

    public bool RayCast(Vector3 from, Vector3 motion, uint mask, BackendBody exclude, bool hitFromInside, out BackendHit3D hit)
    {
        hit = default;
        UseMask(mask);
        Exclude(exclude);
        var ray = new RRayCast { Origin = from.ToJolt(), Direction = motion.ToJolt() };
        var found = _query.CastRayClosest(in ray, hitFromInside ? _solid : _hollow, out var result, JoltRuntime.NoAreas, _layer, _id);
        ThrowIfCallbacksFailed();
        if (!found || _bodies.FromId(result.BodyID) is not { } slot)
            return false;
        hit.Body = slot.Handle;
        hit.ShapeIndex = slot.ShapeIndexOf(result.SubShapeID2);
        hit.Fraction = result.Fraction;
        hit.Position = from + motion * result.Fraction;
        hit.Normal = hitFromInside && result.Fraction == 0f
            ? Vector3.Zero // started inside a solid shape
            : slot.Body.GetWorldSpaceSurfaceNormal(result.SubShapeID2, hit.Position.ToJolt()).FromJolt();
        return true;
    }

    public bool ShapeCast(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, Vector3 motion, uint mask, BackendBody exclude,
        out BackendHit3D hit)
    {
        var query = _shapes.QueryShape(shape);
        UseMask(mask);
        Exclude(exclude);
        return Cast(query, position, rotation, motion, out hit);
    }

    public void IntersectShape(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, uint mask, BackendBody exclude, List<BackendBody> output)
    {
        var query = _shapes.QueryShape(shape);
        UseMask(mask);
        Exclude(exclude);
        var com = JoltMath.CenterOfMassTransform(position, rotation, query.GetCenterOfMass().FromJolt());
        var count = Collide(query, in com, 0f);
        for (var i = 0; i < count; i++)
            AddOnce(output, _results[i].BodyID2);
    }

    public void IntersectPoint(Vector3 point, uint mask, BackendBody exclude, List<BackendBody> output)
    {
        UseMask(mask);
        Exclude(exclude);
        while (true)
        {
            var count = _query.CollidePoint(point.ToJolt(), _points, out var total, JoltRuntime.NoAreas, _layer, _id);
            ThrowIfCallbacksFailed();
            if (total > _points.Length)
            {
                _points = new CollidePointResult[(int)System.Numerics.BitOperations.RoundUpToPowerOf2(total)];
                continue;
            }

            for (var i = 0; i < count; i++)
                AddOnce(output, _points[i].BodyID);
            return;
        }
    }

    private void AddOnce(List<BackendBody> output, uint id)
    {
        if (_bodies.FromId(id) is { } slot && !output.Contains(slot.Handle))
            output.Add(slot.Handle);
    }

    public void Dispose()
    {
        _layer.Dispose();
        _id.Dispose();
        _ledger.Disposed(JoltNativeLedger.Filter);
        _ledger.Disposed(JoltNativeLedger.Filter);
        _solid.Dispose();
        _hollow.Dispose();
        _cast.Dispose();
        _collide.Dispose();
        for (var i = 0; i < 4; i++)
            _ledger.Disposed(JoltNativeLedger.Settings);
    }

    /// <summary>Object layers a query sees: in a mask (never areas), or colliding with one body's object layer.</summary>
    private sealed class LayerFilter(JoltLayerTable table) : ManagedObjectLayerFilter
    {
        private uint _mask;
        private ushort _pair;
        private bool _pairMode;

        public void Mask(uint mask)
        {
            _mask = mask;
            _pairMode = false;
        }

        public void Pair(ushort objectLayer)
        {
            _pair = objectLayer;
            _pairMode = true;
        }

        protected override bool ShouldCollide(ushort layer) => _pairMode ? table.Collides(_pair, layer) : table.InQueryMask(layer, _mask);
    }

    /// <summary>Bodies a query sees: all but one, or only one.</summary>
    private sealed class IdFilter : ManagedBodyFilter
    {
        private uint _id = JoltApiFacts.InvalidBodyId;
        private bool _only;

        public void Exclude(uint id)
        {
            _id = id;
            _only = false;
        }

        public void Only(uint id)
        {
            _id = id;
            _only = true;
        }

        protected override bool ShouldCollide(uint bodyID) => _only ? bodyID == _id : bodyID != _id;

        protected override bool ShouldCollideLocked(Body body) => true;
    }
}
```

- [ ] **Step 4: Wire it into `JoltBackend`.** Add the field `private readonly JoltQueries _queries;`, at the end of the
constructor `_queries = new JoltQueries(_system.GetNarrowPhaseQueryNoLock(), _bodies, _layers, _shapes, Ledger);`, in
`Dispose` replace the comment line `// Task 7: the contact listener. Task 5: the query filters and settings.` with
`// Task 7: the contact listener.` followed by `_queries.Dispose();`, add `internal JoltQueries Queries => _queries;` to the
test surface, and replace the four query overrides with:

```csharp
    public override bool RayCast(Vector3 from, Vector3 motion, uint mask, BackendBody exclude, bool hitFromInside, out BackendHit3D hit) =>
        _queries.RayCast(from, motion, mask, exclude, hitFromInside, out hit);

    public override bool ShapeCast(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, Vector3 motion, uint mask, BackendBody exclude,
        out BackendHit3D hit) => _queries.ShapeCast(shape, position, rotation, motion, mask, exclude, out hit);

    public override void IntersectShape(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, uint mask, BackendBody exclude,
        List<BackendBody> output) => _queries.IntersectShape(shape, position, rotation, mask, exclude, output);

    public override void IntersectPoint(Vector3 point, uint mask, BackendBody exclude, List<BackendBody> output) =>
        _queries.IntersectPoint(point, mask, exclude, output);
```

- [ ] **Step 5: Run** `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltQueryTests|FullyQualifiedName~JoltBackendTests"` → 26 pass; `just build`; physics suite; trace gate → pass. (`QueryShapesAreCachedUntilTheResourceChangesAndQueriesDoNotAllocate` reporting bytes means a binding call marshals; find it with `dotnet-counters` or by bisecting `Queries()` and route around it — e.g. a `Span` overload instead of an array — never relax the 0.)

- [ ] **Step 6: Commit, then PR 1**

```bash
git add MainframeEngine/Src/Physics/3D/Jolt/JoltQueries.cs MainframeEngine/Src/Physics/3D/Jolt/JoltBackend.cs Tests/MainframeEngine.Tests/Physics/Jolt/JoltQueryTests.cs
git commit -F- <<'EOF'
Add Jolt rays, shape casts and overlap queries with reusable filters

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
just build && dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false && just test && just format-check
```

**PR 1 boundary.** Ask the user to push `brutal/b3-jolt-backend` and open PR 1 ("BRUTAL B3 (1/2): Jolt backend core —
bodies, shapes and queries, not yet selectable"); the PR body lists Tasks 1–5, the facts table of Task 1 and the
deviations (pair table in place, shared broad-phase objects, baked scales, axis locks without re-creation). Once merged,
continue on a fresh `brutal/b3-jolt-backend-2` from `main`.

---

## PR 2 — signals, characters, both backends everywhere (Tasks 6–14)

### Task 6: Backend selection and the backend-aware harness

**Files:**
- Modify: `MainframeEngine/Src/Physics/3D/Backend/PhysicsBackend3D.cs` (`Create`, B2 Task 6)
- Modify: `MainframeEngine/Src/Physics/3D/PhysicsServer3D.cs` (`ResolveBackend` and `AddObject` from B2 Task 11; class doc)
- Modify: `MainframeEngine/Src/Physics/PhysicsSettings.cs` (doc comments of `PhysicsBackendKind.Jolt`, `PhysicsSettings3D.Backend`, `JoltPhysicsSettings`)
- Modify: `Tests/MainframeEngine.Tests/Physics/PhysicsHarness.cs`, `Tests/MainframeEngine.Tests/Physics/PhysicsBackendSelectionTests.cs` (B2 Task 11)

**Interfaces:**
- Consumes: `JoltRuntime.TryInitialize`, `JoltBackend(PhysicsSettings3D)`, `PhysicsSpace3D(SceneViewport, PhysicsSettings3D, PhysicsBackendKind)`, `PhysicsSpace3D.BackendKind`/`ObjectCount`/`Dispose`.
- Produces: `PhysicsBackend3D.Create(PhysicsBackendKind.Jolt, …) → new JoltBackend(settings)`; `internal string? PhysicsServer3D.ForceJoltUnavailableForTesting { get; set; }`; `PhysicsServer3D.ResolveBackend()` resolving Jolt; an empty world's space re-created when the resolved backend changes; test harness `PhysicsHarness3D(PhysicsBackendKind backend, PhysicsSettings3D? settings = null)`, `PhysicsHarness3D.Backend`, `.Tolerances`, `internal sealed record PhysicsTolerances(float RestingPenetration, float ContactStartDistance, bool SpheresScaleUniformly)` with `Jitter2`, `Jolt`, `For(PhysicsBackendKind)`.

- [ ] **Step 1: Write the failing tests** — in `PhysicsBackendSelectionTests` replace `SelectingJoltRunsJitter2AndWarnsOncePerServer` with the three tests below (keep `Jitter2IsTheDefaultBackend`):

```csharp
    [Fact]
    public void SelectingJoltRunsJoltSpacesWhereItIsAvailable()
    {
        RawJoltWorld.SkipUnlessAvailable();
        using var h = new PhysicsHarness3D(PhysicsBackendKind.Jolt);
        h.AddFloor();
        var viewport = h.Add(new SceneViewport { Name = "Sub" });
        viewport.AddChild(new StaticBody3D { Name = "Late" });
        var box = h.AddBox(new Vector3(0, 2, 0));
        h.RunSeconds(2f);
        Assert.All(h.Server.Spaces, s => Assert.Equal(PhysicsBackendKind.Jolt, s.BackendKind));
        Assert.InRange(box.Position.Y, 0.45f, 0.55f);
    }

    [Fact]
    public void SelectingJoltWhereItIsUnavailableRunsJitter2AndWarnsOncePerServer()
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            using var h = new PhysicsHarness3D(new PhysicsSettings3D { Backend = PhysicsBackendKind.Jolt, MultiThreaded = false });
            h.Server.ForceJoltUnavailableForTesting = "forced by a test";
            h.AddFloor();
            var viewport = h.Add(new SceneViewport { Name = "Sub" });
            viewport.AddChild(new StaticBody3D { Name = "Late" });
            var box = h.AddBox(new Vector3(0, 2, 0));
            h.RunSeconds(2f);

            Assert.Equal(2, h.Server.Spaces.Count);
            Assert.All(h.Server.Spaces, s => Assert.Equal(PhysicsBackendKind.Jitter2, s.BackendKind));
            Assert.InRange(box.Position.Y, 0.45f, 0.55f);
            Assert.Single(sink.Snapshot(), e => e.Message ==
                $"[Physics] Jolt is not available on {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier} (forced by a test); using Jitter2.");
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void ChangingTheBackendRecreatesAWorldsSpaceOnceItIsEmpty()
    {
        RawJoltWorld.SkipUnlessAvailable();
        using var h = new PhysicsHarness3D();
        var floor = h.AddFloor();
        h.Run(1);
        Assert.Equal(PhysicsBackendKind.Jitter2, h.Space.BackendKind);

        h.Server.Settings.Backend = PhysicsBackendKind.Jolt;
        var box = h.AddBox(new Vector3(0, 2, 0)); // the world still has a body: its space stays
        Assert.Equal(PhysicsBackendKind.Jitter2, h.Space.BackendKind);

        floor.Free();
        box.Free();
        h.AddFloor(); // the world is empty now: a Jolt space
        Assert.Equal(PhysicsBackendKind.Jolt, h.Space.BackendKind);
        Assert.Same(h.Tree.Root.World3D.PhysicsSpace, h.Space);
        Assert.Single(h.Server.Spaces);
        h.Run(1);
    }
```

- [ ] **Step 2: Run them to see them fail** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsBackendSelectionTests"` → build errors (`ForceJoltUnavailableForTesting`, the harness constructor).

- [ ] **Step 3: Implement.** `PhysicsBackend3D.Create`'s switch:

```csharp
        return kind switch
        {
            PhysicsBackendKind.Jitter2 => new Jitter2Backend(settings),
            PhysicsBackendKind.Jolt => new JoltBackend(settings),
            _ => throw new NotSupportedException($"The {kind} 3D physics backend is not part of this engine build."),
        };
```

`PhysicsServer3D`: the class doc's first sentence becomes "The 3D physics server: one `PhysicsSpace3D` per `World3D`,
created when the first collision object enters that world, each over the `PhysicsSettings3D.Backend` library (Jitter2,
or Jolt where its native library exists; ADR 0192)." and its remark "(`Src/Physics/3D/Jitter`; the packages are pinned)"
becomes "(`Src/Physics/3D/Jitter`, `Src/Physics/3D/Jolt`; the packages are pinned)". Replace B2's `ResolveBackend` and
`AddObject` with:

```csharp
    /// <summary>Test hook: Jolt counts as unavailable for this server, with this reason (per server: parallel tests are unaffected).</summary>
    internal string? ForceJoltUnavailableForTesting { get; set; }

    /// <summary>The backend new spaces get: the setting, or Jitter2 (one warning per server) where Jolt cannot run.</summary>
    internal PhysicsBackendKind ResolveBackend()
    {
        if (Settings.Backend != PhysicsBackendKind.Jolt)
            return Settings.Backend;
        var reason = ForceJoltUnavailableForTesting;
        if (reason is null && JoltRuntime.TryInitialize(out reason))
            return PhysicsBackendKind.Jolt;
        if (!_warnedJolt)
        {
            _warnedJolt = true;
            Log.Warning($"[Physics] Jolt is not available on {RuntimeInformation.RuntimeIdentifier} ({reason}); using Jitter2.");
        }

        return PhysicsBackendKind.Jitter2;
    }

    internal void AddObject(CollisionObject3D node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var viewport = node.GetViewport() ?? throw new InvalidOperationException($"'{node.Name}' is not inside a viewport.");
        var world = viewport.World3D;
        var backend = ResolveBackend();
        if (!_spaces.TryGetValue(world, out var space))
        {
            space = new PhysicsSpace3D(viewport, Settings, backend);
            _spaces.Add(world, space);
            _spaceList.Add(space);
        }
        else if (space.ObjectCount == 0 && space.BackendKind != backend)
        {
            // The backend setting changed while this world was empty: replace its space in place (the step loops index
            // _spaceList, so the slot keeps its position).
            var index = _spaceList.IndexOf(space);
            space.Dispose();
            space = new PhysicsSpace3D(viewport, Settings, backend);
            _spaces[world] = space;
            _spaceList[index] = space;
        }

        space.AddObject(node);
    }
```

`PhysicsSettings.cs` doc comments: `PhysicsBackendKind.Jolt` → "Jolt 5.5 through BRUTAL (`Ahwoo.BRUTAL.Jolt`; win-x64,
linux-x64, osx-arm64; elsewhere spaces fall back to Jitter2 with one warning)."; `PhysicsSettings3D.Backend` → "The library
new spaces run on (default Jitter2; `physics.3d.backend`). A world's space is re-created on a changed backend when an
object enters it while it is empty."; `JoltPhysicsSettings` summary → "Jolt-only 3D settings (`physics.3d.jolt`), read when
a Jolt space is created." and the enum's summary drops "Jolt arrives in B3; until then selecting it logs once and runs
Jitter2.".

`PhysicsHarness.cs`: add after the existing constructor:

```csharp
    private readonly PhysicsBackendKind? _requested;

    /// <summary>A harness on <paramref name="backend"/> (single-threaded unless <paramref name="settings"/> says otherwise); skips the test where Jolt cannot run.</summary>
    public PhysicsHarness3D(PhysicsBackendKind backend, PhysicsSettings3D? settings = null)
        : this(WithBackend(backend, settings))
    {
        _requested = backend;
    }

    private static PhysicsSettings3D WithBackend(PhysicsBackendKind backend, PhysicsSettings3D? settings)
    {
        if (backend == PhysicsBackendKind.Jolt)
            RawJoltWorld.SkipUnlessAvailable();
        settings ??= new PhysicsSettings3D { MultiThreaded = false };
        settings.Backend = backend;
        return settings;
    }

    public PhysicsBackendKind Backend => Server.Settings.Backend;

    /// <summary>The named differences of this harness's backend (never inline numbers in a shared test).</summary>
    public PhysicsTolerances Tolerances => PhysicsTolerances.For(Backend);
```

`Run` ends with `EnsureBackend();`, defined as:

```csharp
    // A harness asked for a backend must not silently test another one (a fallback would run a suite twice on Jitter2).
    private void EnsureBackend()
    {
        if (_requested is { } requested && Server.Spaces.Count > 0 && Server.Spaces[0].BackendKind != requested)
            throw new InvalidOperationException($"The harness asked for {requested}, but its space runs {Server.Spaces[0].BackendKind}.");
    }
```

and below the harness class:

```csharp
/// <summary>
/// Named per-backend differences the shared 3D suites allow (ADR 0192, B3), listed in the physics doc's Backends section.
/// </summary>
/// <param name="RestingPenetration">How far a resting body may sink into what it rests on, in metres (Jitter2's 1 cm allowed penetration; Jolt's 2 cm penetration slop).</param>
/// <param name="ContactStartDistance">How far apart two bodies are when a contact monitor starts reporting them, in metres (Jitter2: touching; Jolt: its speculative contact distance). Areas report overlaps only on both.</param>
/// <param name="SpheresScaleUniformly">Non-uniform scale on spheres and capsules: an ellipsoid on Jitter2 (any linear map), the largest axis on Jolt.</param>
internal sealed record PhysicsTolerances(float RestingPenetration, float ContactStartDistance, bool SpheresScaleUniformly)
{
    public static readonly PhysicsTolerances Jitter2 = new(0.0105f, 0f, false);

    public static readonly PhysicsTolerances Jolt = new(0.0205f, JoltApiFacts.SpeculativeContactDistance, true);

    public static PhysicsTolerances For(PhysicsBackendKind backend) => backend == PhysicsBackendKind.Jolt ? Jolt : Jitter2;
}
```

- [ ] **Step 4: Run** `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsBackendSelectionTests"` → 4 pass (2 skipped on osx-x64). Physics suite and trace gate → pass; `just build` → 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Backend/PhysicsBackend3D.cs MainframeEngine/Src/Physics/3D/PhysicsServer3D.cs MainframeEngine/Src/Physics/PhysicsSettings.cs Tests/MainframeEngine.Tests/Physics/PhysicsHarness.cs Tests/MainframeEngine.Tests/Physics/PhysicsBackendSelectionTests.cs
git commit -F- <<'EOF'
Select the Jolt backend per project with a Jitter2 fallback and a backend-aware test harness

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 7: Contacts and areas

**Files:**
- Create: `MainframeEngine/Src/Physics/3D/Jolt/JoltContacts.cs`, `Tests/MainframeEngine.Tests/Physics/Jolt/JoltContactTests.cs`
- Modify: `MainframeEngine/Src/Physics/3D/Jolt/JoltBackend.cs` (the tracker, `Step`'s drain, `DestroyBody`, `Dispose`, `CollectTouching`, `CollectOverlaps`)

**Interfaces:**
- Consumes: `ContactEventListener(uint, ContactEventMask)`, `.Drain`, `.Dropped`, `.Capacity`, `.Dispose`, `PhysicsSystem.SetContactListener(ContactListener)`, `ContactEvent {Kind, Body1, Body2}`, `JoltBodyTable.FromId/At`, `JoltQueries.UsePairOf/Exclude/Collide/Results`, `JoltLayerTable.Collides`.
- Produces: `internal sealed class JoltContactTracker : IDisposable` (`JoltContactTracker(int capacity)`, `Install(PhysicsSystem)`, `Drain(PhysicsSystem, JoltBodyTable)`, `static Forget(JoltBodyTable, JoltBody)`, `QueryMode`, `Capacity`); `JoltBackend.CollectTouching`/`CollectOverlaps` per the contract (`CollectOverlaps` false when the area has no shapes).

Design (reconciling the spec with B2's contract): the backend does not know which bodies are contact monitors (the
contract has no monitor flag; the space simply calls `CollectTouching` for them), so the tracker keeps, **for every body**,
other body index → number of touching sub-shape pairs, from every `Added` (+1 on both bodies) and `Removed` (−1, removed
at 0) event; events with an unknown id (a destroyed body's late `Removed`) are skipped. Areas are kinematic sensors, so
their sets come from the same events (the spec's "receiver lookup" is the per-body dictionary). `CollectTouching` lists
non-area partners, `CollectOverlaps` non-area partners its mask detects — both re-checked against the current layer rule,
so a filter change applies at once. **Overflow** (`Dropped` grew): the counts can no longer be trusted, so the space's
contact monitors and areas switch to shape queries for the rest of the space's life (`CollideShape` against the pair's
filter, `MaxSeparationDistance` = the speculative distance for monitors and 0 for areas — correct, slower), the
listener is replaced by one of double capacity (one allocation) and one warning is logged. This deviates from the spec's
"rebuild that step" because a rebuilt count cannot be reconciled with Jolt's later `Removed` events.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Physics;

/// <summary>Jolt contact sets and areas (B3): the backend alone, then the review-focus cases through the engine.</summary>
public sealed class JoltContactTests : IDisposable
{
    private const float Dt = 1f / 60f;
    private readonly List<ShapeDescription3D> _shapes = [];
    private readonly List<ShapeError3D> _errors = [];
    private readonly List<BackendBody> _others = [];
    private JoltBackend _backend;

    public JoltContactTests()
    {
        RawJoltWorld.SkipUnlessAvailable();
        _backend = new JoltBackend(new PhysicsSettings3D { MultiThreaded = false });
    }

    public void Dispose() => _backend?.Dispose();

    private BackendBody Add(PhysicsBodyKind kind, Vector3 position, Shape3D shape, uint layer = 1, uint mask = 1)
    {
        var body = JoltBackendTests.Create(_backend, kind, position, layer, mask);
        shape.Describe(_shapes, Basis.Identity, Vector3.Zero);
        _backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), kind == PhysicsBodyKind.Dynamic ? 1f : 0f, _errors);
        _shapes.Clear();
        return body;
    }

    private BackendBody Floor() => Add(PhysicsBodyKind.Static, new Vector3(0, -0.5f, 0), new BoxShape3D { Size = new Vector3(40, 1, 40) });

    private List<BackendBody> Touching(BackendBody body)
    {
        _others.Clear();
        _backend.CollectTouching(body, _others);
        return [.. _others.Distinct()];
    }

    private List<BackendBody>? Overlaps(BackendBody area)
    {
        _others.Clear();
        return _backend.CollectOverlaps(area, _others) ? [.. _others] : null;
    }

    private void Run(int steps)
    {
        for (var i = 0; i < steps; i++)
            _backend.Step(Dt);
    }

    [Fact]
    public void ContactsAreTrackedOnBothBodiesFromAddedAndRemovedEvents()
    {
        var floor = Floor();
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 0.6f, 0), new BoxShape3D());
        Run(30);
        Assert.Equal([floor], Touching(box));
        Assert.Equal([box], Touching(floor));
        _backend.Teleport(box, new Vector3(0, 10, 0), Quaternion.Identity);
        Run(2);
        Assert.Empty(Touching(box));
        Assert.Empty(Touching(floor));
    }

    [Fact]
    public void AreasSeeStaticKinematicAndDynamicBodiesByTheirMaskButNeverAreas()
    {
        var area = Add(PhysicsBodyKind.Area, Vector3.Zero, new BoxShape3D { Size = new Vector3(10) }, layer: 0, mask: 1);
        var wall = Add(PhysicsBodyKind.Static, new Vector3(2, 0, 0), new BoxShape3D());
        var hidden = Add(PhysicsBodyKind.Static, new Vector3(-2, 0, 0), new BoxShape3D(), layer: 8);
        var platform = Add(PhysicsBodyKind.Kinematic, new Vector3(0, 2, 0), new BoxShape3D());
        var character = Add(PhysicsBodyKind.Character, new Vector3(0, -2, 0), new CapsuleShape3D());
        var other = Add(PhysicsBodyKind.Area, Vector3.Zero, new BoxShape3D(), layer: 1, mask: 1);
        Run(1);
        Assert.Equal(new[] { wall, platform, character }.OrderBy(b => b.Index), Overlaps(area)!.OrderBy(b => b.Index));
        Assert.DoesNotContain(hidden, Overlaps(area)!);
        Assert.DoesNotContain(other, Overlaps(area)!);
        Assert.Empty(Touching(wall)); // a monitor never lists areas

        _backend.SetFilter(area, 0, 0); // applies at once
        Assert.Empty(Overlaps(area)!);
        Assert.Null(Overlaps(JoltBackendTests.Create(_backend, PhysicsBodyKind.Area, Vector3.Zero))); // no shapes
    }

    [Fact]
    public void DestroyingABodyDropsItFromEveryContactSetAndIgnoresItsLateRemovedEvent()
    {
        var floor = Floor();
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 0.5f, 0), new BoxShape3D());
        Run(5);
        Assert.Equal([box], Touching(floor));
        _backend.DestroyBody(box);
        Assert.Empty(Touching(floor));
        var next = Add(PhysicsBodyKind.Dynamic, new Vector3(20, 5, 0), new BoxShape3D()); // reuses the handle index
        Assert.Equal(box.Index, next.Index);
        Run(2); // the Removed event for the old body id arrives here and is skipped
        Assert.Empty(Touching(floor));
        Assert.Empty(Touching(next));
    }

    [Fact]
    public void ADroppedEventSwitchesTheSpaceToShapeQueriesWithTheSameSets()
    {
        _backend.Dispose();
        var settings = new PhysicsSettings3D { MultiThreaded = false };
        settings.Jolt.ContactEventCapacity = 1;
        _backend = new JoltBackend(settings);
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            var floor = Floor();
            var boxes = Enumerable.Range(0, 3).Select(i => Add(PhysicsBodyKind.Dynamic, new Vector3(i * 2, 0.5f, 0), new BoxShape3D())).ToArray();
            var area = Add(PhysicsBodyKind.Area, Vector3.Zero, new BoxShape3D { Size = new Vector3(3) });
            Run(5);
            Assert.True(_backend.Contacts.QueryMode);
            Assert.True(_backend.Contacts.Capacity >= 2u); // doubled (again if a later step overflowed too)
            foreach (var box in boxes)
                Assert.Contains(floor, Touching(box));
            Assert.Contains(boxes[0], Overlaps(area)!);
            Assert.Contains(floor, Overlaps(area)!);
            Assert.Single(sink.Snapshot(), e => e.Message.Contains("Jolt dropped", StringComparison.Ordinal));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void WakeTouchingWakesSleepingNeighbours()
    {
        var floor = Floor();
        var box = Add(PhysicsBodyKind.Dynamic, new Vector3(0, 0.5f, 0), new BoxShape3D());
        Run(240);
        Assert.True(_backend.IsSleeping(box));
        _backend.WakeTouching(floor);
        Assert.False(_backend.IsSleeping(box));
    }

    // ---- Review Focus 3, through the engine ---------------------------------------------------------------------

    [Fact]
    public void ABodyTeleportedIntoAnAreaAndFreedBeforeTheStepNeverEntersIt()
    {
        using var h = new PhysicsHarness3D(PhysicsBackendKind.Jolt);
        var area = h.AddArea(Vector3.Zero, new Vector3(4, 4, 4));
        var log = new List<string>();
        area.BodyEntered += n => log.Add("enter " + n.Name);
        area.BodyExited += n => log.Add("exit " + n.Name);
        var box = h.AddBox(new Vector3(20, 0, 0), 0.5f, "Box");
        box.GravityScale = 0;
        h.Run(2);
        box.Teleport(Transform3D.FromTrs(Vector3.Zero, Quaternion.Identity, Vector3.One));
        box.Free();
        h.Run(3);
        Assert.Empty(log);
        Assert.False(area.HasOverlappingBodies());
    }

    [Fact]
    public void ABodyFreedInItsEnterHandlerExitsOnceAndItsLateRemovedEventIsIgnored()
    {
        using var h = new PhysicsHarness3D(PhysicsBackendKind.Jolt);
        var area = h.AddArea(Vector3.Zero, new Vector3(4, 4, 4));
        var log = new List<string>();
        var box = h.AddBox(Vector3.Zero, 0.5f, "Box");
        box.GravityScale = 0;
        area.BodyEntered += n =>
        {
            log.Add("enter " + n.Name);
            n.Free();
        };
        area.BodyExited += n => log.Add("exit " + n.Name);
        h.Run(1);
        var next = h.AddBox(new Vector3(30, 0, 0), 0.5f, "Next"); // takes the freed backend handle
        next.GravityScale = 0;
        h.Run(3);
        Assert.Equal(["enter Box", "exit Box"], log);
        Assert.False(area.OverlapsBody(next));
    }
}
```

Add `internal JoltContactTracker Contacts => _contacts;` to `JoltBackend`'s test surface (Step 4).

- [ ] **Step 2: Run them to see them fail** → build errors (`JoltBackend.Contacts`), then `NotSupportedException: Jolt contact sets arrive in B3 Task 7.`

- [ ] **Step 3: Write `JoltContacts.cs`**

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal.JoltApi;

namespace MainframeEngine;

/// <summary>
/// The touching sets of one Jolt space (B3), from a <see cref="ContactEventListener"/> drained after every step: per body,
/// other body index → touching sub-shape pairs (<c>Added</c> +1, <c>Removed</c> −1 on both bodies; Jolt pairs every
/// <c>Added</c> with one <c>Removed</c>). Events naming a body that no longer exists are skipped. When the buffer
/// overflows the counts cannot be trusted: <see cref="QueryMode"/> turns on for the space's lifetime (monitors and areas
/// use shape queries), the listener is replaced with one of double capacity and one warning is logged. Main thread only;
/// the listener records natively from Jolt's workers.
/// </summary>
internal sealed class JoltContactTracker : IDisposable
{
    private ContactEventListener _listener;
    private ContactEvent[] _events;
    private uint _dropped;
    private bool _warned;

    public JoltContactTracker(int capacity)
    {
        _listener = new ContactEventListener((uint)capacity, ContactEventMask.AddedAndRemoved);
        _events = new ContactEvent[capacity];
    }

    /// <summary>Monitors and areas query their sets instead of trusting the event counts (after an overflow).</summary>
    public bool QueryMode { get; private set; }

    public uint Capacity => _listener.Capacity;

    public void Install(PhysicsSystem system) => system.SetContactListener(_listener);

    /// <summary>Applies the events of the last update (between updates only).</summary>
    public void Drain(PhysicsSystem system, JoltBodyTable bodies)
    {
        uint remaining;
        do
        {
            var count = _listener.Drain(_events, out remaining);
            for (var i = 0; i < count; i++)
                Apply(bodies, in _events[i]);
        }
        while (remaining > 0);

        var dropped = _listener.Dropped;
        if (dropped == _dropped)
            return;
        var lost = dropped - _dropped;
        QueryMode = true;
        var capacity = checked(_listener.Capacity * 2);
        var next = new ContactEventListener(capacity, ContactEventMask.AddedAndRemoved);
        system.SetContactListener(next);
        _listener.Dispose();
        _listener = next;
        _events = new ContactEvent[capacity];
        _dropped = 0;
        if (_warned)
            return;
        _warned = true;
        Log.Warning($"[Physics] Jolt dropped {lost} contact events in one step (physics.3d.jolt.contactEventCapacity); this space's contact " +
                    $"monitors and areas now use shape queries (correct, slower), and its capacity is now {capacity}.");
    }

    private static void Apply(JoltBodyTable bodies, in ContactEvent e)
    {
        if (bodies.FromId(e.Body1) is not { } a || bodies.FromId(e.Body2) is not { } b)
            return;
        switch (e.Kind)
        {
            case ContactEventKind.Added:
                CollectionsMarshal.GetValueRefOrAddDefault(a.Contacts, b.Index, out _)++;
                CollectionsMarshal.GetValueRefOrAddDefault(b.Contacts, a.Index, out _)++;
                break;
            case ContactEventKind.Removed:
                Decrement(a.Contacts, b.Index);
                Decrement(b.Contacts, a.Index);
                break;
        }
    }

    private static void Decrement(Dictionary<int, int> contacts, int other)
    {
        ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(contacts, other);
        if (Unsafe.IsNullRef(ref count))
            return;
        if (--count <= 0)
            contacts.Remove(other);
    }

    /// <summary>Takes a body out of every set (it is being destroyed; its late <c>Removed</c> events are skipped).</summary>
    public static void Forget(JoltBodyTable bodies, JoltBody slot)
    {
        foreach (var other in slot.Contacts.Keys)
            bodies.At(other)?.Contacts.Remove(slot.Index);
        slot.Contacts.Clear();
    }

    /// <summary>Dispose after the physics system (it holds the listener).</summary>
    public void Dispose() => _listener.Dispose();
}
```

- [ ] **Step 4: Wire it into `JoltBackend`.** Field `private readonly JoltContactTracker _contacts;`; in the constructor,
before `_bi = …`: `_contacts = new JoltContactTracker(jolt.ContactEventCapacity); _contacts.Install(_system); Ledger.Created(JoltNativeLedger.Listener);`;
test surface `internal JoltContactTracker Contacts => _contacts;`; at the end of `Step`: `_contacts.Drain(_system, _bodies);`;
in `DestroyBody` replace `ForgetContacts(slot);` with `JoltContactTracker.Forget(_bodies, slot);` and delete
`ForgetContacts`; in `Dispose` replace the line `// Task 7: the contact listener.` with
`_contacts.Dispose(); Ledger.Disposed(JoltNativeLedger.Listener);`. Replace the two scan overrides:

```csharp
    public override void CollectTouching(BackendBody body, List<BackendBody> others)
    {
        var slot = _bodies.Get(body);
        if (_contacts.QueryMode)
        {
            CollectByQuery(slot, JoltApiFacts.SpeculativeContactDistance, others);
            return;
        }

        foreach (var (index, _) in slot.Contacts)
        {
            if (_bodies.At(index) is { IsArea: false } other && _layers.Collides(slot.ObjectLayer, other.ObjectLayer))
                others.Add(other.Handle);
        }
    }

    public override bool CollectOverlaps(BackendBody area, List<BackendBody> others)
    {
        var slot = _bodies.Get(area);
        if (slot.Children.Count == 0)
            return false;
        if (_contacts.QueryMode)
        {
            CollectByQuery(slot, 0f, others);
            return true;
        }

        foreach (var (index, _) in slot.Contacts)
        {
            if (_bodies.At(index) is { IsArea: false } other && _layers.Collides(slot.ObjectLayer, other.ObjectLayer))
                others.Add(other.Handle);
        }

        return true;
    }

    // Query mode (after an overflow): what the body's shape at its pose touches under its own filter, each body once.
    private void CollectByQuery(JoltBody slot, float maxSeparation, List<BackendBody> others)
    {
        if (slot.Children.Count == 0)
            return;
        _bi.GetPositionAndRotation(slot.Id, out var position, out var rotation);
        _queries.UsePairOf(slot);
        _queries.Exclude(slot);
        var com = JoltMath.CenterOfMassTransform(position.FromJolt(), rotation.FromJolt(), slot.Root.GetCenterOfMass().FromJolt());
        var count = _queries.Collide(slot.Root, in com, maxSeparation);
        var results = _queries.Results;
        for (var i = 0; i < count; i++)
        {
            if (_bodies.FromId(results[i].BodyID2) is { } other && !others.Contains(other.Handle))
                others.Add(other.Handle);
        }
    }
```

- [ ] **Step 5: Run** `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~JoltContactTests|FullyQualifiedName~JoltBackendTests|FullyQualifiedName~JoltQueryTests"` → all pass; physics suite; trace gate; `just build`.

- [ ] **Step 6: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Jolt/JoltContacts.cs MainframeEngine/Src/Physics/3D/Jolt/JoltBackend.cs Tests/MainframeEngine.Tests/Physics/Jolt/JoltContactTests.cs
git commit -F- <<'EOF'
Track Jolt contacts and area overlaps from drained contact events, with a query-mode fallback on overflow

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 8: Character primitives

**Files:**
- Modify: `MainframeEngine/Src/Physics/3D/Jolt/JoltQueries.cs` (`DistanceProbe`, `CastBody`, `CollectNearbyShapes`, `Penetration`, `Distance`), `MainframeEngine/Src/Physics/3D/Jolt/JoltBackend.cs` (the four overrides)
- Create: `Tests/MainframeEngine.Tests/Physics/Jolt/JoltCharacterTests.cs`

**Interfaces:**
- Consumes: `JoltQueries.Cast/Collide/UseMask/Exclude/Only`, `JoltBody.Children/Root/Mask`, `JoltChild`, the contract's `CastBody`, `GetShapeCount`, `CollectNearbyShapes`, `Penetration`, `Distance`, `BackendShapeRef(Body, Shape)`, `BackendContact3D(Distance, Normal, Point)`.
- Produces: `JoltQueries.CastBody(JoltBody, Vector3, Quaternion, Vector3, out BackendHit3D)`, `.CollectNearbyShapes(JoltBody, Vector3, Quaternion, float, List<BackendShapeRef>)`, `.Penetration(JoltBody, int, Vector3, Quaternion, JoltBody, uint, float, out BackendContact3D)`, `.Distance(JoltBody, Vector3, Quaternion, JoltBody, out float, out Vector3)`, `const float DistanceProbe = 0.5f`.

| Engine primitive (B2 contract) | Jolt (as built) |
|---|---|
| `CastBody` | `CastShapeClosest` of the body's root shape (its compound) at the pose; the body's own mask, the body excluded, no areas; `ReturnDeepestPoint` so an already-touching start reports fraction 0 with the penetration axis |
| `GetShapeCount` | the body's built children (one per description built) |
| `CollectNearbyShapes` | `CollideShape` of the root at the pose with `MaxSeparationDistance = expand`, the body's mask, the body excluded: one `BackendShapeRef(other, SubShapeID2)` per distinct leaf (a mesh's triangles separately, as Jitter2's one `TriangleShape` per triangle) |
| `Penetration(body, shape, …, other, maxDistance)` | `CollideShape` of child `shape` at its world pose against only `other.Body`, `MaxSeparationDistance = maxDistance`; the deepest result whose `SubShapeID2` is `other.Shape`: distance = −depth, normal = the penetration axis (from the body's shape towards the other), point = the contact point on the other |
| `Distance` | the root against only `other`, `MaxSeparationDistance = DistanceProbe` (0.5 m, above every `SafeMargin + maxGap` the engine passes): the smallest signed distance and its axis |

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Physics;

/// <summary>The MoveAndSlide primitives on Jolt (B3), mirroring <c>Jitter2BackendTests.CharacterPrimitivesMeasureTheGapToTheFloor</c>.</summary>
public sealed class JoltCharacterTests : IDisposable
{
    private readonly JoltBackend _backend;
    private readonly List<ShapeDescription3D> _shapes = [];
    private readonly List<ShapeError3D> _errors = [];

    public JoltCharacterTests()
    {
        RawJoltWorld.SkipUnlessAvailable();
        _backend = new JoltBackend(new PhysicsSettings3D { MultiThreaded = false });
    }

    public void Dispose() => _backend?.Dispose();

    private BackendBody Add(PhysicsBodyKind kind, Vector3 position, Shape3D shape, uint layer = 1, uint mask = 1)
    {
        var body = JoltBackendTests.Create(_backend, kind, position, layer, mask);
        shape.Describe(_shapes, Basis.Identity, Vector3.Zero);
        _backend.SetShapes(body, CollectionsMarshal.AsSpan(_shapes), 0f, _errors);
        _shapes.Clear();
        return body;
    }

    private BackendBody Floor() => Add(PhysicsBodyKind.Static, new Vector3(0, -0.5f, 0), new BoxShape3D { Size = new Vector3(40, 1, 40) });

    private BackendBody Capsule(Vector3 at) => Add(PhysicsBodyKind.Character, at, new CapsuleShape3D { Radius = 0.4f, Height = 1.8f });

    [Fact]
    public void CharacterPrimitivesMeasureTheGapToTheFloor()
    {
        var floor = Floor();
        var start = new Vector3(0, 0.905f, 0); // a 1.8 m capsule 5 mm above the floor
        var body = Capsule(start);

        Assert.True(_backend.CastBody(body, start, Quaternion.Identity, new Vector3(0, -0.1f, 0), out var hit));
        Assert.Equal(floor, hit.Body);
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitY) > 0.999f, $"{hit.Normal}");
        Assert.Equal(0.05f, hit.Fraction, 2);

        var nearby = new List<BackendShapeRef>();
        _backend.CollectNearbyShapes(body, start, Quaternion.Identity, 0.02f, nearby);
        var shape = Assert.Single(nearby);
        Assert.Equal(floor, shape.Body);
        Assert.Equal(1, _backend.GetShapeCount(body));
        Assert.True(_backend.Penetration(body, 0, start, Quaternion.Identity, shape, 0.02f, out var contact));
        Assert.InRange(contact.Distance, 0.004f, 0.006f);
        Assert.True(Vector3.Dot(Vector3.Normalize(contact.Normal), -Vector3.UnitY) > 0.999f); // from the body towards the floor
        Assert.Equal(0f, contact.Point.Y, 3);

        Assert.True(_backend.Distance(body, start, Quaternion.Identity, floor, out var distance, out var normal));
        Assert.InRange(distance, 0.004f, 0.006f);
        Assert.True(Vector3.Dot(Vector3.Normalize(normal), -Vector3.UnitY) > 0.999f);
    }

    [Fact]
    public void ANearbyMeshIsReportedTriangleByTriangle()
    {
        var mesh = Add(PhysicsBodyKind.Static, Vector3.Zero, new ConcavePolygonShape3D
        {
            Faces = [new(-2, 0, -2), new(-2, 0, 2), new(2, 0, -2), new(2, 0, -2), new(-2, 0, 2), new(2, 0, 2)],
        });
        var start = new Vector3(0, 0.905f, 0); // over the shared diagonal: both triangles are near
        var body = Capsule(start);
        var nearby = new List<BackendShapeRef>();
        _backend.CollectNearbyShapes(body, start, Quaternion.Identity, 0.02f, nearby);
        Assert.Equal(2, nearby.Count);
        Assert.All(nearby, r => Assert.Equal(mesh, r.Body));
        Assert.NotEqual(nearby[0].Shape, nearby[1].Shape);
        foreach (var reference in nearby)
        {
            Assert.True(_backend.Penetration(body, 0, start, Quaternion.Identity, reference, 0.02f, out var contact));
            Assert.InRange(contact.Distance, 0.004f, 0.006f);
        }
    }

    [Fact]
    public void ACastStartingInsideReportsFractionZeroWithAnOutwardNormal()
    {
        Floor();
        var start = new Vector3(0, 0.85f, 0); // 5 cm into the floor
        var body = Capsule(start);
        Assert.True(_backend.CastBody(body, start, Quaternion.Identity, new Vector3(1, 0, 0), out var hit));
        Assert.Equal(0f, hit.Fraction);
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitY) > 0.99f, $"{hit.Normal}");
    }

    [Fact]
    public void CharacterQueriesUseTheBodysMaskAndSkipItselfAndAreas()
    {
        Floor(); // layer 1
        Add(PhysicsBodyKind.Area, new Vector3(0, 0.9f, 0), new BoxShape3D { Size = new Vector3(4) });
        var start = new Vector3(0, 0.905f, 0);
        var blind = Add(PhysicsBodyKind.Character, start, new CapsuleShape3D { Radius = 0.4f, Height = 1.8f }, layer: 2, mask: 2);
        Assert.False(_backend.CastBody(blind, start, Quaternion.Identity, new Vector3(0, -0.1f, 0), out _));
        var nearby = new List<BackendShapeRef>();
        _backend.CollectNearbyShapes(blind, start, Quaternion.Identity, 0.02f, nearby);
        Assert.Empty(nearby);
    }
}
```

- [ ] **Step 2: Run them to see them fail** → `NotSupportedException: Jolt character queries arrive in B3 Task 8.`

- [ ] **Step 3: Add to `JoltQueries`**

```csharp
    /// <summary>How far <see cref="Distance"/> looks (above any <c>SafeMargin + maxGap</c> the engine's settle passes).</summary>
    public const float DistanceProbe = 0.5f;

    public bool CastBody(JoltBody slot, Vector3 position, Quaternion rotation, Vector3 motion, out BackendHit3D hit)
    {
        hit = default;
        if (slot.Children.Count == 0)
            return false;
        UseMask(slot.Mask);
        Exclude(slot);
        return Cast(slot.Root, position, rotation, motion, out hit);
    }

    public void CollectNearbyShapes(JoltBody slot, Vector3 position, Quaternion rotation, float expand, List<BackendShapeRef> output)
    {
        if (slot.Children.Count == 0)
            return;
        UseMask(slot.Mask);
        Exclude(slot);
        var com = JoltMath.CenterOfMassTransform(position, rotation, slot.Root.GetCenterOfMass().FromJolt());
        var count = Collide(slot.Root, in com, expand);
        for (var i = 0; i < count; i++)
        {
            if (_bodies.FromId(_results[i].BodyID2) is not { } other)
                continue;
            var reference = new BackendShapeRef(other.Handle, unchecked((int)_results[i].SubShapeID2));
            if (!output.Contains(reference))
                output.Add(reference);
        }
    }

    public bool Penetration(JoltBody slot, int shape, Vector3 position, Quaternion rotation, JoltBody target, uint subShape, float maxDistance,
        out BackendContact3D contact)
    {
        contact = default;
        var child = slot.Children[shape];
        var leaf = child.Entry.Shape;
        var childRotation = rotation * child.Rotation;
        var childPosition = position + Vector3.Transform(child.Translation, rotation);
        var com = JoltMath.CenterOfMassTransform(childPosition, childRotation, leaf.GetCenterOfMass().FromJolt());
        UseMask(CollisionLayers.All);
        Only(target);
        var count = Collide(leaf, in com, maxDistance);
        var found = false;
        var deepest = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            ref readonly var r = ref _results[i];
            if (r.SubShapeID2 != subShape || r.PenetrationDepth <= deepest)
                continue;
            deepest = r.PenetrationDepth;
            contact = new BackendContact3D(-r.PenetrationDepth, r.PenetrationAxis.FromJolt(), r.ContactPointOn2.FromJolt());
            found = true;
        }

        return found;
    }

    public bool Distance(JoltBody slot, Vector3 position, Quaternion rotation, JoltBody target, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.Zero;
        if (slot.Children.Count == 0)
            return false;
        UseMask(CollisionLayers.All);
        Only(target);
        var com = JoltMath.CenterOfMassTransform(position, rotation, slot.Root.GetCenterOfMass().FromJolt());
        var count = Collide(slot.Root, in com, DistanceProbe);
        if (count == 0)
            return false;
        var deepest = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            if (_results[i].PenetrationDepth <= deepest)
                continue;
            deepest = _results[i].PenetrationDepth;
            normal = _results[i].PenetrationAxis.FromJolt(); // from the body towards the other
        }

        distance = -deepest;
        return true;
    }
```

`JoltBackend` overrides (replacing the four Task 4 stubs; `GetShapeCount` stays):

```csharp
    public override bool CastBody(BackendBody body, Vector3 position, Quaternion rotation, Vector3 motion, out BackendHit3D hit) =>
        _queries.CastBody(_bodies.Get(body), position, rotation, motion, out hit);

    public override void CollectNearbyShapes(BackendBody body, Vector3 position, Quaternion rotation, float expand, List<BackendShapeRef> output) =>
        _queries.CollectNearbyShapes(_bodies.Get(body), position, rotation, expand, output);

    public override bool Penetration(BackendBody body, int shape, Vector3 position, Quaternion rotation, BackendShapeRef other, float maxDistance,
        out BackendContact3D contact) =>
        _queries.Penetration(_bodies.Get(body), shape, position, rotation, _bodies.Get(other.Body), unchecked((uint)other.Shape), maxDistance, out contact);

    public override bool Distance(BackendBody body, Vector3 position, Quaternion rotation, BackendBody other, out float distance, out Vector3 normal) =>
        _queries.Distance(_bodies.Get(body), position, rotation, _bodies.Get(other), out distance, out normal);
```

- [ ] **Step 4: Run** `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics.Jolt|FullyQualifiedName~JoltCharacterTests"` (all Jolt classes) → pass; physics suite; trace gate; `just build`.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Src/Physics/3D/Jolt/JoltQueries.cs MainframeEngine/Src/Physics/3D/Jolt/JoltBackend.cs Tests/MainframeEngine.Tests/Physics/Jolt/JoltCharacterTests.cs
git commit -F- <<'EOF'
Add the MoveAndSlide primitives (cast, nearby shapes, penetration, distance) to the Jolt backend

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 9: Every shared 3D suite on both backends

**Files:**
- Modify: `Tests/MainframeEngine.Tests/Physics/Physics3DTests.cs`, `Physics3DQueryTests.cs`, `PhysicsSeamTests.cs` (B2 Task 1), `PhysicsEdgeCaseTests.cs`, `DebugDrawTests.cs`, `Tests/MainframeEngine.Tests/Terrain/Terrain3DTests.cs`
- Create: `Tests/MainframeEngine.Tests/Physics/Physics3DEdgeCaseTests.cs`, `DebugDraw3DTests.cs`, `PhysicsBackendDifferenceTests.cs`, `Tests/MainframeEngine.Tests/Terrain/TerrainCollisionTests.cs`
- Modify (only if Step 6 needs it): `Tests/Directory.Build.props`

**Interfaces:**
- Consumes: `PhysicsHarness3D(PhysicsBackendKind, PhysicsSettings3D?)`, `.Tolerances` (Task 6).
- Produces: abstract `Physics3DTests`, `Physics3DQueryTests`, `PhysicsSeamTests`, `Physics3DEdgeCaseTests`, `DebugDraw3DTests`, `PhysicsBackendDifferenceTests`, `TerrainCollisionTests`, each `(PhysicsBackendKind backend)` with sealed `…_Jitter2()`/`…_Jolt()` subclasses.

The pattern, applied to every class below (`X` is the class name):

```csharp
public abstract class X(PhysicsBackendKind backend) : IDisposable
{
    private readonly PhysicsHarness3D _h = new(backend);

    public void Dispose()
    {
        _h.Dispose();
        GC.SuppressFinalize(this);
    }

    // …the tests, unchanged unless listed…
}

public sealed class X_Jitter2() : X(PhysicsBackendKind.Jitter2);

public sealed class X_Jolt() : X(PhysicsBackendKind.Jolt);
```

- [ ] **Step 1: Convert the field-harness classes.**
  - `Physics3DTests.cs` lines 5–10: the summary becomes `/// <summary>Bodies, gravity, layers, signals and lifecycle of the 3D server, on each backend (<c>…_Jitter2</c>, <c>…_Jolt</c>).</summary>` and the header, field and `Dispose` follow the pattern; the subclasses go at the end of the file. Line 83 becomes `Assert.InRange(ball.GlobalPosition.Y, 0.0446f - _h.Tolerances.RestingPenetration, 0.0446f); // a resting body sinks in up to the backend's tolerance`.
  - `Physics3DQueryTests.cs` lines 6–10: the pattern; summary unchanged except "on each backend" appended.
  - `PhysicsSeamTests.cs` (B2 Task 1, `public sealed class PhysicsSeamTests : IDisposable` with `private readonly PhysicsHarness3D _h = new();`): the pattern; its summary gains "Runs on both backends (B3)."; `ChangingABodysModeInsideASignalHandlerAppliesAtTheNextStep`'s comment `// Jitter2 applies this one as a force: it must stop with the switch` becomes `// Jitter2 applies this one as a force, Jolt as its gravity factor: it must stop with the switch`.

- [ ] **Step 2: Split the mixed classes.**
  - Create `Physics3DEdgeCaseTests.cs` with the pattern minus the field (these tests make their own harness): `private PhysicsHarness3D NewHarness() => new(backend);` and move, verbatim, `DegenerateShapesAreSkippedWithoutBreakingTheStep`, `ExitIsDeliveredWhenTheOtherBodyIsFreedEarlierInTheSameDispatch`, `TurningContactMonitorOffReportsTheExits`, `KinematicBodiesIgnoreAxisLocks`, `SpacesAreCreatedMidStepAndReleasedWithTheirViewport` and `PhysicsInterpolationCanBeToggledWithoutARebuild` from `PhysicsEdgeCaseTests.cs`, replacing each `using var h = new PhysicsHarness3D();` with `using var h = NewHarness();`. The class has no `Dispose` and implements nothing:

```csharp
using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>3D edge cases (degenerate shapes, removal during dispatch, monitors off, kinematic locks, mid-step spaces, interpolation toggles) on each backend.</summary>
public abstract class Physics3DEdgeCaseTests(PhysicsBackendKind backend)
{
    private PhysicsHarness3D NewHarness() => new(backend);

    // the six moved tests
}

public sealed class Physics3DEdgeCaseTests_Jitter2() : Physics3DEdgeCaseTests(PhysicsBackendKind.Jitter2);

public sealed class Physics3DEdgeCaseTests_Jolt() : Physics3DEdgeCaseTests(PhysicsBackendKind.Jolt);
```

  `PhysicsEdgeCaseTests` keeps `TinyTwoDimensionalShapesDoNotCrash` and `MovingAStaticPlatformWakesBodiesResting2D` (summary: "2D edge cases").
  - Create `DebugDraw3DTests.cs`:

```csharp
using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>The 3D server draws collision shapes from the resources and render poses, whichever backend simulates them.</summary>
public abstract class DebugDraw3DTests(PhysicsBackendKind backend)
{
    [Fact]
    public void PhysicsServer3DDrawsCollisionShapesWhenEnabled()
    {
        using var h3 = new PhysicsHarness3D(backend);
        h3.AddFloor();
        h3.AddBox(new Vector3(0, 3, 0));
        h3.AddArea(new Vector3(5, 0, 0), Vector3.One);
        var lines = h3.Tree.Root.DebugLines;
        h3.Run(1);
        Assert.Equal(0, lines.LineCount); // off by default

        h3.Server.DebugDrawEnabled = true;
        h3.Run(1);
        Assert.Equal(36, lines.LineCount); // three boxes × 12 edges
        var maxY = float.MinValue;
        foreach (var v in lines.Vertices)
            maxY = MathF.Max(maxY, v.Position.Y);
        Assert.Equal(h3.Scene.GetNode<RigidBody3D>("Box").Position.Y + 0.5f, maxY, 3);
    }
}

public sealed class DebugDraw3DTests_Jitter2() : DebugDraw3DTests(PhysicsBackendKind.Jitter2);

public sealed class DebugDraw3DTests_Jolt() : DebugDraw3DTests(PhysicsBackendKind.Jolt);
```

  In `DebugDrawTests.cs`, `PhysicsServersDrawCollisionShapesWhenEnabled` (lines 50–76) becomes `PhysicsServer2DDrawsCollisionShapesWhenEnabled` holding only its 2D half (the `using var h2 …` block).
  - Create `Tests/MainframeEngine.Tests/Terrain/TerrainCollisionTests.cs` with the pattern (namespace `MainframeEngine.Tests.Terrain`, `using MainframeEngine.Tests.Physics;`), a copy of `Terrain3DTests.AddTerrain` (lines 13–20), and move `ACharacterCapsuleStandsExactlyOnTheTerrain`, `DistanceToSurface`, `ASphereRestsAtHeightPlusRadius` and `PhysicsRaysHitTheTerrainWhereRaycastDoes` (lines 339–391) verbatim out of `Terrain3DTests.cs`. Summary: `/// <summary>Terrain chunks collide like the drawn surface (characters, spheres, rays) on each backend.</summary>`.

- [ ] **Step 3: Write `PhysicsBackendDifferenceTests.cs`** (the named differences, asserted through the tolerances)

```csharp
using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// Where the backends differ on purpose (ADR 0192, B3; the physics doc's Backends section): each difference is a
/// <see cref="PhysicsTolerances"/> field, and these tests pin its value on both backends.
/// </summary>
public abstract class PhysicsBackendDifferenceTests(PhysicsBackendKind backend) : IDisposable
{
    private readonly PhysicsHarness3D _h = new(backend);

    public void Dispose()
    {
        _h.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ContactMonitorsReportBodiesFromTheContactStartDistance()
    {
        _h.AddFloor();
        var box = _h.AddBox(new Vector3(0, 0.51f, 0)); // 1 cm above the floor
        box.GravityScale = 0;
        box.ContactMonitor = true;
        var entered = false;
        box.BodyEntered += _ => entered = true;
        _h.Run(2);
        Assert.Equal(_h.Tolerances.ContactStartDistance > 0.01f, entered);
    }

    [Fact]
    public void AreasReportOnlyRealOverlaps()
    {
        var area = _h.AddArea(Vector3.Zero, new Vector3(2, 2, 2));
        var box = _h.AddBox(new Vector3(0, 1.51f, 0)); // 1 cm outside the area's top
        box.GravityScale = 0;
        _h.Run(2);
        Assert.False(area.OverlapsBody(box));
    }

    [Fact]
    public void NonUniformlyScaledSpheresAreEllipsoidsOrLargestAxisSpheres()
    {
        var egg = new StaticBody3D { Name = "Egg", Scale = new Vector3(1, 2, 1) };
        egg.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.5f } });
        _h.Add(egg);
        Assert.True(_h.Query.RayCast(new Vector3(0, 5, 0), new Vector3(0, -5, 0), out var top));
        Assert.Equal(1f, top.Position.Y, 2); // both: the top is at 1
        // At x = 0.9 a radius-1 sphere (Jolt) is hit; the 0.5-wide ellipsoid (Jitter2) is not.
        Assert.Equal(_h.Tolerances.SpheresScaleUniformly, _h.Query.RayCast(new Vector3(0.9f, 5, 0), new Vector3(0.9f, -5, 0), out _));
    }
}

public sealed class PhysicsBackendDifferenceTests_Jitter2() : PhysicsBackendDifferenceTests(PhysicsBackendKind.Jitter2);

public sealed class PhysicsBackendDifferenceTests_Jolt() : PhysicsBackendDifferenceTests(PhysicsBackendKind.Jolt);
```

- [ ] **Step 4: Run the Jitter2 halves first** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~_Jitter2"` → all pass (they are the old tests). If `ContactMonitorsReportBodiesFromTheContactStartDistance_Jitter2` fails (Jitter2 reports a 1 cm gap), measure Jitter2's start distance with the same test at 2, 5 and 10 mm gaps and set `PhysicsTolerances.Jitter2.ContactStartDistance` to the smallest gap it does not report minus 1 mm; never change the test.

- [ ] **Step 5: Run the Jolt halves** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~_Jolt"`. Expected: all pass. Each failure is either a backend bug (fix it in `Src/Physics/3D/Jolt/`, add a focused test to the matching `Jolt*Tests` class, re-run) or a genuine, documented library difference (add a named field to `PhysicsTolerances` with its doc comment, a row in the physics doc's differences table in Task 14, and use the field in the assertion). A tolerance is added only for a difference that a user would see as "the other engine", never to make a wrong result pass.

- [ ] **Step 6: The skip path and the analyzers.**
  - `MF_TEST_NO_JOLT=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~Physics3DTests_Jolt"` → every test reported **skipped** with "Jolt is disabled for this run (MF_TEST_NO_JOLT=1)." If xUnit reports them failed instead (a skip thrown from a test class constructor is not honoured), move the harness creation into `IAsyncLifetime`: each abstract class implements `IAsyncLifetime`, the field becomes `private PhysicsHarness3D _h = null!;`, `public ValueTask InitializeAsync() { _h = new PhysicsHarness3D(backend); return ValueTask.CompletedTask; }`, `public ValueTask DisposeAsync() { _h.Dispose(); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }`, and `IDisposable` is dropped; re-run until skipped.
  - `dotnet build Tests/MainframeEngine.Tests` → 0 warnings. If CA1707 (underscores in identifiers) is reported for the `…_Jitter2`/`…_Jolt` classes, add to `Tests/Directory.Build.props` under the CA1515 comment: `<!-- CA1707: the shared physics suites name their backend subclasses …_Jitter2 / …_Jolt (ADR 0192, B3). -->` and change the line to `<NoWarn>$(NoWarn);CA1515;CA1707</NoWarn>`.

- [ ] **Step 7: The whole suite and the trace** — `dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~MainframeEngine.Tests.Physics|FullyQualifiedName~MainframeEngine.Tests.Terrain"` → all pass; trace gate → 1 passed.

- [ ] **Step 8: Commit**

```bash
git add Tests/MainframeEngine.Tests/Physics/ Tests/MainframeEngine.Tests/Terrain/ Tests/Directory.Build.props
git commit -F- <<'EOF'
Run every shared 3D physics suite on Jitter2 and Jolt, with named per-backend tolerances

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---
### Task 10: Allocation gates on both backends

**Files:**
- Modify: `Tests/MainframeEngine.Tests/Physics/PhysicsAllocationTests.cs`, `Tests/MainframeEngine.Tests/Terrain/TerrainAllocationTests.cs`
- Create: `Tests/MainframeEngine.Tests/Terrain/TerrainCharacterAllocationTests.cs`

**Interfaces:**
- Consumes: `PhysicsHarness3D(PhysicsBackendKind, PhysicsSettings3D?)`, `PhysicsSpace3D.MeasureLibraryAllocations`/`LibraryAllocatedBytes`, `AllocationGate.SmallestWindow`, `SerialAllocationGates`.
- Produces: `public abstract class PhysicsAllocationTests(PhysicsBackendKind)` + `PhysicsAllocationTests_Jitter2`/`_Jolt`; `public abstract class TerrainCharacterAllocationTests(PhysicsBackendKind)` + `_Jitter2`/`_Jolt`.

- [ ] **Step 1: Convert `PhysicsAllocationTests`.** The summary's last sentences become "… multi-threaded, the library's worker pool may allocate inside its step (Jitter2: 56 B about once per 5 000 steps), so that run excludes what the library allocated inside its steps; Jolt's native allocations never count. Runs alone, on each backend (B3)."; remove `[Collection(nameof(SerialAllocationGates))]` from the class, which becomes `public abstract class PhysicsAllocationTests(PhysicsBackendKind backend)`; the harness line becomes `using var h = new PhysicsHarness3D(backend, new PhysicsSettings3D { MultiThreaded = multiThreaded });`; the first assertion message becomes `$"steady-state 3D physics frames on {backend} allocated {allocated} bytes ({library} inside the library's step)"`. Before the `CollectionDefinition` add:

```csharp
[Collection(nameof(SerialAllocationGates))]
public sealed class PhysicsAllocationTests_Jitter2() : PhysicsAllocationTests(PhysicsBackendKind.Jitter2);

[Collection(nameof(SerialAllocationGates))]
public sealed class PhysicsAllocationTests_Jolt() : PhysicsAllocationTests(PhysicsBackendKind.Jolt);
```

- [ ] **Step 2: The walking gate on both backends.** Move `SteadyStateFramesWithACharacterOnTheTerrainAllocateNothing` (`TerrainAllocationTests.cs` lines 42–65) verbatim into:

```csharp
using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Terrain;

/// <summary>A character walking on a terrain allocates nothing per frame, on each physics backend (the Forest's 0 B walk).</summary>
public abstract class TerrainCharacterAllocationTests(PhysicsBackendKind backend)
{
    // the moved test, with its first line `using var h = new PhysicsHarness3D(backend);`
}

[Collection(nameof(SerialAllocationGates))]
public sealed class TerrainCharacterAllocationTests_Jitter2() : TerrainCharacterAllocationTests(PhysicsBackendKind.Jitter2);

[Collection(nameof(SerialAllocationGates))]
public sealed class TerrainCharacterAllocationTests_Jolt() : TerrainCharacterAllocationTests(PhysicsBackendKind.Jolt);
```

`TerrainAllocationTests` keeps `RepeatedQueriesAllocateNothing` (terrain queries, no physics).

- [ ] **Step 3: Run the gates**

```bash
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsAllocationTests|FullyQualifiedName~TerrainCharacterAllocationTests|FullyQualifiedName~TerrainAllocationTests"
```
Expected: `PhysicsAllocationTests_Jitter2` 2 passed, `PhysicsAllocationTests_Jolt` 2 passed (0 B), both terrain gates pass. A Jolt byte count is a managed allocation in `Src/Physics/3D/Jolt` (or a binding call that marshals) on the per-frame path: find it with `dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x1:5` (GC AllocationTick) over the test or by bisecting `Frame()`, and fix it (preallocate, `Span` overloads, no `foreach` over interfaces); never exclude it.

- [ ] **Step 4: Commit**

```bash
git add Tests/MainframeEngine.Tests/Physics/PhysicsAllocationTests.cs Tests/MainframeEngine.Tests/Terrain/
git commit -F- <<'EOF'
Run the 3D physics and terrain walking allocation gates on both backends

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 11: Render tests and goldens on Jolt

**Files:**
- Modify: `Tests/MainframeEngine.RenderTests.Host/Scenes/PhysicsScene.cs`, `Tests/MainframeEngine.RenderTests.Host/Program.cs`, `Tests/MainframeEngine.RenderTests/SceneTests.cs`
- Create (recorded): `Tests/MainframeEngine.RenderTests/Goldens/moltenvk/physics-jolt_frame0030.png`, `physics-jolt_frame0150.png`, `physics-debug-jolt_frame0150.png` and the same three under `Goldens/lavapipe/`

**Interfaces:**
- Consumes: `RenderTestGame.Fail(string)`, `HostResult.SceneCheckFailures`, `Gates.AssertValidationClean`/`AssertMatchesGolden`, `ImageComparison.Compare`, `Png.ReadRgba8`.
- Produces: scenes `physics-jolt` (`PhysicsJoltScene`), `physics-debug-jolt` (`PhysicsDebugJoltScene`); tests `PhysicsSceneOnJoltRendersCleanlyAndMatchesGoldens`, `PhysicsDebugDrawOnJoltRendersCleanlyAndMatchesGolden`, `PhysicsCapturesOnJoltAreDeterministicAcrossRuns`.

- [ ] **Step 1: The scenes.** In `PhysicsScene`: the summary's "(Jitter2)" becomes "(Jitter2, or Jolt in `PhysicsJoltScene`)" and "Jitter2's deterministic solver" becomes "a deterministic solver"; add

```csharp
    /// <summary>The 3D physics library the scene runs on.</summary>
    protected virtual PhysicsBackendKind Backend => PhysicsBackendKind.Jitter2;

    private bool _checkedBackend;
```

make `physics.Settings.Backend = Backend;` the first statement after `var physics = Servers.Get<PhysicsServer3D>()!;` in `LoadScene`, and replace the empty `UpdateScene` with:

```csharp
    protected override void UpdateScene(in GameTime gameTime)
    {
        // Jolt is missing on some RIDs (osx-x64): the test skips instead of comparing Jitter2 frames with Jolt goldens.
        if (_checkedBackend || Servers.Get<PhysicsServer3D>() is not { Spaces.Count: > 0 } physics)
            return;
        _checkedBackend = true;
        if (physics.Spaces[0].BackendKind != Backend)
            Fail($"backend-unavailable: the scene asked for {Backend} but runs {physics.Spaces[0].BackendKind}");
    }
```

and after `PhysicsDebugScene`:

```csharp
/// <summary><see cref="PhysicsScene"/> on Jolt (B3).</summary>
public sealed class PhysicsJoltScene(HostOptions host) : PhysicsScene(host)
{
    protected override PhysicsBackendKind Backend => PhysicsBackendKind.Jolt;
}

/// <summary><see cref="PhysicsDebugScene"/> on Jolt (B3).</summary>
public sealed class PhysicsDebugJoltScene(HostOptions host) : PhysicsScene(host)
{
    protected override bool DebugDraw => true;

    protected override PhysicsBackendKind Backend => PhysicsBackendKind.Jolt;
}
```

`Program.cs`: after `"physics-debug" => new PhysicsDebugScene(options),` add `"physics-jolt" => new PhysicsJoltScene(options),` and `"physics-debug-jolt" => new PhysicsDebugJoltScene(options),`; in the scene list of the usage text (line 123) `"physics, physics-debug, "` becomes `"physics, physics-debug, physics-jolt, physics-debug-jolt, "`.

- [ ] **Step 2: The tests** (in `SceneTests`, after `PhysicsCapturesAreDeterministicAcrossRuns`):

```csharp
    private static void SkipUnlessBackendRan(HostResult result)
    {
        if (result.SceneCheckFailures.FirstOrDefault(f => f.StartsWith("backend-unavailable:", StringComparison.Ordinal)) is { } reason)
            Assert.Skip(reason);
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
    }

    [Fact]
    public void PhysicsSceneOnJoltRendersCleanlyAndMatchesGoldens()
    {
        var result = HostRunner.Run("physics-jolt", Output("physics-jolt"), "--capture", "30,150", "--hidden");
        SkipUnlessBackendRan(result);
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
        Gates.AssertMatchesGolden(result, 150);
    }

    [Fact]
    public void PhysicsDebugDrawOnJoltRendersCleanlyAndMatchesGolden()
    {
        var result = HostRunner.Run("physics-debug-jolt", Output("physics-debug-jolt"), "--capture", "150", "--hidden");
        SkipUnlessBackendRan(result);
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 150);
    }

    [Fact]
    public void PhysicsCapturesOnJoltAreDeterministicAcrossRuns()
    {
        var first = HostRunner.Run("physics-jolt", Output("physics-jolt-determinism-a"), "--capture", "90", "--hidden");
        SkipUnlessBackendRan(first);
        var second = HostRunner.Run("physics-jolt", Output("physics-jolt-determinism-b"), "--capture", "90", "--hidden");
        var comparison = ImageComparison.Compare(Png.ReadRgba8(first.Captures[0].Path), Png.ReadRgba8(second.Captures[0].Path), channelTolerance: 0);
        Assert.True(comparison.SizeMatches && comparison.DifferingPixels == 0,
            $"Two runs of the Jolt physics scene differ in {comparison.DifferingPixels} pixels (same binary, same calls: Jolt is deterministic).");
    }
```

- [ ] **Step 3: Record the `moltenvk` goldens** (display awake):

```bash
caffeinate -u -t 600 &
UPDATE_GOLDENS=1 dotnet test Tests/MainframeEngine.RenderTests --filter "FullyQualifiedName~PhysicsSceneOnJolt|FullyQualifiedName~PhysicsDebugDrawOnJolt"
```
Open the three new PNGs under `Tests/MainframeEngine.RenderTests/Goldens/moltenvk/`: frame 30 shows crates in flight, frame 150 crates settled on the floor and ramp (as `physics_frame0150.png`, not pixel-identical), the debug golden adds the collision wireframes on every crate, the ball, the ramp and the floor. Then run without `UPDATE_GOLDENS`: the three tests pass, and `PhysicsCapturesOnJoltAreDeterministicAcrossRuns` passes.

- [ ] **Step 4: Record the `lavapipe` goldens** (Docker, from the repository root):

```bash
just render-tests-linux --filter "FullyQualifiedName~PhysicsSceneOnJolt|FullyQualifiedName~PhysicsDebugDrawOnJolt"
cp artifacts/linux/render-tests/new-goldens/lavapipe/physics-jolt_frame0030.png artifacts/linux/render-tests/new-goldens/lavapipe/physics-jolt_frame0150.png artifacts/linux/render-tests/new-goldens/lavapipe/physics-debug-jolt_frame0150.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/
just render-tests-linux --filter "FullyQualifiedName~SceneTests"
```
Expected: the second run passes every `SceneTests` test, the old `physics`/`physics-debug` goldens included (Jitter2 unchanged).

- [ ] **Step 5: Commit** (PNGs are Git LFS)

```bash
git add Tests/MainframeEngine.RenderTests.Host/Scenes/PhysicsScene.cs Tests/MainframeEngine.RenderTests.Host/Program.cs Tests/MainframeEngine.RenderTests/SceneTests.cs Tests/MainframeEngine.RenderTests/Goldens/moltenvk/physics-jolt_frame0030.png Tests/MainframeEngine.RenderTests/Goldens/moltenvk/physics-jolt_frame0150.png Tests/MainframeEngine.RenderTests/Goldens/moltenvk/physics-debug-jolt_frame0150.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/physics-jolt_frame0030.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/physics-jolt_frame0150.png Tests/MainframeEngine.RenderTests/Goldens/lavapipe/physics-debug-jolt_frame0150.png
git commit -F- <<'EOF'
Add physics-jolt, physics-debug-jolt and the Jolt determinism render tests with goldens

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 12: Benchmarks per backend and the Forest terrain build

**Files:**
- Modify: `Tests/MainframeEngine.Benchmarks/PhysicsBenchmarks.cs` (2D only), `Tests/MainframeEngine.Benchmarks/baseline.json`
- Create: `Tests/MainframeEngine.Benchmarks/PhysicsBenchmarks3D.cs`, `Tests/MainframeEngine.Benchmarks/PhysicsBenchmarksTerrain.cs`

**Interfaces:**
- Consumes: `PhysicsSettings3D.Backend`, `PhysicsServer3D.Spaces`, `TerrainData.Create(TerrainProfile, int, float, int)`, `TerrainData.SetHeightsFrom(Func<float, float, float>)`, `Terrain3D`, BenchmarkDotNet `[Params]`, `[IterationSetup]`, `[IterationCleanup]`, `[InvocationCount]`, `[IterationCount]`, `[WarmupCount]`.
- Produces: `PhysicsBenchmarks.Step1kRigidBodies2D` (name unchanged), `PhysicsBenchmarks3D.Step1kRigidBodies3D([Backend=Jitter2|Jolt])`, `PhysicsBenchmarks3D.Raycast10k3D([Backend=…])`, `PhysicsBenchmarksTerrain.BuildForestTerrainCollision([Backend=…])` (names as `BenchmarkHost.Collect` writes them; all match `*PhysicsBenchmarks*`).

- [ ] **Step 1: Split the 3D benchmarks out.** In `PhysicsBenchmarks.cs` delete `_tree3D`, `_query`, the 3D half of `Setup`, `Step1kRigidBodies3D`, `Raycast10k3D`, `Rays`, and `_tree3D` from the cleanup loop; the summary becomes "Physics at scale (M6), 2D: one fixed step of 1 000 awake rigid bodies resting on a ground (Box2D.NET) through the scene tree. 3D is `PhysicsBenchmarks3D`, per backend." Create `PhysicsBenchmarks3D.cs`:

```csharp
using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// 3D physics at scale, per backend (B3): one fixed step of 1 000 awake boxes resting on a floor (the library's worker
/// pool) and 10 000 raycasts into the same world. Bodies never sleep, so every step does the full contact-solving work.
/// </summary>
[MemoryDiagnoser]
public class PhysicsBenchmarks3D : IDisposable
{
    private const int Bodies = 1000;
    private const int Rays = 10_000;

    private readonly GameTime _step = new() { DeltaTime = 1f / 60f, FrameCount = 1 };
    private SceneTree _tree = null!;
    private PhysicsDirectSpaceState3D _query = null!;

    [Params(PhysicsBackendKind.Jitter2, PhysicsBackendKind.Jolt)]
    public PhysicsBackendKind Backend { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var servers = new ServerRegistry();
        var physics = new PhysicsServer3D(new PhysicsSettings3D { Backend = Backend });
        servers.Register(physics);
        _tree = new SceneTree(servers);
        var scene = new Node3D { Name = "Scene3D" };
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(200, 1, 200) } });
        scene.AddChild(floor);
        var cube = new BoxShape3D();
        for (var i = 0; i < Bodies; i++)
        {
            var body = new RigidBody3D { Position = new Vector3(i % 40 * 1.5f - 30, 0.5f, i / 40 * 1.5f - 18), CanSleep = false };
            body.AddChild(new CollisionShape3D { Shape = cube });
            scene.AddChild(body);
        }

        _tree.ChangeScene(scene);
        _query = _tree.Root.World3D.DirectSpaceState;
        for (var i = 0; i < 120; i++)
            _tree.Tick(_step);
        if (physics.Spaces[0].BackendKind != Backend)
            throw new InvalidOperationException($"{Backend} is not available here; this run would measure {physics.Spaces[0].BackendKind} twice.");
    }

    /// <summary>One frame with one physics step: 1 000 awake boxes in contact with the floor.</summary>
    [Benchmark]
    public void Step1kRigidBodies3D() => _tree.Tick(_step);

    /// <summary>10 000 vertical raycasts over the grid (about half hit a box).</summary>
    [Benchmark]
    public int Raycast10k3D()
    {
        var hits = 0;
        for (var i = 0; i < Rays; i++)
        {
            var x = i % 100 * 0.6f - 30;
            var z = i / 100 * 0.37f - 18;
            if (_query.RayCast(new Vector3(x, 5, z), new Vector3(x, -1, z), out _))
                hits++;
        }

        return hits;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        if (_tree is not null)
        {
            _tree.Shutdown();
            _tree.Servers.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
```

Create `PhysicsBenchmarksTerrain.cs`:

```csharp
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Building the collision of a Forest-sized terrain (256 m at 0.5 m: 524 288 triangles in 64 chunk meshes), per backend
/// (B3; B4 asks for ≤ 0.5 s on Jolt; Jitter2 takes ≈ 2.1 s): the first physics step after the terrain enters the tree,
/// where the chunks' shapes are built. One invocation per iteration: every iteration builds a fresh tree.
/// </summary>
[MemoryDiagnoser]
[InvocationCount(1)]
[IterationCount(5)]
[WarmupCount(1)]
public class PhysicsBenchmarksTerrain : IDisposable
{
    private readonly GameTime _step = new() { DeltaTime = 1f / 60f, FrameCount = 1 };
    private SceneTree? _tree;

    [Params(PhysicsBackendKind.Jitter2, PhysicsBackendKind.Jolt)]
    public PhysicsBackendKind Backend { get; set; }

    [IterationSetup]
    public void Setup()
    {
        var servers = new ServerRegistry();
        servers.Register(new PhysicsServer3D(new PhysicsSettings3D { Backend = Backend }));
        _tree = new SceneTree(servers);
        var data = TerrainData.Create(TerrainProfile.Realistic, 256, 0.5f, 32);
        data.SetHeightsFrom(static (x, z) => 4f * MathF.Sin(x * 0.05f) * MathF.Cos(z * 0.07f));
        var scene = new Node3D { Name = "Scene" };
        scene.AddChild(new Terrain3D { Name = "Terrain", Data = data });
        _tree.ChangeScene(scene); // chunk meshes and faces now; their collision shapes at the first step
    }

    [Benchmark]
    public void BuildForestTerrainCollision() => _tree!.Tick(_step);

    [IterationCleanup]
    public void Cleanup()
    {
        if (_tree is null)
            return;
        if (_tree.Servers.Get<PhysicsServer3D>()!.Spaces[0].BackendKind != Backend)
            throw new InvalidOperationException($"{Backend} is not available here.");
        _tree.Shutdown();
        _tree.Servers.Dispose();
        _tree = null;
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 2: Build and dry-run** — `dotnet build Tests/MainframeEngine.Benchmarks -c Release` (0 warnings), then `dotnet run -c Release --project Tests/MainframeEngine.Benchmarks -- --filter '*PhysicsBenchmarks*' --job dry --artifacts artifacts/bench-dry` → every case runs once without an exception.

- [ ] **Step 3: Measure on the baseline machine** (the Mac `baseline.json` was recorded on, quiet, plugged in): `dotnet run -c Release --project Tests/MainframeEngine.Benchmarks -- --filter '*PhysicsBenchmarks*' --artifacts artifacts/bench --baseline-write artifacts/bench/physics-baseline.json`. If `BuildForestTerrainCollision([Backend=Jolt])` is above 0.5 s with the default `MeshShapeSettings` build quality, add `settings.SetBuildQuality(MeshShapeSettings.BuildQuality.FavorBuildSpeed);` before `CreateShape()` in `JoltShapeBuilder.Mesh`, re-run the Jolt physics tests (`dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~_Jolt"`) and the measurement, and keep whichever setting meets the criterion with the faster `Raycast10k3D`/terrain walking (record both numbers in the commit message).

- [ ] **Step 4: Merge into `baseline.json`** (renames the Jitter2 3D entries — same code, new names — after checking they did not regress, and adds the new cases):

```bash
python3 -I - Tests/MainframeEngine.Benchmarks/baseline.json artifacts/bench/physics-baseline.json <<'EOF'
import json, sys
path, new_path = sys.argv[1], sys.argv[2]
base = json.load(open(path, encoding="utf-8"))
new = {b["Name"]: b for b in json.load(open(new_path, encoding="utf-8"))["Benchmarks"]}
old = {b["Name"]: b for b in base["Benchmarks"]}
for name, renamed in [("PhysicsBenchmarks.Step1kRigidBodies3D", "PhysicsBenchmarks3D.Step1kRigidBodies3D([Backend=Jitter2])"),
                      ("PhysicsBenchmarks.Raycast10k3D", "PhysicsBenchmarks3D.Raycast10k3D([Backend=Jitter2])")]:
    ratio = new[renamed]["MeanNs"] / old[name]["MeanNs"]
    assert ratio <= 1.10, f"{renamed} is {ratio:.2f}x {name}: a regression to investigate, not to re-baseline"
    del old[name]
for name, entry in new.items():
    if name.startswith(("PhysicsBenchmarks3D.", "PhysicsBenchmarksTerrain.")):
        old[name] = entry
base["Benchmarks"] = sorted(old.values(), key=lambda b: b["Name"].encode("utf-8"))
with open(path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(base, f, indent=2)
    f.write("\n")
EOF
just bench '*PhysicsBenchmarks*'
```
Expected: the script prints nothing; `just bench` reports `ok` for every physics case (none `NEW`, none `FAIL`).

- [ ] **Step 5: Commit**

```bash
git add Tests/MainframeEngine.Benchmarks/PhysicsBenchmarks.cs Tests/MainframeEngine.Benchmarks/PhysicsBenchmarks3D.cs Tests/MainframeEngine.Benchmarks/PhysicsBenchmarksTerrain.cs Tests/MainframeEngine.Benchmarks/baseline.json MainframeEngine/Src/Physics/3D/Jolt/JoltShapes.cs
git commit -F- <<'EOF'
Benchmark 3D physics per backend and the Forest terrain build; add the Jolt baselines

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 13: The Demo's physics engine toggle

**Files:**
- Modify: `Examples/Demo/Demo/Src/Physics/Physics3DPanel.cs`, `Examples/Demo/Demo/Src/Physics/Physics3DScene.cs` (summary), `Examples/Demo/Content/Physics/panel3d.rml`
- Modify: `Examples/Demo/Content/locale/messages.pot`, `Examples/Demo/Content/locale/es/LC_MESSAGES/messages.po`, `.mo`, `Examples/Demo/Content/locale/qps/LC_MESSAGES/messages.po`, `.mo`
- Modify: `Examples/Demo/Demo.Tests/Physics3DSceneTests.cs`, `docs/design/demo.md`, `docs/images/demo/physics_3d.png`

**Interfaces:**
- Consumes: `PhysicsServer3D.Settings.Backend`, `PhysicsServer3D.FindSpace(World3D)`, `PhysicsSpace3D.BackendKind`, `SceneTree.ChangeScene(Node)` (deferred while the UI dispatches), `Physics3DScene.Build()`, `RmlDataModel.Bind<TOwner, T>`, `Tr._`.
- Produces: `Physics3DPanel.BackendName` (`"jolt"`/`"jitter2"`), `Physics3DPanel.BackendNote`, `Physics3DPanel.SwitchBackend(string)`; RML bindings `backend`, `backendNote`.

- [ ] **Step 1: Write the failing Demo test** (in `Physics3DSceneTests`):

```csharp
    // Public API only: a throwaway server tells whether Jolt runs here.
    private static bool JoltRunsHere()
    {
        var servers = new ServerRegistry();
        var physics = new PhysicsServer3D(new PhysicsSettings3D { Backend = PhysicsBackendKind.Jolt, MultiThreaded = false });
        servers.Register(physics);
        var tree = new SceneTree(servers);
        try
        {
            var body = new StaticBody3D();
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
            tree.ChangeScene(body);
            return physics.Spaces[0].BackendKind == PhysicsBackendKind.Jolt;
        }
        finally
        {
            tree.Shutdown();
            servers.Dispose();
        }
    }

    [Fact]
    public void ThePhysicsEngineToggleRebuildsTheSceneOnTheChosenBackend()
    {
        if (!JoltRunsHere())
            Assert.Skip("Jolt is not available on this machine.");
        Physics3DPanel Panel() => Scene.GetNode<Physics3DPanel>("Ui/Panel");
        Tick();
        Assert.Equal("jitter2", Panel().BackendName);
        Assert.Equal(PhysicsBackendKind.Jitter2, _physics.Spaces[0].BackendKind);

        Panel().SwitchBackend("jolt");
        Tick();
        Assert.Equal(PhysicsBackendKind.Jolt, _physics.Spaces[0].BackendKind);
        Assert.Equal("jolt", Panel().BackendName);
        Assert.Equal("", Panel().BackendNote);
        Assert.Equal(15, Crates.Count);
        var crate = Crates.Drop(new Vector3(0, 8, 3));
        for (var i = 0; i < 30; i++)
            Tick();
        Assert.True(crate.Position.Y < 7.5f, $"crate stayed at {crate.Position.Y}");

        Panel().SwitchBackend("jitter2");
        Tick();
        Assert.Equal(PhysicsBackendKind.Jitter2, _physics.Spaces[0].BackendKind);
    }
```

Run: `dotnet test Examples/Demo/Demo.Tests --filter "FullyQualifiedName~ThePhysicsEngineToggle"` → build error (`BackendName` missing).

- [ ] **Step 2: The panel.** In `Physics3DPanel` add:

```csharp
    /// <summary>The 3D physics library new spaces use (<c>"jolt"</c> or <c>"jitter2"</c>).</summary>
    public string BackendName => _physics?.Settings.Backend == PhysicsBackendKind.Jolt ? "jolt" : "jitter2";

    /// <summary>Shown when Jolt was chosen but this scene runs Jitter2 (no Jolt native for this computer).</summary>
    public string BackendNote =>
        _physics is { Settings.Backend: PhysicsBackendKind.Jolt } physics && GetViewport() is { } viewport &&
        physics.FindSpace(viewport.World3D) is { BackendKind: PhysicsBackendKind.Jitter2 }
            ? Tr._("Jolt is not available on this computer; using Jitter2.")
            : "";

    /// <summary>Switches the physics library and rebuilds the scene on it (its world empties, so its next body gets a new space).</summary>
    public void SwitchBackend(string value)
    {
        var kind = value == "jolt" ? PhysicsBackendKind.Jolt : PhysicsBackendKind.Jitter2;
        if (_physics is null || _physics.Settings.Backend == kind)
            return;
        _physics.Settings.Backend = kind;
        Tree?.ChangeScene(Physics3DScene.Build());
    }
```

and in `OnReady`, after the `shapes` binding: `.Bind("backend", this, static d => d.BackendName, static (d, v) => d.SwitchBackend(v))` and
`.Bind("backendNote", this, static d => d.BackendNote)`. The class summary gains "a physics engine toggle (Jolt or Jitter2),".
`Physics3DScene`'s summary becomes `/// <summary>Rigid bodies on Jolt or Jitter2: a crate stack, a ramp, a character body that shoves, and click-to-drop crates.</summary>`.

`panel3d.rml`: the hint becomes `<p class="hint">Rigid bodies on Jolt or Jitter2, a ramp and a character body. Click the floor to drop a crate.</p>`, and after the "Collision shapes" row add:

```html
        <div class="demo-row"><span>Physics engine</span><select data-value="backend"><option value="jolt" class="no-tr">Jolt</option><option value="jitter2" class="no-tr">Jitter2</option></select></div>
        <p class="hint no-tr" data-if="backendNote != ''">{{backendNote}}</p>
```

- [ ] **Step 3: Strings.** `just l10n-extract`; in `Examples/Demo/Content/locale/es/LC_MESSAGES/messages.po` fill the new entries:

```po
msgid "Rigid bodies on Jolt or Jitter2, a ramp and a character body. Click the floor to drop a crate."
msgstr "Cuerpos rígidos con Jolt o Jitter2, una rampa y un cuerpo de personaje. Haz clic en el suelo para soltar una caja."

msgid "Physics engine"
msgstr "Motor de física"

msgid "Jolt is not available on this computer; using Jitter2."
msgstr "Jolt no está disponible en este ordenador; se usa Jitter2."
```

then `just l10n-compile` and `just l10n-check` (clean; the old hint's `msgid` is gone from the `.pot` and marked obsolete in the `.po`s).

- [ ] **Step 4: Run the Demo tests** — `dotnet test Examples/Demo/Demo.Tests` → all pass (the new one skipped where Jolt cannot run). `just demo --scene Content/Scenes/physics_3d.mscene`: pick Jolt in the panel; the scene rebuilds, crates fall and stack, the pusher shoves them, rain and save/load work; pick Jitter2 back.

- [ ] **Step 5: Docs and screenshot.** `docs/design/demo.md`'s `physics_3d` row: "Jitter2: a crate stack…" → "Jolt or Jitter2 (the panel's **Physics engine** picker rebuilds the scene on the other library; B3): a crate stack…" and its controls cell gains "physics engine". `caffeinate -u -t 600 & just demo-screenshots`, then `git status docs/images/demo`: keep `physics_3d.png` (the new panel row), `git checkout -- ` any other changed PNG under `docs/images/demo/`.

- [ ] **Step 6: Commit**

```bash
git add Examples/Demo/Demo/Src/Physics/Physics3DPanel.cs Examples/Demo/Demo/Src/Physics/Physics3DScene.cs Examples/Demo/Content/Physics/panel3d.rml Examples/Demo/Content/locale/ Examples/Demo/Demo.Tests/Physics3DSceneTests.cs docs/design/demo.md docs/images/demo/physics_3d.png
git commit -F- <<'EOF'
Add a Jolt/Jitter2 physics engine picker to the Demo's Physics 3D scene

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 14: Docs, README, site, all gates and the B4 evidence

**Files:**
- Modify: `docs/design/physics.md`, `docs/design/future/brutal-backend.md`, `docs/design/testing.md`, `docs/milestones.md`, `CLAUDE.md`, `README.md`, `docs-site/src/components/landing/Features.astro`, `docs-site/src/components/landing/Showcase.astro`, `memory/context/progress-log.md`

**Interfaces:**
- Consumes: everything above.
- Produces: docs that match the code; the B4 evidence table.

- [ ] **Step 1: `docs/design/physics.md`.**
  - Libraries table → three library columns:

```markdown
| | 3D: Jitter2 | 3D: Jolt | 2D |
|---|---|---|---|
| Package | [`Jitter2`](https://www.nuget.org/packages/Jitter2) **2.9.0** (pinned exactly; the API changes between minors) | [`Ahwoo.BRUTAL.Jolt`](https://www.nuget.org/packages/Ahwoo.BRUTAL.Jolt) **2026.10.2**: Jolt **5.5.0** (pinned exactly; "highly experimental") | [`Box2D.NET`](https://www.nuget.org/packages/Box2D.NET) **3.1.654** (ikpil's port of Box2D v3.1) |
| Repo / licence | [notgiven688/jitterphysics2](https://github.com/notgiven688/jitterphysics2), MIT | [jrouwe/JoltPhysics](https://github.com/jrouwe/JoltPhysics) (MIT) inside BRUTAL (BRUTAL License v1.0, [ADR 0192](../../memory/decisions/0192-brutal-backend-tech.md)) | [ikpil/Box2D.NET](https://github.com/ikpil/Box2D.NET), MIT |
| Native code | none (pure C#) | `jolt` (win-x64, linux-x64, osx-arm64; none for osx-x64) | none (pure C#) |
| Precision | `float` | `float` (single-precision build, 16-bit object layers) | `float` |
| Threading | process-wide worker pool (`PhysicsSettings3D.MultiThreaded`) | a job system per space: `min(cores − 1, 8)` workers (`MultiThreaded`), otherwise single-threaded | single-threaded |
| Determinism | non-deterministic by default; `Deterministic` = island solver, reproducible on one machine/build | deterministic for the same binary and the same calls, multi-threaded included (not across platforms) | deterministic on one machine/build |
```

  - The paragraph after it (B2 Task 12's "3D physics runs behind an internal seam…") → "3D physics runs behind an internal seam, `PhysicsBackend3D` ([Backends](#backends-3d)): Jitter2 (the default) or Jolt through BRUTAL, chosen per project with `physics.3d.backend`; Jolt becomes the default once the B4 criteria hold ([BRUTAL backend](future/brutal-backend.md)). Physics is single precision and offers no cross-platform lockstep mode ([ADR 0020](../../memory/decisions/0020-physics-precision-and-determinism.md))."
  - Key types: add the row `| JoltBackend, JoltRuntime, JoltShapeCache, JoltContactTracker, JoltQueries | [Physics/3D/Jolt/](../../MainframeEngine/Src/Physics/3D/Jolt/) | Jolt backend (B3) |`.
  - Shape table: the header becomes `| Resource (3D) | Jitter2 | Jolt | Resource (2D, px) | Box2D.NET |` and each 3D row gains its Jolt cell: box `BoxShape(half extents × scale, convex radius min(0.05, 0.1 × smallest half extent))`; sphere `SphereShape(r × largest scale axis)`; capsule `CapsuleShape(MidHeight / 2, r)` × the largest scale axis (Y axis; no cylinder: a sphere); cylinder `CylinderShape(Height / 2, r, convex radius as for boxes)` (radius × the larger of X/Z scale); convex `ConvexHullShape` (the linear map baked into the points); concave `MeshShape` (linear map baked, degenerate triangles skipped); height map `HeightFieldShape` when square with a block size (`JoltApiFacts.HeightFieldBlockSize`) and no shear, the engine's cell diagonal kept by a quarter turn of the grid, otherwise `MeshShape`. Below the table: "On Jolt, several shapes form one `StaticCompoundShape` (compound user data = the description index, so hits report the right `CollisionShape3D`); one placed shape is a `RotatedTranslatedShape`; scales are baked into the shape. Native shapes are cached per space by (resource, `ChangeCount`, linear map) and shared by every body using them."
  - Contacts table, 3D cells: `RigidBody.BodyEntered/BodyExited` → "Jitter2: scan the body's arbiters (`RigidBody.Contacts`) with intact contact points. Jolt: the body's set from the drained `ContactEventListener` (`Added`/`Removed` per sub-shape pair; contacts start within the 2 cm speculative distance)"; `Area.BodyEntered/BodyExited` → "Jitter2: per area: tree query of its AABB, then `NarrowPhase.Overlap` against each candidate (sees static, rigid and character bodies). Jolt: the area is an active kinematic sensor that collides with kinematic and static bodies; its set comes from the same events (exact overlaps)"; `RigidBody.SleepingStateChanged` → "`RigidBody.IsActive` (Jitter2) / `BodyInterface.IsActive` (Jolt) changes".
  - Queries, the "Jitter2 queries go through…" bullet gains: "Jolt queries go through `NarrowPhaseQuery.CastRayClosest/CastShapeClosest/CollideShape/CollidePoint` with one reusable layer filter and body filter per space and a broad-phase filter that skips areas; result spans are owned by the space."
  - The `## Backends (3D)` section (B2 Task 12) gains, after its Jitter2 paragraph:

```markdown
**Jolt** (`JoltBackend`, `Src/Physics/3D/Jolt/`; B3): `JoltRuntime.TryInitialize` loads the `jolt` native once per
process (allocator, factory, types, the trace/assert bridge to `Log` as `[Jolt] …`, and the immutable 65 536-entry
broad-phase layer table and filters every space shares). Where it fails (osx-x64), `physics.3d.backend: "jolt"` logs
`[Physics] Jolt is not available on <rid> (<reason>); using Jitter2.` once per server and runs Jitter2. Per space: a
`PhysicsSystem` (`physics.3d.jolt.*` sizes and solver steps, sleep after 1 s like Jitter2, deterministic simulation,
friction and restitution combined with the maximum like Jitter2), a malloc-fallback temp allocator, a single-threaded or
pooled job system, a contact listener, query filters and a shape cache, disposed in the order bodies → system →
listener → filters → job systems → allocator → shapes (Debug builds count every native handle). Object layers are
`(broadPhase << 14) | index` over three broad-phase layers (static, moving, area) with one index per distinct
`(layer, mask)` pair in use (at most 16 383 at once; the pair filter calls `PhysicsSpace3D.Collides`). Bodies: dynamic,
kinematic (and characters, moved by `MoveKinematic`) or static; areas are active kinematic sensors; damping per step is
converted to Jolt's rate so the per-step factor equals Jitter2's; gravity scale is `GravityFactor`; axis locks are
`AllowedDOFs` (changed in place; all six locked: held kinematic); CCD is `LinearCast`. Contact sets come from the drained
events; if the event buffer overflows, the space's monitors and areas use shape queries from then on and the buffer
doubles (logged once). Facts about the binding the backend relies on are `JoltApiFacts`, pinned by `JoltApiFactsTests`:
re-run them first on every BRUTAL upgrade.

Where the backends differ (each a `PhysicsTolerances` field of the shared tests, or no tolerance needed):

| Difference | Jitter2 | Jolt | `PhysicsTolerances` |
|---|---|---|---|
| Resting penetration | up to 1 cm | up to 2 cm (penetration slop) | `RestingPenetration` |
| Contact monitor start | touching | within 2 cm (speculative contacts); areas: exact overlap on both | `ContactStartDistance` |
| Non-uniform scale on spheres, capsules (and X ≠ Z on cylinders) | exact (any linear map) | the largest axis, one warning per resource | `SpheresScaleUniformly` |
| Sheared shape transforms | exact | nearest rotation × scale, one warning | — |
| Layer/mask pairs per space | unlimited | 16 383 distinct pairs in use at once (then an error naming `jitter2`) | — |
| Height maps | triangle mesh | height field (square, valid sample count) or mesh, same surface | — |
| Platforms | every platform | win-x64, linux-x64, osx-arm64; elsewhere Jitter2 with one warning | — |
```

  - Settings bullet: `Backend` (`Jitter2` default; `Jolt` where its native exists, else Jitter2 with one warning; a world's empty space is re-created when it changes) and `Jolt.*` "read when a Jolt space is created".
  - Testing paragraph: append "Every shared 3D suite (`Physics3DTests`, `Physics3DQueryTests`, `Physics3DEdgeCaseTests`, `PhysicsSeamTests`, `DebugDraw3DTests`, `PhysicsBackendDifferenceTests`, `TerrainCollisionTests`, both allocation gates) is abstract with `…_Jitter2`/`…_Jolt` subclasses over `PhysicsHarness3D(PhysicsBackendKind)`; Jolt classes skip where Jolt cannot run (`MF_TEST_NO_JOLT=1` skips them anywhere). The Jolt parts alone: `JoltApiFactsTests`, `JoltRuntimeTests`, `JoltMathTests`, `JoltLayerTableTests`, `JoltShapeTests`, `JoltBackendTests`, `JoltQueryTests`, `JoltContactTests`, `JoltCharacterTests`. Render tests `physics-jolt`, `physics-debug-jolt` and the determinism check on Jolt; benchmarks `PhysicsBenchmarks3D` and `PhysicsBenchmarksTerrain` per backend."
  - Known issues: "Resting bodies settle up to 1 cm into what they rest on (Jitter2's …)" → "Resting bodies settle up to 1 cm (Jitter2's `ContactData.Contact.AllowedPenetration`) or 2 cm (Jolt's penetration slop) into what they rest on; Godot's margin is about a millimetre."; "`HeightMapShape3D` is a triangle mesh (memory grows with the map)." → "`HeightMapShape3D` is a triangle mesh on Jitter2 (memory grows with the map) and a height field on Jolt when square with a valid sample count."; add "- Jolt: at most 16 383 distinct layer/mask pairs in use per space; a contact event overflow moves that space's monitors and areas to shape queries for its lifetime (logged once)."

- [ ] **Step 2: `docs/design/future/brutal-backend.md`.** Status line: `**Status:** 🟨 B2 and B3 done ·`. Under "## B3: the Jolt backend" add first:

```markdown
**Built (B3).** As specified, except where the binding or the B2 contract decided otherwise: the 65 536-entry
broad-phase table and the stateless object-vs-broad-phase and no-areas filters are shared by every space (immutable),
and the pair table is a fixed array written between steps (Jolt reads it only inside `Update` and main-thread queries),
not copy-on-write; scales are baked into the shapes (no `ScaledShape`); axis locks change in place through
`MotionProperties.SetMassProperties` (no re-creation), and all six locked hold the body kinematic (Jolt cannot lock
every axis); the backend does not know which bodies are contact monitors (the contract has no such flag), so every
body keeps a touching set from the events, and an event overflow moves the space's monitors and areas to shape
queries for its lifetime instead of rebuilding once (a rebuilt count cannot be reconciled with Jolt's later `Removed`
events); Jolt's traces come through `Jolt.SetTraceHandler`/`SetAssertHandler` (Brutal.Jolt does not use
Brutal.Logging); single-threaded spaces use `JobSystemSingleThreaded`. Binding facts: `JoltApiFacts` +
`JoltApiFactsTests`.
```

  In "## Open questions" mark the first three answered: "~~Does BRUTAL's `JobSystemThreadPool` accept 0 workers…~~ It does, and the binding has `JobSystemSingleThreaded`, which single-threaded spaces use (B3).", "~~Do `Brutal.Numerics.float3`/`floatQuat` share…~~ Yes (and `float4x4` has `Mat44`'s memory as a `Matrix4x4`): `JoltMath` bit-casts (B3).", "~~Jitter2 2.9's friction/restitution combine rule…~~ The maximum for both (`ContactData.Init`); Jolt is set to match (B3)." Then append to "## B4: Jolt by default" the evidence table of Step 6.

- [ ] **Step 3: `docs/design/testing.md`** — the render-test scene list gains "`physics-jolt`, `physics-debug-jolt` (the physics scenes on Jolt; skipped where Jolt cannot run)". **`docs/milestones.md`**: the B3 row's `⬜` → `✅`; the BRUTAL backend summary row (line 56) `⬜` → `🟨` if it still shows `⬜`.

- [ ] **Step 4: `CLAUDE.md`, `README.md`, the site.**
  - `CLAUDE.md` Project Structure, the physics bullet (B2 Task 12's text "… `3D/Jitter/Jitter2Backend` (Jitter2), `3D/Jolt/` (B3)") → "… `3D/Jitter/Jitter2Backend` (Jitter2), `3D/Jolt/JoltBackend` (Jolt 5.5 via `Ahwoo.BRUTAL.Jolt`; `physics.3d.backend`; Jitter2 where the native is missing; binding facts in `JoltApiFacts`)"; Physics section "`PhysicsServer3D` (Jitter2 2.9.0) and" → "`PhysicsServer3D` (Jitter2 2.9.0, or Jolt 5.5 via BRUTAL per project: `physics.3d.backend`) and"; Dependencies "- Jitter2 (3D) and Box2D.NET (2D) — physics (pinned exactly)" → "- Jitter2 (3D), Jolt via `Ahwoo.BRUTAL.Jolt` (3D, ADR 0192) and Box2D.NET (2D) — physics (pinned exactly)".
  - `README.md`: line 18 "Jitter2/Box2D physics" → "Jolt (via BRUTAL) or Jitter2 3D physics, Box2D 2D physics"; the Physics row (line 42) → "| Physics | 3D: [Jolt](https://github.com/jrouwe/JoltPhysics) via BRUTAL, or [Jitter2](https://github.com/notgiven688/jitterphysics2) (pure C#); 2D: [Box2D.NET](https://github.com/ikpil/Box2D.NET): bodies, areas, layers, queries, `MoveAndSlide`, interpolation, debug draw | [Physics](docs/design/physics.md) |"; the showcase caption (line 65) "**Physics 3D** — Jitter2, save slots with thumbnails" → "**Physics 3D** — Jolt or Jitter2 (picker), save slots with thumbnails"; the layout comment (line 140) "PhysicsServer3D (Jitter2) / PhysicsServer2D (Box2D.NET)" → "PhysicsServer3D (Jolt via BRUTAL or Jitter2) / PhysicsServer2D (Box2D.NET)"; the dependency table gains under the Jitter2 row "| Physics 3D (Jolt) | [Jolt Physics](https://github.com/jrouwe/JoltPhysics) via [`Ahwoo.BRUTAL.Jolt`](https://www.nuget.org/packages/Ahwoo.BRUTAL.Jolt) | 5.5.0 (2026.10.2) |".
  - `docs-site/src/components/landing/Features.astro` Physics text → `'3D physics on Jolt via BRUTAL, or Jitter2 (pure C#), and Box2D in 2D: rigid, static, character and area bodies, layers, raycasts, MoveAndSlide and interpolation.'`; `Showcase.astro` physics_3d text → `'Jolt or Jitter2 (picker), save slots with thumbnails'`. Check the site: `just docs-build` → builds.

- [ ] **Step 5: `memory/context/progress-log.md`** — append:

```markdown

### 2026-10-16 — BRUTAL B3: the Jolt backend (ADR 0192)
- `JoltBackend` (`Src/Physics/3D/Jolt/`: `JoltRuntime`, `JoltApiFacts`, `JoltMath`, `JoltLayers`, `JoltShapes`, `JoltBodies`, `JoltContacts`, `JoltQueries`, `JoltNativeLedger`) on `Ahwoo.BRUTAL.Jolt` 2026.10.2 (Jolt 5.5.0); `physics.3d.backend: "jolt"`, Jitter2 fallback with one warning where the native is missing (osx-x64); an empty world's space is re-created on a backend change.
- Every shared 3D suite, both allocation gates, `physics-jolt`/`physics-debug-jolt` goldens (moltenvk, lavapipe) and the Jolt determinism check run on both backends; differences are `PhysicsTolerances` fields; benchmarks `PhysicsBenchmarks3D`/`PhysicsBenchmarksTerrain` per backend in `baseline.json`; the Demo's Physics 3D panel picks the engine.
- B4 evidence: the table in `docs/design/future/brutal-backend.md` (B4).
```

- [ ] **Step 6: All gates and the B4 evidence** (display awake: `caffeinate -u -t 1800 &`)

```bash
just build
dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false
just test
just test-render
just render-tests-linux
just format-check
just shaders-check
just template-smoke
MF_PHYSICS_TRACE=1 dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~PhysicsTraceTests"
just bench '*PhysicsBenchmarks*'
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~_Jolt" --logger "console;verbosity=normal"
dotnet test Tests/MainframeEngine.Tests --filter "FullyQualifiedName~_Jitter2"
```
Expected: 0 warnings in both builds; every test passes (the trace 1 passed; Jolt skipped nowhere on this Mac); render tests pass on moltenvk and lavapipe; format and shaders clean; template smoke passes; `just bench` reports `ok` for every case. If `just format-check` fails, run `just format`, re-run `just test`.

Then the Forest walk (B4 criterion 5) on both backends, without committing the setting: add `"backend": "jolt", ` before `"gravity"` in `Examples/Forest/project.mfproj`'s `physics.3d`, run `just forest ++ --autowalk` (exit code 0: the 600-frame window allocated nothing) and `just forest ++ --autowalk-lap` (exit code 0: the loop completes), then `git checkout -- Examples/Forest/project.mfproj` and run both again on Jitter2. Walk the Demo's Physics 3D scene on both engines (`just demo --scene Content/Scenes/physics_3d.mscene`: drop, rain, push, save, load).

Append to `docs/design/future/brutal-backend.md` under "## B4: Jolt by default", filled from those runs:

```markdown
**Evidence from B3** (osx-arm64, moltenvk; lavapipe in Docker; <date of the run>):

| # | Criterion | Holds | Evidence |
|---|---|---|---|
| 1 | Every shared physics test passes on Jolt with the documented tolerances only | yes/no | `_Jolt`: <passed> passed, <failed> failed, <skipped> skipped; tolerances: `RestingPenetration`, `ContactStartDistance`, `SpheresScaleUniformly` |
| 2 | Allocation gates 0 B | yes/no | `PhysicsAllocationTests_Jolt` (single/multi-threaded): <bytes> B / <bytes> B; `TerrainCharacterAllocationTests_Jolt`: <bytes> B |
| 3 | `physics`, `physics-debug`, determinism render tests on moltenvk and lavapipe | yes/no | `physics-jolt`, `physics-debug-jolt`, `PhysicsCapturesOnJoltAreDeterministicAcrossRuns`: <result> (moltenvk), <result> (lavapipe) |
| 4 | Step 1 000 boxes ≤ Jitter2; 10 000 rays ≤ Jitter2; terrain build ≤ 0.5 s | yes/no | `Step1kRigidBodies3D`: Jolt <ns> vs Jitter2 <ns>; `Raycast10k3D`: Jolt <ns> vs Jitter2 <ns>; `BuildForestTerrainCollision`: Jolt <s> vs Jitter2 <s> |
| 5 | Demo Physics 3D and the Forest play the same on both | yes/no | Forest `--autowalk` exit <code> (Jolt) / <code> (Jitter2), `--autowalk-lap` exit <code> / <code>; Demo walkthrough: <what differed, or "no difference"> |
```

Every `<…>` is replaced by the measured value from the command output above before committing (numbers from `just bench`'s output lines, test counts from the `dotnet test` summary lines); a criterion that does not hold is "no" with its numbers — B4 decides on it, B3 does not tune toward it.

- [ ] **Step 7: Commit**

```bash
git add docs/design/physics.md docs/design/future/brutal-backend.md docs/design/testing.md docs/milestones.md CLAUDE.md README.md docs-site/src/components/landing/Features.astro docs-site/src/components/landing/Showcase.astro memory/context/progress-log.md
git commit -F- <<'EOF'
Document the Jolt backend (B3): physics doc, BRUTAL spec and B4 evidence, README, site, CLAUDE.md

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

**PR 2.** Ask the user to push and open PR 2 ("BRUTAL B3 (2/2): Jolt signals, characters and selection; every physics
suite, gate, golden and benchmark on both backends; Demo picker"), the body listing Tasks 6–14, the differences table and
the B4 evidence table. Do not merge without the user.

---

## Self-review

| Spec B3 requirement | Task |
|---|---|
| `JoltRuntime.TryInitialize` once per process: `NativeLibrary.TryLoad("jolt")`, `RegisterDefaultAllocator`, `Factory.Instance`, `RegisterTypes`; process lifetime | 1 (+ shared objects 2) |
| Unavailable → one log line `[Physics] Jolt is not available on <rid> (<reason>); using Jitter2.`, Jitter2 spaces; forced-unavailable test | 6 (`ForceJoltUnavailableForTesting`, `SelectingJoltWhereItIsUnavailable…`) |
| Jolt traces/asserts to `Log` (`[Jolt] …`), muted level formats nothing | 1 (`OnTrace`/`OnAssert` check `Log.IsEnabled` first), 2 (`JoltTracesReachTheLogAsWarnings`) |
| Per space: `TempAllocatorImpl` (`tempAllocatorMB`) | 4 (`TempAllocatorImplWithMallocFallback`: Jolt's plain allocator aborts the process when full) |
| Job system: `min(cores − 1, 8)` workers, else single-threaded (0-worker question) | 1 (facts), 4 (`Jobs()`) |
| Layer interfaces, `PhysicsSystem.Init` with `maxBodies`/`maxBodyPairs`/`maxContactConstraints`, velocity/position steps, `AllowSleeping` | 2, 4 |
| `ContactEventListener(contactEventCapacity)`, Added + Removed | 7 |
| Reusable query filters per space, reconfigured per query | 5 (`LayerFilter`, `IdFilter`) |
| `Step`: `Update(dt, SubstepCount, temp, jobs)`, `TakeCallbackException()` rethrown, drain | 4, 7 |
| Teardown order + debug leak check; tests create and dispose many spaces | 4 (`Dispose`, `JoltNativeLedger`, `DisposingMultiThreadedSpacesRightAfterAStepJoinsTheirWorkers`) |
| Three broad-phase layers; object layer `(bp << 14) | index`; table for all 65 536 layers; object-vs-bp rules; managed pair filter calling `Collides` | 2 |
| Layer/mask change → `SetObjectLayer`, contacts dropped | 4 (`SetFilter`, `ClearContacts`) |
| Body table: kinds, area sensor (collide kinematic vs non-dynamic), user data, id → handle table | 4 (`JoltBodyTable.FromId`), 7 |
| Mass (`CalculateInertia` semantics), damping conversion, `GravityFactor`, `AllowedDOFs`, `LinearCast`, `AllowSleeping`, friction/restitution + combine rule pinned, `Teleport`, `MoveKinematic` | 1 (combine fact), 4 |
| Shapes: box (convex radius rule), sphere, capsule, cylinder, hull, mesh, height field or mesh, rotated/translated, scale (uniform for sphere/capsule + log once), compound with sub-shape → description index, per-space cache by (resource, ChangeCount, scale) | 3, 4 (`BuildRoot`, `ShapeIndexOf`), 5 (hits) |
| Shape building in the flush; Forest terrain benchmark | 3, 12 |
| Contacts and areas from the drained events; skip other pairs; overflow → queries, double capacity, log once; 2 cm contact start in the tests | 7, 9 (`ContactStartDistance`) |
| Queries: ray (`TreatConvexAsSolid = hitFromInside`), shape cast (scale 1), intersect shape (each body once), point | 5 |
| Character: `CastBody` (deepest point), penetrations (`CollideShape` with `MaxSeparationDistance`), `Distance` | 8 (B2 contract: `GetShapeCount` + `CollectNearbyShapes` + `Penetration`) |
| Determinism: no setting; render test on Jolt | 4 (`DeterministicSimulation = true`), 11 |
| `PhysicsHarness3D(PhysicsBackendKind)`; abstract suites with `_Jitter2`/`_Jolt`; Jolt skip with a reason | 6, 9 |
| Named tolerances in the harness | 6, 9 |
| Allocation gates on both | 10 |
| Render tests `physics-jolt`, `physics-debug-jolt`, determinism on Jolt, goldens per driver | 11 |
| Benchmarks per backend, Jolt numbers in `baseline.json`, terrain build | 12 |
| Demo Physics 3D backend toggle | 13 |
| physics.md Backends + Jolt columns; README/landing "3D physics: Jolt via BRUTAL, or Jitter2"; screenshots; CLAUDE.md | 13 (screenshot), 14 |
| B4 parity evidence | 14 |
| Two PRs (bodies + queries, then signals + characters) | boundary after Task 5 |

Spec statements the binding or the B2 contract override (all recorded in the BRUTAL doc by Task 14): no copy-on-write
pair array and shared broad-phase objects (Task 2); `TempAllocatorImplWithMallocFallback` instead of `TempAllocatorImpl`
(Task 4); `JobSystemSingleThreaded` instead of a 0-worker pool (Task 1); baked scales instead of `ScaledShape` (Task 3);
axis locks without re-creation and all-locked bodies held kinematic (Task 4); every body's touching set instead of
receiver-only tracking, and permanent query mode instead of a one-step rebuild on overflow (Task 7); the log bridge is
`Jolt.SetTraceHandler`/`SetAssertHandler`, not BRUTAL's logging (Task 1); B2's `CollectOverlaps(area, list) → bool` is
implemented from sensor events (Jolt) where Jitter2 queries shapes; the character contract's
`GetShapeCount`/`CollectNearbyShapes`/`Penetration` (per shape pair) replaces the spec's single `CollectPenetrations`.

Name consistency with the B2 plan: `PhysicsBackend3D` members and parameter names as in its contract listing;
`BackendBody(Index)`/`None`/`IsNone`, `BodyDescription3D(Kind, Position, Rotation, Layer, Mask, Properties, Friction, Bounce)`,
`BodyProperties3D(Simulated, LinearDampingPerStep, AngularDampingPerStep, GravityScale, AxisLocks, Ccd, CanSleep)`,
`BackendHit3D{Body, ShapeIndex, Position, Normal, Fraction}`, `BackendContact3D(Distance, Normal, Point)`,
`BackendShapeRef(Body, Shape)`, `ShapeError3D(ShapeIndex, Message)`, `ShapeKind3D`, `ShapeDescription3D{Kind, Resource,
ChangeCount, Size, Radius, Height, Points, Linear, Translation, MidHeight}`, `Shape3D.Describe/DescribeQuery/ChangeCount`,
`PhysicsSpace3D.Collides/MeasureLibraryAllocations/BackendKind`, `PhysicsServer3D.ResolveBackend`,
`PhysicsBackendKind.Jitter2/Jolt`, `JoltPhysicsSettings{VelocitySteps, PositionSteps, MaxBodies, MaxBodyPairs,
MaxContactConstraints, TempAllocatorMB, ContactEventCapacity}`, `PhysicsTraceTests`, `BackendSourceRulesTests.Rules`,
`PhysicsSeamTests`, `PhysicsBackendSelectionTests`, `MF_PHYSICS_TRACE`. New names introduced here and used consistently:
`JoltRuntime`, `JoltApiFacts`, `JoltMath.ToJolt/FromJolt`, `JoltLayerTable`, `JoltObjectLayerPairFilter`,
`JoltObjectVsBroadPhaseFilter`, `JoltNoAreasFilter`, `JoltShapeBuilder`, `ShapeKey`, `JoltShapeCache.Entry`,
`JoltNativeLedger`, `JoltChild`, `JoltBody`, `JoltBodyTable`, `JoltContactTracker`, `JoltQueries`, `JoltBackend`,
`PhysicsTolerances`, `RawJoltWorld`, `MF_TEST_NO_JOLT`, `ForceJoltUnavailableForTesting`.
