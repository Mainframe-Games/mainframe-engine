# Forest showcase: vertical slice (G6.1 + G8a–G8d subsets)

**Date:** 2026-10-08. **Branch:** `feature/forest-slice`, worktree `.claude/worktrees/forest-slice`. Commits stay local; one push at the end, then a PR.

**Ask (Brogan):** "implement the forest demo. Let me know when it's done and I will play it and give you notes."

**Scope (Brogan, 2026-10-08):** a vertical slice. Each engine feature is built in the shape its proposal gives it, but only the parts the forest needs. Deferred until after Brogan's notes:
- editor tools (Terrain dock, tree inspector, River3D gizmo);
- impostors;
- TAA, PCSS and contact shadows;
- LUT grading;
- `SceneTextures` refraction.

**Budget:** stop launching new lanes at 50% weekly usage (`get_usage`), finish the lanes in flight, then hand over.

**Specs:**
- `docs/design/future/terrain.md` (G8a)
- `docs/design/future/procedural-trees.md` (G8b)
- `docs/design/future/water.md` (G8c)
- `docs/design/future/forest-showcase.md` (G8d)
- `docs/design/future/rendering-features.md` (G6.1/G6.2)
- ADR 0149

## Shared contracts (every lane codes against these)

**Per-frame data (done, 29a8ae2).** `include/frame.slang` `FrameData` is 432 bytes. It gains:
- `wind`: xyz = normalized direction the wind blows towards, w = strength;
- `windParams`: x = frequency in Hz, y = turbulence, z = noise scale in m;
- `fogColor`: rgb = linear colour, a = enabled;
- `fogParams`: x = density, y = height, z = height density, w = sun scatter.

These come from `WorldEnvironment` (`Wind*` and `Fog*` exports, `WorldEnvironment.FrameEnvironment`) through `FrameContext.Environment`.

**Lighting (lane A1).**
- `lights.slang` gains `float3 shadeLightsPbr(PbrSurface s, float3 Ngeo, float3 worldPos)`, with `PbrSurface { float3 albedo; float metallic; float roughness; float ao; float3 N; }`.
  - Shading is Cook–Torrance GGX plus Lambert, with the same shadow lookups as Blinn-Phong.
  - Ambient comes from sky IBL: diffuse irradiance, plus a prefiltered specular cube with a BRDF LUT.
- `fog.slang` adds `float3 applyFog(float3 color, float3 worldPos)`.
- Every lit 3D shader in the later lanes calls both.

**Vertex data (lane A2).**
- `MeshSurface.Colors` (`Vector4[]`, RGBA 0..1) and `MeshSurface.Custom0` (`Vector4[]`) are optional.
- A surface with either one draws with `VertexLayoutId.MeshInstancedExt`: binding 2, 20 bytes per vertex (RGBA8 unorm colour plus float4 custom0).
- A missing stream reads as colour (1,1,1,1) and custom0 (0,0,0,0).
- For foliage, Custom0 is: x = wind weight (0 at the trunk base, 1 at the tips), y = branch level / 4, z = phase (0..1), w = AO.

**Instancing and visibility (lane A2).**
- `MultiMesh` resource: `Mesh`, `InstanceCount`, `VisibleInstanceCount`, `SetInstanceTransform`, bulk `SetTransforms`. Drawn by `MultiMeshInstance3D : GeometryInstance3D`.
- `GeometryInstance3D.VisibilityRangeBegin`/`End`: Godot's HLOD distances from the camera to the AABB centre; 0 = unbounded.
- `Texture2DArray`: built from images of the same size.

**Materials.**
- `StandardMaterial3D.ShadingMode.Pbr` (A1): `Metallic`, `Roughness`, `AmbientOcclusion`, and an `OrmTexture` slot (R = AO, G = roughness, B = metallic).
- `FoliageMaterial3D` (A2): wind sway from `Custom0` × `frame.wind`, cutout, double-sided with a translucency term, and swaying cutout shadow casters.
- `TerrainSplatMaterial3D` and `WaterMaterial3D` come in wave 2.

**Terrain (lane C).** `Terrain3D` + `TerrainData` in `MainframeEngine/Src/Scene/Nodes3D/Terrain/`, Realistic profile.
- Heights are float CPU data, saved as a 16-bit PNG layer.
- Chunks are internal, unsaved `GeometryInstance3D` children with smooth normals and per-chunk collision.
- Two RGBA8 splat weight maps (8 layers) and a water depth layer.
- Queries: `HeightAt`, `NormalAt`, `SurfaceAt` (the dominant layer), `WaterDepthAt`, `Raycast`.
- `Terrain3D.Material` takes any `Material`, and the chunks draw with it.
- Edits:
  - `TerrainEdit.Begin(TerrainLayers)` / `End()`;
  - code-side `SetHeights(Rect2I, ReadOnlySpan<float>)` and `SetWeights`;
  - every edit raises `Changed(TerrainChange)`.

**Trees (lane B).**
- `TreeGenerator`: a pure C# port of Ez Tree v1.1.0 (`dcf309b`) with a bit-exact RNG.
- `TreeOptions` + `TreeLevel`, with the 15 presets.
- Output: `TreeMeshData` (positions, normals, uvs, custom0, indices) for the bark and the leaves, per LOD.
- `Tree3D` and the material hookup come in wave 2.

**Water (lane E, then wave 2).**
- `Curve3D`: a Godot subset plus per-point `Width`/`Depth`.
- `River3D`: a ribbon mesh with UV.u across, UV.v along the arc length, and the flow speed in Custom0.
- Carving and `WaterMaterial3D` come in wave 2.

## Waves

1. **Wave 1, in parallel.** Each lane has its own worktree, `.claude/worktrees/forest-<lane>`, on branch `forest/<lane>` off `feature/forest-slice`.
   - **A1:** PBR + IBL + fog.
   - **A2:** vertex streams, `MultiMesh`, visibility ranges, `Texture2DArray`, and `FoliageMaterial3D` with vertex wind only (the A1 lighting is swapped in at merge).
   - **B:** the Ez Tree port.
   - **C:** the terrain core.
   - **D:** physical sky, FXAA, auto exposure.
   - **E:** `Curve3D`/`River3D` geometry, the `Examples/Forest` scaffold, and `FirstPersonController` on a flat test ground.
2. **Wave 2.**
   - `TerrainSplatMaterial3D`: height-blended, triplanar, hex-tiling.
   - `Tree3D` with realistic leaves and bark (PBR, foliage wind).
   - Grass scatter: one `MultiMesh` per chunk, using `FoliageMaterial3D`.
   - `River3D` carve and `WaterMaterial3D`, fallback look: flow normals, IBL reflection, depth from the per-vertex depth.
   - Screen-space light shafts.
3. **Wave 3.**
   - The forest content: the generated valley, the stream, scatter, ambientCG and Poly Haven assets, audio.
   - A performance pass.
   - Screenshots, README, docs, golden updates, gates.

## Done when

- `just forest` opens a first-person walk through the valley at a playable frame rate on this Mac.
- Trees sway, the stream flows, and lighting is PBR with fog.
- Every gate is green: build, Release build, test, test-render, format-check, shaders-check.

Status and "Resume here" are in `memory/context/progress-log.md`.
