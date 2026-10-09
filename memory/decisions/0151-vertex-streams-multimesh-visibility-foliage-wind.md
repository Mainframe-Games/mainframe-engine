# ADR 0151 — Vertex streams, MultiMesh, visibility ranges and foliage wind

- **Date:** 2026-10-08
- **Status:** accepted
- **Milestone:** Gameplay toolkit G8 (forest slice, wave 1, lane A2)
- **Spec:** docs/design/future/terrain.md (prerequisites, MultiMesh), docs/design/future/procedural-trees.md
  (FoliageMaterial3D, the Custom0 layout); docs/superpowers/plans/2026-10-08-forest-vertical-slice.md (shared contracts)
- **Docs:** docs/design/materials-and-meshes.md, docs/design/scene-graph-and-nodes.md

## Context

The forest needs per-vertex wind data and tints (grass, Ez Tree leaves and bark), hundreds of thousands of grass and
scatter instances, HLOD distances, texture arrays for the terrain's splat layers, and leaves that sway with their
shadows. The renderer had a single 32-byte vertex, one `GeometryInstance3D` per drawn instance (about 8 ms for
10 000 nodes), no distance culling, no array textures, and only `StandardMaterial3D`/`OutlineMaterial3D`. Nothing
drove `FrameContext.Time`.

## Decision

- **A second vertex stream, not a bigger vertex.** `MeshSurface.Colors` and `Custom0` (`Vector4[]`, optional,
  serialized like the other arrays) upload one extra buffer per mesh, `MeshVertexExt` (20 B: RGBA8 unorm + float4) at
  binding 2, only when a surface has a stream or a material always reads it (then filled with white and zero).
  `MeshVertex` stays 32 B, primitives and existing casters are untouched.
  - Lit stream surfaces draw with `VertexLayoutId.MeshInstancedExt` (part of `PipelineKey`) and `MeshExt.vk.vert`.
  - `Mesh.vk.frag` multiplies the albedo by the colour behind **specialization constant 1**. The other lit vertex
    shaders write a constant white that is never read. An interpolated 1.0 is not guaranteed to be exactly 1.0, so a
    spec constant keeps every existing golden bit-identical.
  - Vertex colours are authored in sRGB (the engine's rule) and linearized in the vertex shader.
- **`MultiMesh` + `MultiMeshInstance3D`** (Godot's API subset, transforms only).
  - A node is one render item, culled by the world AABB of its drawn instances, one instanced draw per surface (and
    per shadow pass), picked as the node.
  - The instance buffer is **persistent, device-local, in world space** (instance × node transform, the existing
    80 B `MeshInstanceData`), so every existing pipeline draws it unchanged. It is rebuilt — a new buffer, the old one
    through the deletion queue, no write-after-read hazard — only when the multimesh version, the node transform or the
    mesh bounds change. It belongs to the node, not the `MultiMesh` (the proposal's wording), because it bakes the
    node transform; a moving node re-uploads each frame it moves.
  - Steady state: one item per surface, 0 B per frame; 50 000 instances cost the same CPU time as one.
- **Visibility ranges.** `GeometryInstance3D.VisibilityRangeBegin`/`End` (Godot's names, 0 = unbounded), distance from
  the view's camera to the world-AABB centre in [begin, end), applied before caster collection so hidden instances
  cast no shadow. No margins (Godot's hysteresis needs per-view state) and no fade.
- **`Texture2DArray`**: a runtime resource of same-size RGBA8 layers with `TextureImportSettings`;
  `GpuTexture.Create2DArray` over the existing layered `Create` (mips per layer); `Texture2DArrayGpu` for the binder.
  Not saved with scenes, not bindable from `StandardMaterial3D`: the wave-2 terrain material binds it.
- **`FoliageMaterial3D`**, built in like `OutlineMaterial3D`: `ShaderSetId.MeshFoliage`, always the ext layout, the
  `StandardMaterial3D` set-2 layout with its parameters packed into the existing 80-byte block (emission = translucency,
  wind strength, branch bend; `flags.z` = back-face mode), so the cutout caster layouts bind it unchanged and the
  material block (which lane A1 grows for PBR) is not edited.
  - Wind (`include/wind.slang`): Ez Tree's leaf flutter (three sines, simplex phase) in world space, ω = 2π × Hz,
    on leaves only (`Custom0.y` = 1) towards the tip (`1 − v`), plus a branch lean by `Custom0.x²` phased by
    `Custom0.z`. Amplitudes are metres per unit of `WorldEnvironment.WindStrength` (0.12 flutter, 0.35 bend).
  - Lighting goes through one function, `foliageLight`, on `shadeLightsBlinnPhong` until A1's `shadeLightsPbr`
    lands; translucency is a back-lighting term from the first directional light; `Custom0.w` darkens the ambient
    (0 = no data).
  - Shadow casters sway with `Shadow{2D,Point}FoliageInstanced`: the caster layouts' set 0 is the light matrix, so the
    wind and time are **push constants at offset 0** (48 B, unused by the instanced casters; point casters keep the
    light at 64) rather than a bigger light-matrix ring entry.
- **Shader time.** `Engine` sets `FrameContext.Time` before `PrepareFrame` to the summed tree deltas, wrapped hourly
  (Godot's `TIME`): deterministic under `--fixed-fps`, so a render test's frame N is t = N / 60 s.

## Consequences

- Foliage, grass and trees (wave 2) build on `Custom0`/`Colors`, `MultiMesh` and `FoliageMaterial3D`; the terrain
  material binds `Texture2DArray`s through `Texture2DArrayGpu`.
- `MaterialGpu` caches 32 pipeline entries (4 shader sets × extra pass × mirrored × stream); `MeshDrawStats` gains
  `OutOfRange` and `MultiMeshInstances`.
- Every `GeometryInstance3D` shows a Visibility range group in the inspector, so the editor golden was re-recorded.
- Not in this slice: per-instance colour/custom data, mirrored multimesh instances, normals bent by the wind, foliage
  wind in the object-ID pass, visibility margins and fade, distance fade/shrink for grass, saving `Texture2DArray`
  pixels, `StandardMaterial3D.VertexColorUseAsAlbedo` (vertex colours always multiply).
- Render tests: `vertex-colors`, `multimesh` (self-checked, with a pick and a texture-array upload) and `foliage-wind`
  goldens; allocation gates for 50 000 multimesh instances and foliage.
