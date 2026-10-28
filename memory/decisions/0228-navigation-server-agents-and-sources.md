# ADR 0228 — NavigationServer2D sync points, agents, the grid source seam and the editor

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/navigation.md (from the proposal docs/design/future/navigation.md, G3.3–G3.5)

## Context

The navigation server, the grid region node, the path-following agent and the editor tools (G3.3–G3.5) have to agree on
when map changes become visible, how agents react, how a tile map source (G3.6, after the tile maps of G2b) plugs in
without API changes, and what the editor and running games show. Open questions 6 (painting cells in the editor), 7
(pixel defaults) and 9 (Visible Navigation in played games) are in scope.

## Decision

- **Sync points.** `NavigationServer2D` (registered after `PhysicsServer2D` in `Engine` and `HeadlessHost`) keeps one
  `NavigationMap2D` per `World2D` and syncs every map after each fixed step, or in `Process` on frames without one (edit
  mode, a paused tree). Region registration, bakes (`Data`), runtime `SetCellSolid`/`SetCellWeight`, layer changes and
  moves (a transform notification) are queued and applied there; a sync that changed anything bumps `IterationId`.
  Outside a map (not in the tree) a grid applies changes at once, so tools and tests need no tree.
- **Agents (Godot 4.3's rules).** Paths are computed lazily in `GetNextPathPosition`/`IsNavigationFinished` (inside the
  body's `OnPhysicsProcess`) on a new target, a new map iteration, being farther than `PathMaxDistance` from the path
  segment, or `RequestRepath()`. Waypoints advance within `PathDesiredDistance` (the last one once); within
  `TargetDesiredDistance` of the target `TargetReached` then `NavigationFinished` fire; at the end of a partial path
  `NavigationFinished` alone. Finishing stops updates until a new target. New: `TargetRepathDistance` (0 = Godot) keeps the
  path while the target moves less than that. A query that finds no path (no region yet — the map registers regions at
  its first sync — or none at the start) makes the agent stand still and retry on the next map change, without a signal.
  `SetVelocity` raises `VelocityComputed` at once (a pass-through until avoidance, G3.7). **Pixel defaults stay Godot's
  20 / 10 px** (open question 7): deriving them from the cell size would surprise people porting Godot scenes.
- **Grid paths are post-processed** with Godot's enum names: `CorridorFunnel` pulls strings with an exact integer line
  of sight that applies the diagonal mode at cell corners and never crosses cells heavier than the path it replaces;
  `EdgeCentered` keeps the corners; `None` every cell. Points start at the query's start and end at the target when it
  was reached.
- **The source seam for G3.6.** `NavigationGridSource { Manual, Physics, TileMapLayer }` and `INavigationGridBaker`
  (`PrepareLayout`, `Bake`, `Attach`/`Detach`, `SourceHash`), registered per source in `NavigationGridBakers`. Physics is
  built in (`GridPhysicsBaker2D`: one `IntersectShape` per cell, static bodies only, the box inset 0.1 px so walls that
  only touch a cell's edge leave it walkable). A live source attaches when the grid enters the tree and calls
  `NavigationGrid2D.MarkSourceChanged()`, which re-bakes the live grid (not `Data`) at the next sync, at most once. The
  tile layer's exports (`TileMapLayerPath`, `TilePhysicsLayer`, `SolidDataLayer`, `WeightDataLayer`) exist now, so G3.6
  adds a baker and no API.
- **Baked data** (`NavigationGridData`): RLE + base64 strings (a 256 × 256 grid is a few lines), the counts, and a hash of
  the static bodies the bake read (`SourceHash`, one overlap query over the region), which tells the inspector when the
  bake is out of date. Corrupt or mismatched data logs a warning and leaves the grid walkable.
- **Editor.** A Visible Navigation toggle (icon `route`, on by default like collision shapes; a viewport setting
  applied to the editor tree's server each frame), a `NavigationGridInspector` with icon buttons Bake (`wand`) and Clear
  (`trash`) as undoable `Data` edits, a status line and only the current source's bake rows, and arrival circles for the
  selected agent. **No cell painting** (open question 6): grids are baked or edited from code until tile maps (G3.6)
  give a painting workflow. **Played games use the dev overlay** (open question 9): a Navigation checkbox in the
  Physics panel and `EngineOptions.DebugNavigation`; the editor toggle does not go over the editor link.
- **2D games draw debug lines over their canvas.** A root viewport without a 3D camera now hands its `DebugLines` and
  `OverlayLines` to the canvas server (like `Disable3D` sub-viewports), so navigation and collision shapes show over the
  sprites instead of under them.

## Consequences

- Queries during a tick all see the map of the last sync: deterministic whatever order nodes ask in, and async queries
  (G3.10) can deliver at the next sync without changing semantics.
- G3.6 needs from the tile map only its public API: `TileSet.TileSize`, `GetUsedRect()`, `GetUsedCells()`,
  `GetCellTileData()` (collision polygons per physics layer, bool/float custom data), `MapToLocal` agreeing with
  `GetPointPosition`, a `TileShape` to reject non-square layers, and a `Changed` signal (or `CellsChanged(Rect2I)`).
- Agents cost nothing when idle; 64 agents repathing on a 128 × 128 maze stay at 0 bytes per tick.
