# Terrain

## Purpose

`Terrain3D` draws and collides a square heightmap terrain from a `TerrainData` resource: chunked meshes with LOD,
per-chunk trimesh collision built from the exact render triangles, exact height/normal/surface/water queries and an
analytic raycast. Layers live as PNG images next to the scene. This is the core of G8a ([proposal](future/terrain.md),
[ADR 0149](../../memory/decisions/0149-terrain-trees-water-engine-features.md),
[ADR 0153](../../memory/decisions/0153-terrain3d-core.md), [ADR 0156](../../memory/decisions/0156-terrain-splat-material.md), foliage:
[ADR 0157](../../memory/decisions/0157-terrain-foliage-scatter.md)); the design is modelled on
[TerraBrush](https://github.com/spimort/TerraBrush) (MIT, © 2023 spimort) — no TerraBrush code or art is used.

Built so far: the data and its files, both profiles' knobs, the Realistic profile's geometry (smooth normals,
geomipmapped chunk LOD with skirts), collision (`CollisionMode.All`), queries, the edit API with undo tiles, the
scene-save hook, the Realistic look (`TerrainSplatMaterial3D` with `TerrainLayer`s) and [foliage scatter](#foliage)
(grass, ferns, pebbles). Not yet: the Faceted material (`TerrainMaterial3D`), object scatter, the foliage build
radius and painted density layers, the editor dock and brushes, `CollisionMode.NearBodies` (a stub that
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
| `TerrainLayer : Resource`, `TerrainSplatMaterial3D : Material` | the Realistic look ([below](#terrainsplatmaterial3d); in `Src/Rendering/Resources/`) |
| `TerrainLayerPacker`, `TerrainSplatGpu`, `TerrainSplatParams` (internal) | layer arrays, GPU state, parameter block (`Src/Rendering/Terrain/`) |
| `FoliageType : Resource` | one kind of foliage in `TerrainData.FoliageTypes` ([Foliage](#foliage), in `Terrain/Foliage/`) |
| `TerrainFoliage3D : Node3D` | `[Tool]` internal child (`Terrain3D.Foliage`): one `MultiMeshInstance3D` per (tile, type), thinning in `OnProcess` |
| `GrassMesh` | procedural `Clump`, `Fern`, `Rock` meshes with the foliage streams, and `CreateMaterial()` |

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

## TerrainSplatMaterial3D

The Realistic look (ADR 0156): `Terrain3D.Material = new TerrainSplatMaterial3D { Layers = [...] }`. Assigning it links
the material to the terrain (`TerrainSplatMaterial3D.Terrain`, runtime only; a scene load links it again), and the
chunks draw it with the terrain's splat maps; no other wiring. Another mesh, or a Faceted terrain, draws layer 0.

| `TerrainLayer` | Default | What |
|---|---|---|
| `Name`, `Tag` | "" | display name; game key (`Terrain3D.SurfaceTagAt(x, z)` returns the strongest layer's tag: footsteps) |
| `Albedo`, `Normal`, `Orm`, `Height` | null | sRGB colour; OpenGL normal map; occlusion/roughness/metallic; optional height (R) |
| `TilingMeters` | 4 | world metres per repeat (layer UV = world XZ ÷ tiling) |
| `HeightBlendContrast` | 0.2 | 0 = the higher layer wins outright; 1 = a soft weight blend |
| `Tint`, `NormalScale` | white, 1 | multiplies the albedo; scales the normal map's slopes |

| `TerrainSplatMaterial3D` | Default | What |
|---|---|---|
| `Layers` | [] | up to `MaxLayers` (8); index = splat channel (0–3 in `splat-0.png`, 4–7 in `splat-1.png`) |
| `LayerTextureSize` | 1024 | packed size of every layer image (3 × 8 × 1024² RGBA8 with mips ≈ 128 MB; 512 is a quarter) |
| `TriplanarStartDegrees` / `EndDegrees` | 35° / 45° | slope range over which every layer fades from planar to triplanar |
| `AntiTiling` | true | hex-tiling in the near band |
| `DetailDistance` / `FarDistance` / `FarTilingScale` | 60 m / 150 m / 4 | the near band to `DetailDistance`, the far band past `FarDistance`, both blended between |
| `MacroStrength` / `MacroScaleMeters` | 0.15 / 48 m | low-frequency brightness and warmth variation |

- **Packing** (`TerrainLayerPacker`, on the render thread when the layers' textures change): three `Texture2DArray`s of
  `LayerTextureSize`² with full GPU mips — albedo (sRGB), normal and ORM (linear) — one layer per `TerrainLayer`.
  Images of another size are box-filtered down or bilinearly resampled up with wrapping (the layers tile). Missing
  textures pack as white albedo, a flat normal and ORM (1, 0.9, 0).
- **Height** (the albedo array's alpha) comes from `Height` when set; else the albedo's own alpha when it has one (any
  texel below 255: the common "albedo + height" packing, also Godot Terrain3D's); else the ORM's occlusion (crevices are
  low); else 0.5.
- **Per fragment** (`Terrain/TerrainSplat.vk.frag`): both splat maps bilinear at the chunk UV → the four strongest
  weights (two in the far band; nothing painted = layer 0) → each layer sampled: planar on world XZ with an analytic
  frame (+X, +Z), hex-tiled in the near band, and/or triplanar (three plain lookups weighted by |N|⁴) on slopes →
  height blending `wᵢ' = max(hᵢ + wᵢ − (maxⱼ(hⱼ + wⱼ) − contrastᵢ), 0)`, normalised → albedo × tint, ORM, normals
  blended in world space after a whiteout blend per projection (Golus) → macro noise → `shadeLightsPbr` →
  `applyFog`. Every lookup is `SampleGrad` with gradients taken first, so skipped layers and the band and slope
  branches keep mip selection defined.
- **Hex-tiling** is written from Mikkelsen 2022 (JCGT 11(2)): a triangle grid over the UV plane, a random rotation and
  offset per vertex (a PCG hash), three lookups blended by barycentrics^7 weighted towards the brighter sample, normals
  rotated back. 9 fetches per layer instead of 3; off in the far band.
- **Cost** in texture fetches: 2 for the weights; per layer with a non-zero weight (at most 4 near, 2 far) 9 near
  (hex), 3 far, plus 9 on slopes (triplanar); the transition band between the distances pays both bands.
- **Rendering** is the mesh renderer's, with the material's own set 2 and pipeline layout
  ([Materials & meshes](materials-and-meshes.md#terrainsplatmaterial3d-adr-0156)): chunks keep LOD, skirts, culling,
  batching, picking and shadows (opaque casters, no material). Painting re-uploads the splat map through
  `GetSplatTexture`'s version, and the set follows.

## Macro texture (ADR 0175)

An "RVT-lite" (G8e.7): `Terrain3D.MacroTextureEnabled` (off by default) bakes the terrain's surface from above into a
`TerrainMacroTexture`: a two-layer `Texture2DArray` of `MacroTextureResolution`² (2048: 12.5 cm over 256 m; 32 MB RGBA8,
43 MB with mips) covering the terrain. Layer 0: albedo stored as its square root (precision in the shade) and roughness;
layer 1: the world normal's x and z, and the ground height as a 16-bit code in blue and alpha (`HeightMin` +
code × `HeightStep`, sub-millimetre). Surfaces with `StandardMaterial3D.TerrainBlend` blend towards it near the ground
([Materials & meshes](materials-and-meshes.md#terrain-blend-adr-0175)).

- **Bake** (`TerrainMacroTexture.Bake(data, material, resolution)`, CPU, every core): per texel the splat weights
  bilinear between cell centres, the four strongest, each layer's albedo and height at a 32² level of the packed layer
  images (`TerrainLayerPacker`), the splat shader's height blending, tints and macro noise (the same PCG value noise),
  each layer's mean roughness, and the terrain's smooth normal and bed height. A non-splat material bakes its albedo
  colour. ≈ 1.5–2 s at 2048² over eight 1024² layers (most of it decoding the layer images).
- **When**: on a worker thread when the terrain is built (the world blends from the frame it lands, a second or two
  later), and again half a second after the last edit (`OnDataEdited`; a stroke edits every frame) or a material change;
  `RebakeMacroTexture()` bakes now. `IsBakingMacroTexture` while the build's bake runs.
- **Binding**: the world's first visible terrain with a macro texture (`World3D.MacroTerrain`) is bound at set 0 binding
  7 (`FrameContext.TerrainMacroBinding`, a sampled image: shaders sample it with their own sampler and clamp the UV) with
  `FrameData.terrainMacroRect` (the corner's world XZ, 1 / size) and `terrainMacroHeight` (code 0's world height, metres
  per code, texels per side, 1 while bound). The render server uploads it the first frame it binds it and after each
  re-bake (`Texture2DArrayGpu`, deletion-queued). The terrain is translated, never rotated or scaled.
- **Not yet**: the proposal's distant-terrain shading from the macro and the probe baker's albedo from it.

## Foliage

`TerrainData.FoliageTypes` lists `FoliageType`s (append new ones at the end: the index is part of the placement hash).
The terrain's `TerrainFoliage3D` child (internal, unowned, unsaved, created with the chunks) draws each type as one
`MultiMeshInstance3D` per **tile** (a chunk, or a chunk ÷ `Subdivisions` per side), with `MaterialOverride` = the
type's `Material`, `CastShadows` = the type's and `VisibilityRangeEnd` = its `CullDistance`.

| `FoliageType` | Default | |
|---|---|---|
| `Mesh`, `Material` | — | e.g. `GrassMesh.Clump(...)` + `GrassMesh.CreateMaterial()` |
| `Density` | 1 | instances per m² at full weight (grid spacing 1/√density) |
| `Jitter`, `RandomYaw`, `ScaleMin`/`ScaleMax` | 1, on, 0.8/1.2 | offset in the grid cell, rotation about up, uniform scale |
| `AlignToNormal`, `SinkMeters` | 0.3, 0.03 | tilt towards `SmoothNormalAt`; base pushed down (× scale) |
| `Seed` | 0 | another arrangement |
| `LayerMask` | 0 | splat layers (bits) whose summed weight is the growth probability (Faceted: surface ids); 0 = everywhere |
| `SlopeMaxDegrees`, `HeightMin`/`HeightMax` | 40°, ±100 km | limits (the slope fades over its last 5°; heights terrain-local) |
| `CullDistance`, `ThinBand` | 60, 25 m | full density to cull − band, none at cull |
| `Subdivisions` | 1 | tiles per chunk side: smaller tiles follow the thinning more closely, more draws |
| `CastShadows` | off | |

- **Placement** is deterministic: one jittered grid per type over the whole map; each grid point's values (accept,
  jitter, tile fuzz, yaw, scale, thinning key) are SplitMix64 hashes of (seed, type index, grid x, grid z). A point
  grows when its accept hash is below the layer weight × slope fade, it is dry (no water on its triangle) and inside the
  height limits. Same data, same instances; tiling and neighbour rebuilds never move them.
- **Fuzzy tiles, smooth thinning.** A point belongs to the tile under its position moved by a hashed offset of up to
  half a tile on each axis. Each tile's instances are sorted by a uniform hash key, and every frame it draws the first
  `f · n` (`MultiMesh.VisibleInstanceCount`), `f` = clamp((`CullDistance` − d) / `ThinBand`, 0, 1) with d the camera's
  distance to the tile centre; at 0 the tile hides (`Visible`). As membership near an edge is shared at random, the
  drawn density between tile centres blends their factors linearly — no line at tile edges or at the cull distance;
  single instances pop. 0 B and no upload: a `VisibleInstanceCount` change only moves the draw count (see
  [MultiMesh](materials-and-meshes.md#multimesh-adr-0151)), and tiles set `MultiMesh.CustomAabb`.
- **Runtime quality scales** ([ADR 0180](../../memory/decisions/0180-forest-settings-menu.md)): `Terrain3D.FoliageDensityScale`
  (0–1) multiplies `f`, so every tile draws an even share of its sorted instances, and `Terrain3D.FoliageDistanceScale`
  (0.1–1) multiplies `CullDistance` and `ThinBand`. Both apply the next frame without a rebuild or an upload (a settings
  menu's ground cover density and distance; not saved). Above 1 would need a rebuild (`VisibilityRangeEnd`, the tiles'
  instance counts), so they only reduce.
- **Rebuilds.** A `Changed` with heights, splat weights or water rebuilds the tiles within half a tile (+ a vertex) of
  the touched cells; user channels rebuild nothing. A changed type (or its mesh) rebuilds that type; a new list
  rebuilds all. Every tile of the map is placed at load (no build radius yet).
- **Meshes** (`GrassMesh`, vertex colours, no textures): `Clump(blades, height, width, bend, seed)` — tapered blades
  of four segments crossing at random yaws on a small disc, curving outward; `Fern(fronds, length, width, leaflets,
  seed)` — fronds arching up and drooping with a leaflet triangle each side per step; `Rock(radius, roughness, seed)`
  — a flattened, lobed icosphere sunk into the ground (draw it with `StandardMaterial3D`). Plants carry `Custom0` =
  (wind weight 0 at the root → 1 at the tip, 1 = leaf flutter, per-blade phase, AO darker at the root), UV v = 0 at the
  tip, colours root → tip, and normals leaning halfway to up; `CreateMaterial()` is a `FoliageMaterial3D` with no
  cut-out, `BackFace = Keep`, translucency 0.45, PBR, wind strength 0.6 and bend 0.5.

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
| Foliage: place 472k instances (grass 7/m² in 16 m tiles, ferns, pebbles; 384 tiles) | ≈ 0.2–0.3 s at load |
| Foliage: per-frame thinning over 384 tiles | ≈ 8 µs, 0 B |
| Foliage: a 20 × 20 vertex `SetHeights` (terrain + foliage tiles) | ≈ 7–11 ms |
| Foliage drawn around a walker (grass to 45 m) | ≈ 27k clumps, 46 draws; GPU-bound ≈ 8.7 ms per frame at 1920 × 1080, ≈ 10.2 ms at 2560 × 1440 (≈ 8.3 ms display-capped without foliage) |

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
- Foliage: `TerrainFoliageTests` (bit-identical placement wherever the terrain is and whatever the tiling, instances
  on `HeightAt`, density = the masked weights ± 6 %, water/slope/height exclusions, edits rebuild only nearby tiles,
  distance thinning monotonic within ± 8 % of the factor and hidden past the cull distance, unsaved, list changes);
  `TerrainFoliageAllocationTests` (0 B frames and updates with a moving camera); `GrassMeshTests` (streams, winding,
  determinism, closed outward rocks).
- PNG: [`PngGray16Tests`](../../Tests/MainframeEngine.Tests/Imaging/PngGray16Tests.cs).
- Splat material ([`TerrainSplatMaterialTests`](../../Tests/MainframeEngine.Tests/Terrain/TerrainSplatMaterialTests.cs)):
  packing (sizes, colour spaces, resampling both ways, defaults, the height sources in order, the content stamp),
  the parameter block, layer subscription and the 8-layer cap, the shader set, the terrain link and `SurfaceTagAt`,
  and a scene round trip; the fragment-stage budget with the terrain set is in `ImageBasedLightingTests`.
- Render ([`TerrainFoliageRenderTests`](../../Tests/MainframeEngine.RenderTests/TerrainFoliageRenderTests.cs), scene
  `terrain-foliage`): grass, ferns and pebbles on the hill in the wind, golden at frame 30 (t = 0.5 s); a 0 B gate
  while the camera walks (tiles thin, hide, return); and a report of the cost on a 256 m map (`--count 1`, against
  `--count 2` without foliage).
- Render ([`TerrainRenderTests`](../../Tests/MainframeEngine.RenderTests/TerrainRenderTests.cs), scene `terrain`): a
  64 m noise hill at 0.5 m lit and shadowed by a low sun, golden at frame 10 (moltenvk, lavapipe), self-checks (one
  level per chunk, coarser far chunks, ray = `HeightAt`), and a 0 B allocation gate while the camera orbits.
- Render, scene `terrain-splat` (`TerrainSplatScene`): rolling ground, a mound and a plateau behind a steep cliff,
  four layers generated in code (grass, dirt with pebbles, layered rock, moss; height in the albedo alpha): a
  triplanar rock face, a dirt path and moss patches height-blended into the grass, the far band behind. Golden at
  frame 10 (moltenvk, lavapipe), self-checks (`SurfaceTagAt` on the cliff, the path and the meadow; every chunk drawn), and a
  0 B allocation gate while the camera orbits.

## Known gaps (next waves)

- Painting re-uploads whole splat maps; `Texture2D.SetPixelsRect` + region uploads are the fix (G8a.15).
- The splat material has no `MacroColor` map, no per-layer triplanar/anti-tiling flags (every layer goes triplanar on
  slopes, every layer hex-tiles near) and no Blinn-Phong fallback; packing (decoding and resampling every layer image)
  runs synchronously on the render thread when a layer texture changes.
- Foliage: the build radius (place only chunks near the camera, from a buffer pool), painted `foliage-*.png` density
  layers, `ChannelCutoff`, the editor Foliage mode, and `FoliageMaterial3D.FadeMode.Shrink` (G8b) so thinning
  instances shrink instead of popping.
- `CollisionMode.NearBodies`, the analytic physics-ray registration (decision 2 of the proposal), objects,
  the Faceted `TerrainMaterial3D`, the editor dock, brushes and `TerrainEditAction`.
- Inspector edits of a loaded `TerrainData`'s layout knobs throw (the dock's "Create terrain data" will own them).
- Inline (never saved) terrain data does not survive an editor code reload (snapshots carry knobs, not pixels).
- When G6.4 lands, the per-level meshes can become index-only `MeshSurface.Lods` over one vertex buffer.

## Related docs

[Proposal](future/terrain.md) · [Physics](physics.md) · [Materials & meshes](materials-and-meshes.md) ·
[Scene serialization](scene-serialization.md) · [Testing](testing.md)
