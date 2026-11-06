# Plan: The Faceted terrain look (G8a.4, `TerrainMaterial3D`)

**Status:** done, see [log](../logs/2026-11-07-terrain-faceted-look.md)
**Docs:** [Terrain proposal → Faceted profile](../../docs/design/future/terrain.md#faceted-profile-terrainmaterial3d),
[Terrain](../../docs/design/terrain.md), [Materials & meshes](../../docs/design/materials-and-meshes.md),
[ADR 0149](../decisions/0149-terrain-trees-water-engine-features.md)
**Started:** 2026-11-07

## Goal

A Faceted terrain shows crisp flat palette colours per surface cell on exact face normals, with optional two-tone bands
and a user-channel tint, through a built-in `TerrainMaterial3D` that a Faceted `Terrain3D` uses by default. The Terrain
dock's surface swatches show the palette.

## Not in scope

- The editor overlays inside the material (Id-channel legend tint, soft brush disc): the dock's draped ring covers the
  brush; a legend tint waits for named user channels.
- `TerrainUserChannel` legends and named surfaces in `TerrainData` beyond what the material holds.

## Steps

- [x] `TerrainSurface` (Color, StripeColor, HasStripes) and `TerrainMaterial3D : Material` (Surfaces, CellMode Cell/Triangle,
      StripeWidth, StripeAngleDegrees, TintChannel, TintColor, BandStart, Roughness), the `Terrain` back-reference set by
      `Terrain3D`, default palette (8 surfaces). Verified by: unit tests for packing (palette texture texels, unused ids
      magenta) and serialization round trip.
- [x] `TerrainData.GetSurfaceTexture()` (nearest, clamp, linear, follows edits) and `GetUserTexture()`. Verified by: unit
      test that an edit changes the texture's pixels and version.
- [x] `ShaderSetId.MeshTerrainFaceted`: `Terrain/TerrainFaceted.vk.frag.slang` on the mesh vertex shader and the shared
      set 2 layout (albedo slot = surface map, normal slot = palette, emission slot = user map); facet normal from
      derivatives; cell lookup; palette; box-filtered bands; tint; lit through the same path as `StandardMaterial3D`
      (Blinn-Phong or PBR by `ShadingMode`). `just shaders`. Verified by: shaders-check, validation clean.
- [x] `Terrain3D` uses a default `TerrainMaterial3D` for Faceted data when `Material` is null. Verified by: unit test.
- [x] Render test `terrain-faceted` (hill and ramp facets, four surfaces, bands, a box shadow beside a
      `StandardMaterial3D` plane), goldens on moltenvk and lavapipe (Docker). Verified by: PNGs inspected.
- [x] Dock swatches use the palette colours; re-capture `editor-terrain.png`. Verified by: the capture inspected.
- [x] Docs (terrain.md Faceted section, materials-and-meshes.md, shaders.md), milestones, log. Verified by: check.sh.

## Done when

- Gates pass, `terrain-faceted` goldens exist for both drivers and show crisp cells and facets, and a new Faceted
  terrain in the editor shows its palette with no material assigned.

## Open questions

- None that change what gets built.
