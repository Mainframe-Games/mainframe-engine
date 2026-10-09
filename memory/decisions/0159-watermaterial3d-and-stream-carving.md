# ADR 0159 — WaterMaterial3D and stream carving

- **Date:** 2026-10-08
- **Status:** accepted (implemented on `forest/h`, forest slice wave 2)
- **Milestone:** Gameplay toolkit G8 (G8c.1, G8c.4 and G8c.7 subsets)
- **Spec:** docs/design/future/water.md; plan docs/superpowers/plans/2026-10-08-forest-vertical-slice.md
- **Docs:** docs/design/water.md, docs/design/terrain.md, docs/design/materials-and-meshes.md

## Context

Wave 1 gave the forest a `River3D` ribbon drawn with a blended blue `StandardMaterial3D`, sitting on whatever ground
happened to be under it, and a terrain whose water layer lowers the bed but draws no water. The slice needs the stream
cut into the valley and a water look that reads as water. `SceneTextures` (depth prepass, pass split, colour copy) is
deferred, so the material is the proposal's fallback look: no refraction, absorption from the per-vertex column.

## Decision

- **Carving lives on `River3D`:** `Carve()`, `Recarve()`, `Uncarve()` and `FitToTerrain(depthBelow)`, with Carve exports
  `TerrainPath` (empty: the code-set `Terrain`, else the first `Terrain3D` in the tree under the river's start),
  `BankWidth` 2 m, `ShoreLift` 0.05 m and `CarveId`. Each call is **one** `TerrainEdit` on the height layer (or part of
  the caller's open edit), exposed as `LastCarveEdit` for undo.
- **The profile** (internal `RiverCarver`, pure C#): for each height vertex the nearest centreline segment (sections
  transformed into terrain-local space) gives `r`, `hw`, `D` and the surface `y`; channel `y + lift − (D + lift)(1 − x²)`,
  bank `lerp(y + lift, original, smoothstep((r − hw) / BankWidth))`. Past either end the channel closes into the bank
  blend (the proposal's nearest-segment rule would leave a dry pit beyond a river that stops). Segments rasterise into
  their own boxes with a best-distance buffer, so the cost is the river's area, not area × sections.
- **The ribbon follows the carve, not the other way round:** the ribbon's surface is the curve's downhill-clamped
  height, and the banks are cut `ShoreLift` above it, so the water sits at bank height minus the freeboard. Code-made
  streams call `FitToTerrain` first (points at `HeightAt − depthBelow`, one curve change).
- **Carve records belong to the terrain data**, keyed by `CarveId`: the original heights of the rectangle a carve wrote,
  in memory, and saved by `TerrainData.SaveLayers` as one `carve_<id>.json` (rectangle + the heights as the terrain's
  16-bit values, base64) — not the proposal's PNG + `.meta`, because `.meta` is the asset database's sidecar extension
  and one file writes atomically. Carving again first restores the record, so carves never stack; uncarve restores bit
  for bit (the heights are already quantised).
- **`WaterMaterial3D`** is a built-in material with its own `ShaderSetId.MeshWater` (`Water/Water.vk.vert/frag`), always
  on the `MeshInstancedExt` layout (it reads `Custom0`: column depth, flow, foam). It reuses the `StandardMaterial3D`
  set 2 layout: the 96-byte block holds its parameters, the albedo slot the foam mask and the normal slot the ripple
  normals, so no new descriptor layout or pipeline layout. It is `AlphaMode.Blend` (transparent list, back to front, no
  depth writes, no shadows cast); the shader outputs straight alpha such that colour × alpha is the light the water adds
  and 1 − alpha the share of the bed that shows through.
- **Shading:** two scales of the normal map in world XZ, each advected in two phases along the flow (+ the world's wind
  × `WindDrift`), whiteout-blended; Schlick Fresnel F0 = 0.02; `iblSpecular(R, roughness) × (0.02·x + y)` from
  `iblBrdf`; GGX glints from every light through `pbrLight` with F0 0.02 and the usual shadow lookups (so the sun's
  shadow darkens glints, scatter and foam); Beer–Lambert over `column / cos(view)` with in-scatter; foam from shallow
  column, the ribbon's UV.u edge, `Custom0.w` and fast flow; a soft fade over `SoftEdgeDistance` of column; `applyFog`
  last. The helpers stay in the fragment shader, not a new include, so no other `.spv` goes stale.
- **Built-in textures** (`WaterTextures.Normal`, `.Foam`): generated in C# on first use, 256², tileable, cached per process:
  32 sine waves with whole-period wave vectors normalised to an RMS slope of 0.2, and two scales of tileable Worley edges.
- **`River3D.Material` null** now means a `WaterMaterial3D`, and `UpdateRibbon` wires `RiverMeshData.Custom0` into
  `MeshSurface.Custom0`.
- **Ponds:** `Terrain3D` keeps one unowned `MeshInstance3D` per wet chunk (`TerrainWaterMesh`: the wet render triangles at
  the stored height − 0.02 m, `Custom0.x` = column depth, UV (0.5, 0) so ribbon-edge foam stays off), drawn with
  `Terrain3D.WaterMaterial` (default `Terrain3D.DefaultWaterMaterial`), rebuilt with the chunk on height and water edits.
  `Terrain3D` is an `IWaterBody3D` registered with `World3D.Water`: surface `HeightAt + WaterDepthAt`, no flow, bounds of
  the wet vertices (empty when dry, so dry terrains cost the queries nothing).

## Consequences

- The forest's stream can be generated, fitted, carved and drawn as water in code; the editor tool, falls, pond joins,
  audio and the Faceted palette sheet remain (docs/design/water.md, "Not yet").
- Without refraction the bed is not tinted per channel (straight alpha has one coverage value: the bed is attenuated by
  the mean transmittance); `SceneTextures` will replace the blend with a composed refraction.
- Pond surfaces are full resolution at every terrain LOD; far, coarse chunks may let the bed poke through the surface.
- `ShaderSetId` gained a value (`MaterialGpu.ShaderSetCount` 5): lanes adding shader sets merge on the enum.
- The carve assumes the river is translated and yawed only, like the queries; the terrain must not be rotated or scaled.
