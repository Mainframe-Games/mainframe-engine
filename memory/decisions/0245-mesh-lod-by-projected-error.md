# ADR 0245 — Mesh levels of detail by projected error, authored LODs only

- **Date:** 2026-11-05
- **Status:** accepted
- **Milestone:** G6 (part: G6.4)
- **Docs:** docs/design/rendering-features.md#levels-of-detail-g64, memory/plans/2026-11-05-g6-rendering-features.md

## Context

The G6 proposal designed per-surface LOD levels picked by projected screen-space error (Godot's `lod_bias` and
`mesh_lod_threshold`), with three sources: code, authored `_LODn` nodes in model files, and generated levels. Generated
levels need a mesh simplifier, a dependency the owner has not approved (open question 1: meshoptimizer through a
`Native/` shim, or an in-house quadric simplifier). Trees already have their own LOD (`TreeLod3D`, scatter batches), and
visibility ranges already exist on `GeometryInstance3D`.

## Decision

- **Data:** `MeshSurface.Lods` (`MeshLod[]`, a `Resource` so `ArrayMesh` levels serialize inline), each level an error in
  mesh units plus index-only `Indices` or its own `Geometry`; at most 8 levels per surface.
- **GPU:** levels append after the surfaces in the mesh's own buffers (`MeshGpu.Layout`), so a level is another index
  range and draws, batches and casts exactly like a surface (`MeshGpu.Range(surface, lod)`); no new buffers or
  pipelines.
- **Selection** in `MeshRenderer.Prepare`, only for meshes with levels: allowed error = `threshold / bias / (k · maxScale
  / distance)`, `k = viewHeight · |P.M22| / 2` (one formula for perspective and orthographic cameras), distance to the
  bounds' closest point. Casters use the camera's level, computed before the camera cull.
- **Keys:** the level takes the low 3 bits of the opaque and caster keys; the opaque key's mesh field shrinks to 17 bits.
- **Authored LODs** merge on import (`ModelLods`), keeping each level's offset from LOD0 (Unity's LOD groups);
  `lodErrors` in the model `.meta` sets their errors.
- **Generated LODs are not built.** `generateLods` is reserved in the model `.meta` (default false) so projects do not
  change when a simplifier lands.
- **`TreeLod3D` stays** as it is: trees switch whole meshes and impostors by distance with per-instance fades, which a
  per-surface error does not express; the two coexist.

## Consequences

- Meshes without levels cost nothing new; with levels, selection is ≈ 2.4 ns per instance.
- Imported models need authored `_LODn` nodes (or levels set in code) until the simplifier decision.
- 131 072 resident meshes fit the opaque key; beyond that only batching degrades.
- Render test `lod`, `MeshDrawListBenchmarks.BuildAndSortOpaqueWithLod10k`.
