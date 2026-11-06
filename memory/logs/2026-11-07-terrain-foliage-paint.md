---
summary: >-
  G8a.8 rest: painted foliage density (foliage-0/1.png, types 0–7, lazy, undoable) combined with each type's own rule
  (FoliageType.AutoGrowth), and the Terrain dock's Foliage mode.
---

# 2026-11-07: Painted foliage density and the dock's Foliage mode

Branch `feature/terrain-foliage-objects` (stacked on `feature/river-tool`). Plan:
[Foliage paint](../plans/2026-11-07-terrain-foliage-paint.md). As built: [Terrain → Foliage](../../docs/design/terrain.md#foliage).

## What changed

- Engine: `TerrainData` foliage images (`FoliageFile`, `GetFoliageDensity`, `HasPaintedFoliage`, `PaintedFoliageTypes`;
  `LayerFiles` grew to `ushort`), `TerrainLayers.Foliage` in `GetCells`/`SetCells`/undo (two images), `FoliageType.AutoGrowth`,
  `FoliagePlacement` growth = max(rule, painted), `TerrainFoliage3D` rebuilds on Foliage edits.
- Editor: `TerrainDockMode.Foliage`, `TerrainBrushOps.PaintFoliage`, the type list and Density field.
- Tests: `TerrainFoliagePaintTests` (2), `TerrainDockTests.TheFoliageBrushPaintsDensityForTheChosenTypeInOneUndoStep`.

## Not done

- Object scatter and the Objects mode (next PR); the foliage build radius; `ChannelCutoff`.

## State changes

- milestones G8a: "Terrain dock: create, sculpt, paint, water ✅ … Foliage and Objects modes, object scatter ⬜" → "Terrain dock: create, sculpt, paint, water, foliage ✅ (ADR 0270); Faceted `TerrainMaterial3D` ✅; painted foliage density ✅; object scatter and the Objects mode ⬜".
- state: Next steps 2 replaces "the Terrain dock's Foliage and Objects modes" with "object scatter and the Terrain dock's Objects mode".
