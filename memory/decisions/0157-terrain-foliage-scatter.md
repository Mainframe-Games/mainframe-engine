# ADR 0157 — Terrain foliage scatter

- **Date:** 2026-10-08
- **Status:** accepted
- **Milestone:** Gameplay toolkit G8a (G8a.8 foliage subset, G8a.16 realistic grass), forest slice wave 2 lane F2
- **Spec:** docs/design/future/terrain.md (Scatter); current state in docs/design/terrain.md#foliage

## Context

The forest needs grass, ferns and pebbles over its 256 × 256 m terrain: dense grass near the walker (about 30 m),
swaying in the shared wind, with no visible line where it thins out, and cheap enough per frame (under ~1.5 ms CPU).
`MultiMesh`, `FoliageMaterial3D` and the foliage `Custom0` layout exist (ADR 0151); `Terrain3D` exists (ADR 0153). The
proposal's painted foliage density layer, build radius, editor Foliage mode and `FoliageMaterial3D.FadeMode.Shrink`
(G8b) do not.

Two engine facts shaped the design:
- **`MultiMesh` visibility is per node**, and before this lane a `VisibleInstanceCount` change bumped the version, so
  `MultiMeshGpu` built a new instance buffer (allocations, an upload) and `GetAabb` walked the drawn instances.
  Thinning by count every frame would have allocated.
- **Per-tile counts make steps.** Drawing a prefix of a tile's hash-sorted instances thins evenly inside the tile, but
  neighbouring tiles at different factors meet at a straight density step, and terrain chunks are 32 m.

## Decision

- **Where it lives.** As the proposal says: `TerrainData.FoliageTypes` (`FoliageType[]`, exported, append-only since
  the index is part of the hash) and an internal, unsaved `TerrainFoliage3D` child that `Terrain3D` creates with its
  chunks (`Terrain3D.Foliage`). `Terrain3D` gained four lines; the foliage listens to `Terrain3D.Changed`.
- **`FoliageType`** (resource): `Mesh`, `Material` (override), `Density` per m², `Jitter`, `ScaleMin`/`ScaleMax`,
  `RandomYaw`, `AlignToNormal`, `SinkMeters`, `Seed`; limits `LayerMask` (splat layers whose summed weight is the
  growth probability; Faceted: surface ids; 0 = everywhere), `SlopeMaxDegrees` (5° fade), `HeightMin`/`HeightMax`;
  distance `CullDistance`, `ThinBand`, `Subdivisions` (tiles per chunk side); `CastShadows` (off).
- **Placement.** One global jittered grid per type, spacing 1/√density. Every value of a grid point (accept, jitter,
  tile fuzz, yaw, scale, key) is a SplitMix64 hash of (seed, type index, grid x, grid z). This is finer than the task's
  "seeded by chunk coordinate and type": a chunk's instances still depend only on its own area, and no instance moves
  when a neighbour is rebuilt or the tiling changes. A point grows when its hash is below the layer weight × slope fade
  and is dry and inside the height limits. No water, no painted density layer (none exists yet; the weights alone
  drive density).
- **Fuzzy tiles.** Each (tile, type) is one `MultiMeshInstance3D` (a tile = a chunk, or a chunk ÷ `Subdivisions`). A
  point belongs to the tile under its position moved by a hashed offset of up to half a tile per axis. A tile's
  instances are sorted by a uniform hash key; each frame it draws the first `f · n` (`VisibleInstanceCount`), `f` = 1
  up to `CullDistance − ThinBand` falling linearly to 0 at `CullDistance` (from the camera to the tile centre), and it
  hides at 0. Because membership is shared at random across tile edges, the drawn density between two tile centres is
  the linear blend of their factors: continuous, so no line at tile edges and none at the cull distance.
  `VisibilityRangeEnd` = `CullDistance` as a safety net for other views.
- **0 B thinning.** `MultiMesh` now keeps an internal `ContentVersion` (mesh, count, transforms) apart from `Version`;
  `MultiMeshGpu` uploads every instance and, on a `VisibleInstanceCount`-only change, just updates its draw count and
  bounds. `MultiMesh.CustomAabb` (runtime only) skips the bounds walk; foliage sets it per tile. Existing behaviour and
  goldens are unchanged (bounds still cover the drawn instances when no custom box is set).
- **Rebuilds.** `Changed` with heights, splat weights or water rebuilds the tiles of every type within half a tile
  (+ one vertex) of the touched cells. Changing a type (its `Version` or its mesh's) rebuilds that type; replacing the
  list rebuilds all. User-channel edits rebuild nothing.
- **Meshes.** `GrassMesh.Clump(blades, height, width, bend, seed)` (tapered, curved strips crossing at random yaws on a
  small disc), `GrassMesh.Fern(...)` (fronds arching from the centre with leaflet triangles), `GrassMesh.Rock(...)`
  (a flattened lobed icosphere) and `GrassMesh.CreateMaterial()`. Vertex colours only, no texture: geometry blades need
  no cut-out, do not shimmer and cost no fill-rate on transparent texels. `Custom0` = (wind weight 0 root → 1 tip,
  1 = leaf flutter, per-blade phase, AO darker at the root); UV v = 0 at the tip; normals lean halfway to up and the
  material keeps them on both faces (`FoliageBackFace.Keep`), so clumps shade evenly from every side.
- **No shader changes.** `FadeMode.Shrink` belongs to G8b's `FoliageMaterial3D`; the fuzzy tiles make it unnecessary
  for density, at the cost of individual instances popping (not lines).

## Consequences

- Measured (Apple M5, Release, 256 m at 0.5 m, 32 m chunks; grass 7/m² in 16 m tiles to 45 m, ferns and pebbles,
  384 tiles): placing 472k instances ≈ 0.2–0.3 s at load; the per-frame update ≈ 8 µs; a 20 × 20 vertex height edit
  ≈ 7–11 ms including the terrain's own chunk rebuild; ~27k clumps drawn around the walker, 46 draws. Frame
  CPU stays within noise of the terrain alone (the render host's CPU figure is paced at 120 Hz); the GPU is the cost:
  ≈ 8.7 ms per frame at 1920 × 1080 and ≈ 10.2 ms at 2560 × 1440 (≈ 8.3 ms without foliage, display-capped).
  Most of it is the wind's per-vertex simplex noise over ~3.9M vertices: tune blades and density per scene.
- The whole map's instances are placed at load (no build radius): fine at 256 m, not at 1 km. The proposal's build
  radius (nearest chunks first, a pool of buffers) is the next step, then the painted `foliage-*.png` layers, the
  editor Foliage mode and `ChannelCutoff`.
- Instances near the thinning edge pop individually; `FadeMode.Shrink` (G8b) would let them shrink instead.
- `TerrainLayers.Foliage` stays reserved.
