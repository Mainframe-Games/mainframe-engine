# ADR 0153 — Terrain3D core: image layers, chunk grid, collision

- **Date:** 2026-10-08
- **Status:** accepted
- **Milestone:** Gameplay toolkit G8a (G8a.1 PNG part, G8a.3, G8a.13 geometry), forest slice lane C
- **Spec:** docs/design/future/terrain.md; current state in docs/design/terrain.md

## Context

ADR 0149 made terrain an engine feature with two profiles. The forest showcase needs a 256 × 256 m Realistic terrain at
0.5–1 m spacing that it generates in code, stands a first-person character on, and queries for placement. The splat
material, scatter and editor tools come in later waves, so the core has to stand on its own: data, files, meshes,
LOD, collision, queries and edits.

Three things the proposal assumed did not exist:
- **16-bit PNG.** The codec read and wrote 8-bit RGB/RGBA only.
- **G6.4 index-only LODs** (`MeshSurface.Lods`), which the proposal's geomipmapping was to sit on.
- **A scene-save hook.** `SceneSaver.Save` wrote only the scene, and the editor viewport-tools API (G8a.2) that was to
  carry `SaveExternalData` is not built.

## Decision

- **Data.** `TerrainData : Resource` exports only knobs: `Profile`, `SizeMeters`, `VertexSpacing`, `ChunkMeters`,
  `HeightMin`/`HeightMax`, `Diagonal`, `MaxWaterDepth`, collision layer/mask/mode, `CastShadows`. The layout knobs
  throw once the layers are loaded.
  - The layers live in memory as float heights (quantised to the 16-bit step on every write) and RGBA8 images: splat
    0/1 or surface, user, and water.
  - On disk they are PNGs in the resource's folder. Missing files load as defaults, so `TerrainData.Create(...)` works
    with no files: the forest's path.
  - The ground is the **bed**: height minus water depth. Meshes, collision and queries all use the bed.
- **Names.** We used the task's knob names (`VertexSpacing`, `ChunkMeters`) rather than the proposal's `Spacing`,
  `ChunkQuads` and `CellSize`. `ChunkQuads`, `CellSize` and `CellsPerSide` are derived read-only properties. The cell
  size is the spacing (Realistic) or half of it (Faceted).
- **Save hook.** We added an internal `ISceneSaveHook`, which `SceneSaver.Save` runs on every node under the root
  before it writes the scene.
  - `Terrain3D` uses it to write `<scene>_terrain/terrain.mres` and the dirty layers (each to a temp file, then
    renamed).
  - Inline data becomes external at that point, so the scene references it by UID.
  - The editor's `EditorSession.Save` goes through `SceneSaver.Save`, so it gets the hook for free. The viewport-tools
    `SaveExternalData` can call the same code later.
- **Chunks.** Each chunk gets one internal `TerrainChunk3D : GeometryInstance3D` per LOD level: unowned and unsaved,
  overriding `GetRenderMesh`. They draw through the unchanged `MeshRenderer` with `Terrain3D.Material`.
  - Each level is its own compact `ArrayMesh`, and the chunks of a level share one index array.
  - Switching levels toggles `Visible`. We did not swap meshes on one node: in `MeshRenderer.Sync`, a mesh change
    releases the old GPU mesh and uploads the new one, which costs a re-upload and allocations on every switch. Hidden
    nodes keep their buffers.
  - Memory is about 1.33× the LOD 0 vertices. When G6.4 lands, the levels can become index-only LODs over one vertex
    buffer.
- **LOD.** Geomipmapping with skirts (ADR 0149), up to 4 levels.
  - Each level's skirt hangs one quad below the chunk's worst level error. It is drawn in both windings and collapses
    on the map border.
  - The selection runs in `OnProcess`, and `Terrain3D` is `[Tool]` so the editor camera drives it too. It picks the
    coarsest level whose error projects to at most 1 px (1080 px over 60°, times `LodBias`), from the camera's
    distance to the chunk box.
- **Collision.** One `StaticBody3D` per chunk, with a `ConcavePolygonShape3D` whose faces are copied from the LOD 0 mesh,
  so they match the drawn surface bit for bit. It goes through the existing nodes, so no Jitter2 code leaves
  `Src/Physics`.
  - `CollisionMode.All` builds every chunk. `NearBodies` is a documented stub that does the same.
  - Edits rebuild only the touched chunks. Inside a `TerrainEdit` they rebuild at `End()`.
- **Queries and edits.**
  - `TerrainGrid` (a struct) owns the maths: the diagonal rule, exact `HeightAt`, face and smooth normals, and an
    analytic raycast (a DDA over chunks with min/max rejection, then over quads). `Terrain3D` wraps it in world space at
    0 B.
  - `NormalAt` is the face normal, as in the proposal. `SmoothNormalAt` is the interpolated vertex normal.
  - Writes go through `TerrainData`, so code that edits the data directly also rebuilds the terrain. The data raises an
    internal `Edited` event, and `Terrain3D` turns it into chunk rebuilds and `Changed(TerrainChange)`.
  - `TerrainEdit.Begin`/`End` copies per-chunk tiles before the first write, for undo.
- **Splat textures.** `TerrainData.GetSplatTexture(i)` returns a code-made `Texture2D`. It is re-set whole on every
  weight edit, because `Texture2D.SetPixelsRect` and region uploads are left for wave 2 painting.

## Consequences

- The forest can generate, query and walk on its terrain now. Wave 2's `TerrainSplatMaterial3D` binds
  `GetSplatTexture(0/1)` and draws through `Terrain3D.Material`.
- Loading costs about 2.1 s on the first physics step at the forest's 524k collision triangles (Jitter2 makes one
  shape per triangle). `NearBodies` is the fix if load time matters.
- `Png.ReadGray16`/`WriteGray16` are public engine API, so heightmaps from other tools import directly.
- Editor gaps:
  - Inspector edits of a loaded `TerrainData`'s layout knobs throw.
  - Inline terrain data loses its pixels on a code reload.
  - The dock, `TerrainEditAction` and `SaveExternalData` come with G8a.2/G8a.5.
