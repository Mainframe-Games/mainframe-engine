---
summary: >-
  G3.6: NavigationGrid2D reads a TileMapLayer live (collision, "solid" and weight custom data), completing G3a; the
  Demo's tiles_2d has a shadow that chases the hero on it.
---

# 2026-11-02: Navigation from a tile map layer (G3.6)

Branch `feature/nav-tilemap-source`. Plan: [Navigation from a tile map layer](../plans/2026-11-02-nav-tilemap-source.md).

## What changed

- `TileMapLayerBaker` (`Src/Navigation/Baking/`), built in for `NavigationGridSource.TileMapLayer`: the layer owns the
  grid's layout (tile size, global position, used rectangle); a cell is solid from collision on `TilePhysicsLayer` or
  the bool `SolidDataLayer`; `WeightDataLayer` gives weights; the live grid re-reads on `Changed`, once per sync, 0 B
  once warm. Errors leave the grid walkable with a reason.
- `NavigationGrid2D`: `TileMapLayerPath` is typed `TileMapLayer`; the four tile options re-read the layer when changed;
  a live source's grid no longer applies `Data`, and a layout set during a re-read is not applied twice.
- `AStarGrid2D.ClearCells()` keeps the weight array (`HasWeights` now means "has a weight other than 1").
- Demo `tiles_2d`: water moved from "Ground" to "Walls" (the blocking layer); a `NavigationGrid2D` reads "Walls" and a
  `TileShadow2D` chases the hero; panel rows "Shadow chases" and "Show navigation" (es translated); new screenshot.
- Docs: `navigation.md` gained "Tile map source"; the proposal now holds only G3b; G3a ✅ in `milestones.md`; tile maps,
  Demo, README, landing page and `AGENTS.md` updated. State file corrected (it still listed #136/#138 as in flight).

## Decisions

- The tile layer always owns `Region`. The proposal kept a hand-set region, but nothing records that it was set by
  hand; recorded under "Changes from the proposal" in `navigation.md`. No ADR: it follows ADR 0228's seam.
- One layer per grid. The Demo puts everything that blocks on "Walls" rather than adding multi-layer sources.
- The shadow is a plain `Node2D` that walks the path, not a body: bushes and trunks are on the objects layer, which the
  grid does not read.

## Dead ends

- Picking "the middle cell of the path" in the Demo repath test: agent paths are string-pulled (corners only), so the
  test samples the polyline instead.

## Verified

- `TileMapNavigationTests` (8) and the whole navigation suite (93) pass, including the 0-byte re-read of a 128 × 128
  layer. `Tiles2DSceneTests` (6) pass: the shadow reaches the hero without stepping on stone or water, and repaths
  round a wall placed on its path.
- `tiles_2d` screenshots at 240 and 600 frames inspected: water draws from the walls layer, the shadow follows the hero.
- Gates: `just build`, the Release build, `just test` (3151 engine, 1780 editor, 460 video), `just format-check`,
  `just shaders-check` and `check.sh` pass. `just test-render`: 221 of 222 passed; `ShowcaseRecordingTheProfilerAllocatesNothing`
  (the 3D showcase with the profiler recording, no navigation or tiles in it) measured 3 KB over 300 frames once in the
  full run and passed 3 of 3 runs alone: a load-dependent flake, not this change.

## Not verified

- The editor with a tile-sourced grid (Visible Navigation while painting tiles): the inspector already shows the tile
  rows, but no editor test paints tiles under a tile-sourced grid. In the editor the layer also writes the grid's
  `Region`, `CellSize` and position directly, outside undo.

## Next

- `ShowcaseRecordingTheProfilerAllocatesNothing` failed once under the full render run: worth a look if it recurs.
- G3b, avoidance (G3.7) first. `CellsChanged(Rect2I)` only if a game's re-reads show in the profiler.
