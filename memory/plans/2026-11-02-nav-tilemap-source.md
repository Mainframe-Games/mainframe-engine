# Plan: Navigation from a tile map layer (G3.6)

**Status:** done, see [log](../logs/2026-11-02-nav-tilemap-source.md)
**Docs:** [Navigation → Tile map source (was the G3.6 proposal)](../../docs/design/navigation.md#tile-map-source),
[Navigation → Grid sources](../../docs/design/navigation.md#grid-sources),
[Tile maps → Navigation hook](../../docs/design/tile-maps.md#navigation-hook),
[ADR 0228](../decisions/0228-navigation-server-agents-and-sources.md)
**Started:** 2026-11-02

## Goal

A `NavigationGrid2D` with `Source = TileMapLayer` reads its cells from a `TileMapLayer`, as the proposal designs: the
layer's tile size, position and used rectangle are the grid's layout; a tile is solid when it has collision on
`TilePhysicsLayer` or its bool custom data `SolidDataLayer` is true; `WeightDataLayer` gives weights; the live grid
re-reads the layer when it raises `Changed`, at most once per sync, without allocating once sized. The Demo's
`tiles_2d` scene has an enemy that navigates the map and repaths around walls the player adds.

## Not in scope

- `CellsChanged(Rect2I)` dirty rects (open question 4 stays open until a game digs every frame).
- Several layers feeding one grid, per-tile navigation polygons (G3.9), non-square shapes.
- Avoidance and the later G3b phases.

## Steps

- [x] `TileMapLayerBaker` in `Src/Navigation/Baking/`: `PrepareLayout` copies tile size, layer global position and used
  rectangle; `Bake` marks solid and weight cells from collision and custom data; `Attach`/`Detach` subscribe to
  `Changed` per grid (the layer resolved lazily, since it may enter the tree after the grid). Registered as the built-in
  baker of `NavigationGridSource.TileMapLayer`; `Unregister` restores the built-in. Verified by: unit tests below.
- [x] `NavigationGrid2D`: `TileMapLayerPath` narrowed to `TileMapLayer`; a live source skips applying `Data` (no false
  "baked for region" warning) and a region change made by `PrepareLayout` during a live re-read does not re-bake twice.
  Verified by: unit tests below; existing navigation tests stay green.
- [x] Tests (`Tests/MainframeEngine.Tests/Navigation/TileMapNavigationTests.cs`): collision on the chosen physics layer
  only, `"solid"` bool data, float weights, layout copied (cell (x, y) centre = `MapToLocal`), `SetCell` re-reads once at
  the next sync and agents repath, wrong custom data type fails with a warning, non-square shape rejected, layer added
  after the grid, detach on exit, 0 B re-read once warmed. Verified by: `just test`.
- [x] Demo `tiles_2d`: water moves from "Ground" to "Walls" (the blocking layer), a `NavigationGrid2D` reads "Walls",
  and a tinted copy of the hero (`TileShadow2D`) chases the hero along it; scene regenerated; Demo tests cover the
  chase and the repath after `ToggleWall`. Verified by: `Demo.Tests`, `just demo-screenshots` for `tiles_2d`.
- [x] Docs: `navigation.md` (tile map source section, current state), proposal trimmed to G3b with "Changes from the
  proposal", `tile-maps.md` hook, `demo.md`, milestone row G3a ✅, state file refreshed, log. Verified by: `check.sh`.
- [x] Gates: `just build`, Release build, `just test`, `just test-render`, `just format-check`, `just shaders-check`.

## Done when

- `NavigationGridBakers.Get(NavigationGridSource.TileMapLayer)` is a `TileMapLayerBaker` and the new tests pass.
- In the Demo's `tiles_2d`, the shadow reaches the hero without crossing stone or water, and a wall added on its path
  makes it repath (a Demo test asserts both).
- The G3a milestone row is ✅ and the proposal holds only G3b.
- Every gate above passes locally.

## Open questions

- Should a hand-set `Region` survive the tile source (the proposal says "unless set by hand", but nothing records that
  it was)? This plan lets the layer own the layout and notes it under "Changes from the proposal"; the owner can ask for
  a flag later.
