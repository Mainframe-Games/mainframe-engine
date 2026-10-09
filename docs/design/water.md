# Water

The current state of water in the engine: `Curve3D`, `River3D` geometry and the world's water queries (G8c.2–G8c.3,
[ADR 0155](../../memory/decisions/0155-curve3d-river3d-forest-project.md)). The full design (ponds from the terrain's water
layer, carving, falls, `WaterMaterial3D`, `SceneTextures`, audio) is the proposal
[future/water.md](future/water.md); what is built is listed under [Not yet](#not-yet).

## `Curve3D`

`MainframeEngine/Src/Resources/Curve3D.cs`: a `Resource` with a subset of Godot's `Curve3D` API and two per-point values
Godot does not have.

| Member | Notes |
|---|---|
| `AddPoint(position, in, out, index)`, `RemovePoint`, `ClearPoints`, `PointCount` | `in`/`out` are control offsets relative to the point (Godot) |
| `Get/SetPointPosition`, `In`, `Out`, `Tilt` | Godot's |
| `Get/SetPointWidth` (default 1 m), `Get/SetPointDepth` (default 0 m) | extensions: the **full** width and the centreline water depth |
| `Sample(index, t)` | the Bézier on one segment; out-of-range indices return the end points |
| `BakeInterval` (0.2 m), `GetBakedLength`, `GetBakedPoints`, `GetBakedDistances` | equal-arc-length baked points |
| `SampleBaked(offset, cubic)`, `SampleBakedWithRotation(offset, cubic, applyTilt)` | offsets clamp to 0 … length; cubic is Catmull-Rom through the baked points; the transform looks along the curve with **−Z** (the engine's forward) |
| `SampleBakedTilt/Width/Depth(offset)` | smoothstep between points |
| `GetClosestOffset(point)`, `GetClosestPoint(point)` | projection onto every baked segment |

- **Storage:** one exported `float[] Points`, 12 floats per point (position, in, out, tilt, width, depth). Every setter
  raises `Resource.Changed` and marks the bake dirty.
- **Baking** is lazy. Each Bézier segment is subdivided adaptively (at least 8, at most 1024 pieces, until the curve's
  midpoint is within 1 % of the interval of the chord's), then resampled at equal arc length (`ceil(length / interval)`
  steps, so the step is at most `BakeInterval`). Baked arrays are reallocated only when the baked count grows.
- **Determinism:** baking uses only `+ − × ÷` and `MathF.Sqrt` on floats, with scalar dot products in a fixed order
  (never `Vector3.Dot`, `Length` or `Lerp`, whose SIMD forms may round differently per CPU), so a curve bakes to the same
  bits on x64 and arm64. `Curve3DTests.FixedCurveBakesToACommittedHash` holds a hash of a fixed curve's bake.
- Sampling and closest-point queries allocate nothing.

## `River3D`

`MainframeEngine/Src/Scene/Nodes3D/Water/River3D.cs` (generation in `RiverBuilder.cs`): a `Node3D` holding a `Curve3D`
whose anchor Y is the water surface. It generates a ribbon and implements `IWaterBody3D`.

| Group | Exports (defaults) |
|---|---|
| — | `Curve`, `Material` (null: a blended blue `StandardMaterial3D`, alpha 0.6, until `WaterMaterial3D`) |
| Shape | `SectionLength` 1 m, `CrossSegments` 4, `UvLength` 4 m, `EnforceDownhill` on |
| Flow | `MinSpeed` 0.3 m/s, `MaxSpeed` 4 m/s, `SpeedPerSqrtSlope` 6, `SlopeWindow` 6 m |

Also `[Signal] Regenerated`, `Regenerate()`, `MeshData` (`RiverMeshData`), `Length`, `FlowSpeedAt(offset)`,
`SurfaceHeightAtOffset(offset)` and `Ribbon` (the generated child).

**Generation** runs when the curve raises `Changed` or a Shape/Flow export changes (immediately when the river is ready
in a tree, else on entering it, or lazily on the first query); never per frame. `RiverBuilder` is plain C#:

1. **Downhill clamp:** the baked heights are clamped so they never rise downstream (`y[i] = min(y[i], y[i−1])`); the
   curve itself is unchanged.
2. **Sections** every `SectionLength` of arc length (`ceil(length / SectionLength)` equal steps), each with the clamped
   surface height, the XZ tangent (the previous one where the curve is vertical), half the interpolated width, the depth
   and the flow speed.
3. **Flow speed:** `slope = (y(s − W/2) − y(s + W/2)) / arc distance` over `W = SlopeWindow` (shortened at the ends),
   `speed = clamp(MinSpeed + SpeedPerSqrtSlope × √slope, MinSpeed, MaxSpeed)`: 1 % ≈ 0.9 m/s, 10 % ≈ 2.2 m/s. The slope
   is per metre of arc, not of horizontal run.
4. **Ribbon:** `CrossSegments + 1` vertices per section at `x = −1 … 1` across (left bank to right bank, facing
   downstream), normals up, triangles counter-clockwise seen from above. **UV.u = 0 … 1 across, UV.v = arc length /
   `UvLength`.**
5. **`RiverMeshData`** (`Positions`, `Normals`, `UVs`, `Custom0`, `Indices`): `Custom0` is x = column depth
   `depth × (1 − x²)`, yz = flow along local X/Z (`tangent × speed × (1 − 0.6x²)`, slower at the banks), w = foam
   (`0.6 × saturate((speed − 2) / 2)`, rapids). The `ArrayMesh` gets positions, normals, UVs and indices; `Custom0` is
   wired to `MeshSurface.Custom0` when that stream lands (lane A2). Arrays are reused while the section count holds.
6. **Grid:** a uniform 4 m grid (river-local XZ) maps cells to the section segments whose quads overlap them.

The ribbon is an internal `MeshInstance3D` child named `Ribbon`, created in `OnReady` and **unowned**, so scenes never
save it. Rivers are meant to be moved and turned about Y; queries assume no tilt or scale.

## Water queries

`MainframeEngine/Src/Scene/Nodes3D/Water/WaterQueries.cs`. Every `IWaterBody3D` registers with its world's
`World3D.Water` while in the tree (`River3D` does so in `OnEnterTree`/`OnExitTree`; game types call `Register`).

```csharp
public interface IWaterBody3D { Aabb WaterBounds { get; } bool TrySample(Vector3 position, out WaterSample sample); }
public readonly record struct WaterSample(float SurfaceHeight, float ColumnDepth, Vector3 Flow);

var water = GetWorld3D()!.Water;
water.TrySample(p, out var sample);  // false when dry; the highest surface wins where bodies overlap
water.WaterDepthAt(p);               // bed-to-surface column, 0 when dry
water.SurfaceHeightAt(p);            // NaN when dry
water.FlowAt(p);                     // m/s, zero when dry or still
water.ImmersionAt(p);                // surface − p.Y; ≤ 0 above the water, 0 when dry
water.IsUnderwater(p, margin: 0.05f);
WaterQueries.WadeSpeedScale(immersion, start: 0.3f, full: 1.2f, minScale: 0.4f); // smoothstep 1 → 0.4
```

- A river sample is **inside** when the point lies between two section lines (each perpendicular to its section's
  tangent) and within the interpolated half width of the interpolated centreline, so bends have no gaps. Column depth
  and flow follow the ribbon's lateral profile.
- Bodies are skipped by their `WaterBounds` in XZ first. Main thread only; no query allocates (tested).

A character controller slows while wading and drifts with the flow (the Forest's `FirstPersonController`):

```csharp
var water = GetWorld3D()!.Water;
var scale = WaterQueries.WadeSpeedScale(water.ImmersionAt(GlobalPosition));
target = wish * (WalkSpeed * scale) + water.FlowAt(GlobalPosition) * FlowPush;   // FlowPush ≈ 0.3
```

## Not yet

From [future/water.md](future/water.md): ponds and lakes from the terrain's water layer (G8c.1, and the terrain as a
water body), falls, pond joins and the `Faceted` look (G8c.3), carving and the River3D editor tool (G8c.4), audio
(G8c.5), `SceneTextures` (G8c.6) and `WaterMaterial3D` (G8c.7–8; wave 2 of the forest slice). Buoyancy stays a sample.

## Tests

`Tests/MainframeEngine.Tests/Water/`: `Curve3DTests` (Bézier values, a quarter circle's length within 0.1 %, clamped
sampling, closest offset, width/depth/tilt interpolation, rotation, a scene round trip, `Changed` on every setter, 0 B
sampling, the committed bake hash), `RiverBuilderTests` (section count, UVs, normals and winding, bank order, the downhill
clamp, speed from slope, `Custom0`, array reuse, lateral sampling), `WaterQueriesTests` (registration, the unowned and
unsaved ribbon, regeneration on curve changes, inside/outside/edge, transforms, bends, overlaps, `WadeSpeedScale`, 0 B
queries).

## Related

[Physics](physics.md) · [Materials & meshes](materials-and-meshes.md) · [Forest](forest.md) ·
[future/water.md](future/water.md) · [ADR 0149](../../memory/decisions/0149-terrain-trees-water-engine-features.md) ·
[ADR 0155](../../memory/decisions/0155-curve3d-river3d-forest-project.md)
