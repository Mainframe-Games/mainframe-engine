# Terrain

## Purpose

`Terrain3D` draws and collides a square heightmap terrain from a `TerrainData` resource: chunked meshes with LOD,
per-chunk trimesh collision built from the exact render triangles, exact height/normal/surface/water queries and an
analytic raycast. Layers live as PNG images next to the scene. This is the core of G8a ([proposal](future/terrain.md),
[ADR 0149](../../memory/decisions/0149-terrain-trees-water-engine-features.md),
[ADR 0153](../../memory/decisions/0153-terrain3d-core.md)); the design is modelled on
[TerraBrush](https://github.com/spimort/TerraBrush) (MIT, © 2023 spimort) — no TerraBrush code or art is used.

Built so far: the data and its files, both profiles' knobs, the Realistic profile's geometry (smooth normals,
geomipmapped chunk LOD with skirts), collision (`CollisionMode.All`), queries, the edit API with undo tiles, and the
scene-save hook. Not yet: the splat material (`TerrainSplatMaterial3D`, wave 2), the Faceted material
(`TerrainMaterial3D`), foliage/object scatter, the editor dock and brushes, `CollisionMode.NearBodies` (a stub that
builds every chunk) and sub-rectangle texture uploads.

## Key types

All in [`Src/Scene/Nodes3D/Terrain/`](../../MainframeEngine/Src/Scene/Nodes3D/Terrain/).

| Type | What |
|---|---|
| `TerrainData : Resource` | knobs (exported) + layers (in memory, PNG on disk); quantising, load/save, edits |
| `Terrain3D : Node3D` | `[Tool]` node: builds chunks and collision, LOD in `OnProcess`, queries, edit forwarding, `Changed` |
| `TerrainChunk3D : GeometryInstance3D` | one LOD level of one chunk; internal, unowned, unsaved |
| `TerrainGrid` (struct) | the grid maths: diagonal rule, `HeightAt`, normals, `Raycast` (2D DDA over chunks, then quads) |
| `TerrainEdit`, `TerrainEditRecord`, `TerrainEditTile` | `Begin(layers)` / `End()`: before/after chunk tiles, `Undo()` / `Redo()` |
| `TerrainProfile`, `TerrainDiagonal`, `TerrainLayers`, `TerrainCollisionMode` | enums |
| `TerrainHit`, `TerrainChange` | `(Position, Normal, Distance)`; `(Layers, Cells, Chunks, FromUndo)` |

## Coordinates and grid

- **Origin at the map corner.** Terrain-local X and Z run from 0 to `SizeMeters`; world = `GlobalPosition` + local.
  Rotation and scale on the node are not supported (chunks inherit them, queries ignore them).
- `N = SizeMeters / VertexSpacing` quads per side; vertex `(i, j)` sits at `(i·s, h, j·s)`. Heights are stored row by
  row (`j·(N+1) + i`), X along a row, Z down the rows, row 0 first — the same layout as the images.
- **Cells** (paint layers): one per quad (Realistic, `CellSize = VertexSpacing`) or four per quad (Faceted, half a quad).
- **Chunks**: `ChunkQuads = ChunkMeters / VertexSpacing` quads per side, even and dividing `N` (`Validate`). Neighbours
  repeat their shared edge vertices from the same heights, so edges match bit for bit.
- **Diagonal rule.** `Checkerboard` (default) splits quad `(i, j)` along A `(i+1, j)–(i, j+1)` when `i + j` is even,
  B `(i, j)–(i+1, j+1)` when odd; `Uniform` is A everywhere. Triangles are counter-clockwise from above: A
  `(00, 01, 10)`, `(10, 01, 11)`; B `(00, 11, 10)`, `(00, 01, 11)`. Chunk origins are even, so every chunk (and every
  LOD level's own grid) shares one index pattern.

| Knob (`TerrainData`) | `Create(Realistic, …)` | `Create(Faceted, …)` | Notes |
|---|---|---|---|
| `Profile` | Realistic | Faceted | fixed once loaded |
| `SizeMeters` | caller | caller | ≤ 2048 (Realistic), ≤ 1024 (Faceted); ≤ 2049² vertices |
| `VertexSpacing` | caller (0.5–1) | caller (2) | |
| `ChunkMeters` | 64 quads' worth | 16 quads' worth | the forest: 32 m at 0.5 m (64 quads, 8 × 8 chunks for 256 m) |
| `HeightMin` / `HeightMax` | −64 / 192 m | −16 / 48 m | 16-bit range: 3.9 mm / 1 mm steps |
| `Diagonal` | Checkerboard | Checkerboard | |
| `MaxWaterDepth` | 1.2 m | 1.2 m | depth of a full water layer |
| `CollisionLayer` / `CollisionMask` | 1 / 0 | 1 / 0 | applied to the chunk bodies |
| `CollisionMode` | `All` | `All` | `NearBodies` is a stub (= `All`); `None` builds no bodies |
| `CastShadows` | true | true | applied to the chunks |

The layout knobs throw once the layers are loaded (`EnsureLoaded`); change them by importing into new data.

## Layers and files

`TerrainData` holds no pixels in its JSON. The layers are images in its folder (`LayerFolder`: the folder of
`ResourcePath`, or set explicitly):

| File | Profile | Format | Contents (default) |
|---|---|---|---|
| `terrain.mres` | both | JSON | the knobs |
| `heightmap.png` | both | 16-bit grey, (N+1)² | `h = HeightMin + v / 65535 · (HeightMax − HeightMin)` (flat at 0) |
| `splat-0.png`, `splat-1.png` | Realistic | RGBA8, cells | weights of layers 0–3 and 4–7, sum 255 (layer 0) |
| `surface.png` | Faceted | RGBA8, cells | R = surface id (0) |
| `user.png` | both | RGBA8, cells | four game channels (0) |
| `water.png` | both | RGBA8, (N+1)² | R = depth fraction of `MaxWaterDepth`; G, B, A reserved (0) |

- **Heights are quantised as they are written** (`QuantizeHeight`), so save → load gives the same floats.
- A missing file loads as its default; code-made data (`TerrainData.Create`) works with no folder at all — the forest
  generates its terrain in code (`SetHeightsFrom`, `SetWeightsFrom`, `SetWaterDepthFrom`).
- `SaveLayers(folder)` writes only dirty layers (all non-default ones when the folder changes), each to `x.tmp` then
  renamed; a layer back at its default deletes its file. No `.png.meta` sidecars are written yet.
- **Water and the bed.** The stored height is the water surface; the **bed** (`BedHeights` = height − depth) is what is
  drawn, collided and queried. `HeightAt + WaterDepthAt` is the water surface. Wet chunks draw a water surface
  (`GetChunkWater`, `WaterMaterial`, default a `WaterMaterial3D`) and the terrain is an `IWaterBody3D` in
  `World3D.Water` ([water.md → ponds](water.md#ponds), ADR 0159).
- **Carve records.** `River3D.Carve` keeps the heights it replaced in the data, saved by `SaveLayers` as
  `carve_<id>.json` ([water.md → carving](water.md#carving)).
- **Scene save.** `SceneSaver.Save` first calls the internal `ISceneSaveHook` on every node under the root. A
  `Terrain3D` with inline data saves it as `<scene>_terrain/terrain.mres` (it becomes external, so the scene references
  it by UID) and writes the layers there; external data rewrites its `.mres` and dirty layers in its own folder. A
  throwing hook fails the save before the scene file is written. (`SceneSaver.ToJson`, used by editor snapshots, runs
  no hook.)
- `GetSplatTexture(0|1)`: the splat maps as linear, unmipmapped, bilinear, clamped `Texture2D`s (texel centres at
  cell centres: UV = local XZ ÷ size), kept in sync with edits — each edit re-uploads the whole map for now.

## Chunks and LOD

`Terrain3D.OnReady` (and a `Data` change while ready) builds, per chunk, one `TerrainChunk3D` per LOD level plus one
`StaticBody3D` (a `CollisionShape3D` with a `ConcavePolygonShape3D`). They are children with no `Owner`, so the scene
writer skips them and a viewport click selects the terrain. Replacing `Data` frees and rebuilds them; leaving the tree
keeps them (an edit made meanwhile refreshes every chunk on re-entry).

- **Meshes.** One `ArrayMesh` per (chunk, level): chunk-local positions (the node sits at the chunk origin), smooth
  normals from central differences of the full heightmap (one-sided at the map edge, so seams agree), UV = terrain-local
  XZ ÷ `SizeMeters`. All chunks of a level share one index array. Drawn by the normal mesh renderer (culling,
  batching, shadows, ID picking) with `Terrain3D.Material` (any `Material`; null: `Terrain3D.DefaultMaterial`, matte grey).
- **Geomipmapping with skirts (Realistic).** Up to four levels (vertices every 1st, 2nd, 4th, 8th quad; fewer when a
  level would have an odd quad count). Each level is a separate compact mesh (≈ 1.33× the LOD 0 vertices) because G6.4's
  index-only `MeshSurface.Lods` do not exist yet and switching a node's mesh releases its GPU buffers; showing one
  level means toggling `Visible` on prebuilt nodes, which is free. Every level carries a skirt: each edge vertex repeated
  below by the chunk's worst level error + one quad, triangles in both windings, so a chunk next to any level shows no
  crack. Skirts on the map border collapse (zero depth, degenerate).
- **Selection** (`OnProcess`, `[Tool]` so it also runs in the editor with its camera): per chunk, the coarsest level
  whose error (the largest height difference from LOD 0 over the chunk, `GetChunkLodError`) projects to at most 1 px at
  the camera's distance to the chunk box — `error · 935 · LodBias ≤ distance` (1080 px over 60°). With fine noise the
  1 px rule keeps LOD 0 out to ~50 m per 5 cm of error; `LodBias` < 1 drops levels sooner. Shadow casters draw the
  level the camera sees. Allocation-free.
- **Faceted** builds one level and no skirts.

## Collision

One `StaticBody3D` per chunk (at the chunk origin) whose `ConcavePolygonShape3D.Faces` are copied from the chunk's
LOD 0 render triangles (same heights, diagonal and winding — bit for bit). `HeightMapShape3D` is not used: its origin
and diagonal differ. An edit rewrites the faces array in place and reassigns it (the body rebuilds at the next step);
inside a `TerrainEdit` the touched chunks' collision waits for `End()`. See [Physics](physics.md#terrain-collision).

## Queries

World space, metres; outside the map they clamp to the edge. Main thread, 0 B.

| Query | Returns |
|---|---|
| `Contains(x, z)` | whether the point is over the map |
| `HeightAt(x, z)` | ground (bed) height on the drawn LOD 0 triangle — exact, not bilinear |
| `NormalAt(x, z)` | that triangle's face normal (what physics sees) |
| `SmoothNormalAt(x, z)` | vertex normals interpolated across the triangle (placement, shading) |
| `SurfaceAt(x, z, ignoreWater)` | strongest splat layer (Realistic; ties → lower index) or surface id (Faceted); `WaterSurface` (255) where there is water |
| `LayerWeightAt(layer, x, z)` | the cell's weight of a splat layer, 0..1 |
| `UserChannelAt(channel, x, z)` | the cell's user channel byte |
| `WaterDepthAt(x, z)` | water depth above the bed, interpolated on the same triangle; 0 when dry |
| `Raycast(origin, dir, max, out TerrainHit)` | analytic: clip to the map box, 2D DDA over chunks (each chunk's min/max rejects), then quads, both triangles from either side; vertical rays read `HeightAt` |

## Edits

Edits go through `Terrain3D` or straight to `TerrainData` (the terrain listens to the data's internal `Edited` event, so
both rebuild):

- heights: `GetHeights` / `SetHeights(Rect2I vertices, ReadOnlySpan<float>)`, `SetHeightsFrom(Func<x, z, h>)`;
- splat weights (Realistic): `SetWeights(Rect2I cells, ReadOnlySpan<float>)` (8 per cell, normalised to sum 255 by
  largest remainders), `SetWeightsFrom(TerrainWeightGenerator)`, `PaintSurface(center, radius, layer, strength)` (the
  layer moves toward 1, the others scale; Faceted: writes the id);
- water: `SetWaterDepth(Rect2I vertices, depths)`, `SetWaterDepthFrom(...)`;
- raw texels: `GetCells` / `SetCells(TerrainLayers, image, Rect2I, uint RGBA)` for Surface (splat 0/1 or surface),
  User and Water.

Every write raises `Terrain3D.Changed(TerrainChange)` synchronously: the layers, the touched cells (for height/water:
the quads around the touched vertices) and the chunk rectangle. Height and water writes rebuild the meshes (all levels,
errors and skirts) of every chunk holding a vertex within one of the rectangle (normals use neighbours) and their
collision; paint rebuilds nothing. `Terrain3D.Edit.Begin(layers)` records, the first time a write touches a chunk, its
tile of each recorded layer (heights `(ChunkQuads + 1)²`, cells per splat image); `End()` rebuilds the deferred
collision and returns a `TerrainEditRecord` whose `Undo()` / `Redo()` write the tiles back (raised with `FromUndo`).

## Performance

Apple M5, Release, the forest's 256 m at 0.5 m (513² vertices, 8 × 8 chunks of 64 quads, 524k collision triangles):

| What | Measured |
|---|---|
| Generate heights (`SetHeightsFrom`) | ≈ 30 ms |
| Build chunks (4 levels each) | ≈ 40 ms |
| First physics step (Jitter2 builds 524k `TriangleShape`s) | ≈ 2.1 s, then ≈ 0.65 s over the next 60 steps; ≈ 0.03 ms per step after |
| `HeightAt` | ≈ 23 ns, 0 B |
| `Raycast` across the map | ≈ 1 µs, 0 B |
| A 20 × 20 vertex `SetHeights` | ≈ 3 ms (+ ≈ 0.2 s for the next step's Jitter2 rebuild of the touched chunks) |
| Steady-state frame | 0 B (unit gate with a walking character; render gate with an orbiting camera through LOD switches) |

The collision load cost is Jitter2's per-triangle shapes; `CollisionMode.NearBodies` (building only chunks near moving
bodies) is the planned fix for larger maps.

## Testing

- Unit ([`Tests/MainframeEngine.Tests/Terrain/`](../../Tests/MainframeEngine.Tests/Terrain/)): `TerrainGridTests`
  (diagonal rule, winding, `HeightAt` on every triangle plane and continuous across edges, clamping, face/smooth
  normals, raycast against brute force for random, vertical, grazing, from-below and missing rays);
  `TerrainDataTests` (knobs and validation, idempotent quantising over all 65536 values, exact save/load, defaults
  not written, dirty-only writes, weight normalisation, `SurfaceAt`, splat texture sync, water bed);
  `Terrain3DTests` (headless tree: unowned unsaved chunks, mesh layout and UVs, collision faces = LOD 0 triangles bit
  for bit, `HeightAt` = drawn triangles, world raycast, edits rebuild only touched chunks and raise `Changed`, LOD errors
  and selection, skirts, undo/redo bit-exact, data replacement, Faceted, water, a `CharacterBody3D` capsule standing
  1 cm (its safe margin) above the surface, a sphere resting on it, physics rays agree); `TerrainSaveTests` (scene
  save writes `<scene>_terrain/`, reload); `TerrainAllocationTests` (0 B queries and frames).
- PNG: [`PngGray16Tests`](../../Tests/MainframeEngine.Tests/Imaging/PngGray16Tests.cs).
- Render ([`TerrainRenderTests`](../../Tests/MainframeEngine.RenderTests/TerrainRenderTests.cs), scene `terrain`): a
  64 m noise hill at 0.5 m lit and shadowed by a low sun, golden at frame 10 (moltenvk, lavapipe), self-checks (one
  level per chunk, coarser far chunks, ray = `HeightAt`), and a 0 B allocation gate while the camera orbits.

## Known gaps (next waves)

- `TerrainSplatMaterial3D` (wave 2) reads `GetSplatTexture`; it will want `Texture2D.SetPixelsRect` + in-place region
  uploads so painting does not re-upload whole maps.
- `CollisionMode.NearBodies`, the analytic physics-ray registration (decision 2 of the proposal), foliage and objects,
  the Faceted `TerrainMaterial3D`, the editor dock, brushes and `TerrainEditAction`.
- Inspector edits of a loaded `TerrainData`'s layout knobs throw (the dock's "Create terrain data" will own them).
- Inline (never saved) terrain data does not survive an editor code reload (snapshots carry knobs, not pixels).
- When G6.4 lands, the per-level meshes can become index-only `MeshSurface.Lods` over one vertex buffer.

## Related docs

[Proposal](future/terrain.md) · [Physics](physics.md) · [Materials & meshes](materials-and-meshes.md) ·
[Scene serialization](scene-serialization.md) · [Testing](testing.md)
