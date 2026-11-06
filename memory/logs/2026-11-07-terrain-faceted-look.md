---
summary: >-
  G8a.4: the Faceted terrain look. TerrainMaterial3D (a palette of TerrainSurfaces, exact face normals from derivatives,
  box-filtered two-tone bands, a user-channel tint) on the shared mesh layout, the default for Faceted terrains; the
  surface and user maps as textures that follow edits; textures re-uploaded in place (0 B painting); the dock's swatches.
---

# 2026-11-07: The Faceted terrain look (G8a.4)

Branch `feature/terrain-faceted` (on `feature/terrain-dock`, #186). Plan:
[Faceted look](../plans/2026-11-07-terrain-faceted-look.md). As built: [Terrain → Faceted look](../../docs/design/terrain.md#faceted-look).

## What changed

- Engine: `TerrainSurface`, `TerrainMaterial3D` (`Palette`, `CreateDefault`), `Terrain3D.RenderMaterial` (the default
  instance for Faceted data), Faceted `SurfaceTagAt`; `TerrainData.GetSurfaceTexture()` / `GetUserTexture()`;
  `ShaderSetId.MeshTerrainFaceted` + `Terrain/TerrainFaceted.vk.frag.slang`; `MaterialParams.From(TerrainMaterial3D)`;
  `TextureGpu.Update` writes a same-size, same-sampling code texture into its image (`GpuTexture.Update2D`).
- Editor: the dock's Faceted swatches show the palette's names and colours; Create picks a chunk size that divides the
  map (`ChunkMetersFor`) and reports, instead of throwing, when none does (`InvalidDataException` escaped before).
- Tests: `TerrainMaterialTests` (palette, defaults, link, tags, maps follow edits and undo, resource round trip),
  `TerrainAllocationTests.PaintingFacetedCellsAllocatesNothing`, `NewTerrainsGetAChunkSizeThatDividesTheMap`; render test
  `terrain-faceted` (golden moltenvk + lavapipe, a 0 B gate that repaints a cell every frame).

## Dead ends

- The first `terrain-faceted` used a 48 m map: 24 quads that the Faceted default 16-quad chunk cannot divide.
- `just shaders` rewrote four unrelated `.spv` (Debug, Gizmo, two grid shaders) with different bytes from unchanged
  sources; restored them and added only the new shader's lock line.

## Not done

- Sub-rectangle uploads, the `Triangle` cell mode and the material's editor overlays.

## State changes

- milestones G8a: "Faceted `TerrainMaterial3D` ⬜" → "Faceted `TerrainMaterial3D` ✅".
- state: Next steps 2 drops "Faceted look" from G8a–e rest.
