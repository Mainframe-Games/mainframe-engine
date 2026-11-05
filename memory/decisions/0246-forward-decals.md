# ADR 0246 — Forward decals with per-instance masks and one shared atlas

- **Date:** 2026-11-05
- **Status:** accepted
- **Milestone:** G6 (part: G6.5)
- **Docs:** docs/design/rendering-features.md#decals-g65, memory/plans/2026-11-05-g6-rendering-features.md

## Context

The G6 proposal compared projected-box ("deferred") decals with forward decals and recommended forward ones: they are
lit with the surface (normal and roughness decals work), need no depth read or extra pass (the mobile TBDR plan), and
match Godot's renderers. Lights are a loop over a UBO, not clustered, so the matching granularity is per instance.

## Decision

- **`Decal : Node3D`** with Godot's properties and defaults, projecting along local −Y; `World3D.Decals` lists them.
- **Per view** (`DecalSelection`): the visible decals with a texture, frustum-culled and distance-faded, the 32 nearest
  when more (`MAX_DECALS` in `limits.json`), written to a per-frame-slot UBO (192 bytes each) at set 0 binding 8.
- **Per instance:** a 32-bit mask of the decals whose world bounds overlap the instance's, in `MeshInstanceData`, which
  grows from 80 to 84 bytes (location 10, read only by the lit vertex shaders). Only opaque or cutout
  `StandardMaterial3D` and terrain-splat surfaces take decals.
- **One atlas** for every world (`DecalAtlas`, set 0 binding 9): linear RGBA8 with mips, albedo and emission kept
  sRGB-encoded and decoded in the shader (one binding instead of two; filtering in gamma space is an accepted error),
  rebuilt only when a decal shows a texture it lacks.
- **Shader:** `include/decals.slang`'s `applyDecals` before lighting in `Mesh.vk.frag` and `TerrainSplat.vk.frag`;
  `Mesh.vk.frag` computes the mapped normal and ORM before its shading branches so one pass serves both shading modes.
  The fragment stage reaches 16 images and 14 samplers, within the 16/16 budget.
- **Editor:** a scene icon and a box gizmo with size handles (G7's `IHandleProvider`).

## Consequences

- No new passes; without decals a pixel pays one uniform test and the output is bit-identical.
- `MultiMesh` instances, foliage, water, impostors, spatial shaders and particles get no decals.
- The fragment-stage budget is now full: a further set 0 texture needs a combined binding removed or a separate sampler
  shared.
- Render test `decals`, `DecalAssignBenchmarks.Assign32x10k` (≈ 0.35 ms for 10 000 instances).
