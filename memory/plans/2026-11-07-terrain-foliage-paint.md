# Plan: Painted foliage density and the Terrain dock's Foliage mode (G8a.8 rest)

**Status:** done, see [log](../logs/2026-11-07-terrain-foliage-paint.md)
**Docs:** [Terrain proposal → Scatter](../../docs/design/future/terrain.md#scatter), [Terrain → Foliage](../../docs/design/terrain.md#foliage)
**Started:** 2026-11-07

## Goal

Foliage types 0–7 can be painted where they grow (density per cell, saved as `foliage-*.png`, undoable), alongside or
instead of their own growth rule, from the Terrain dock's Foliage mode.

## Not in scope

- The foliage build radius, `ChannelCutoff`, object scatter (its own PR).

## Steps

- [x] `TerrainData` foliage images (lazy, load/save, `GetFoliageDensity`, `HasPaintedFoliage`), `TerrainLayers.Foliage`
      in edits and undo, `FoliageType.AutoGrowth`, placement takes max(rule, painted), rebuild on Foliage changes.
      Verified by: `TerrainFoliagePaintTests` (growth, undo, save/load).
- [x] Dock Foliage mode (type list, density, Shift clears, Cmd/Ctrl+click picks, rectangle fill). Verified by: editor test.
- [x] Docs, milestones, log. Verified by: check.sh.

## Done when

- Gates pass and a type with `AutoGrowth` off grows only where the dock painted it, undoably.
