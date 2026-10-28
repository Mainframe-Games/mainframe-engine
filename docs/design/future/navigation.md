# Proposal: Navigation — tile map source and later phases (G3.6, G3b)

**Milestone:** [Gameplay toolkit](../../milestones.md#gameplay-toolkit-) (G3) · **Status:** 🚧 G3.1–G3.5 shipped, see
[Navigation](../navigation.md); G3.6 waits for tile maps (G2b), G3.7–G3.12 planned ·
**Depends on:** [2D content](2d-content.md) (G2b: `TileMapLayer`), [Navigation](../navigation.md) (the shipped core)
· **Related:** [Networking](../networking.md), [Mobile](mobile.md) (AOT)

The grid pathfinder, point graphs, `NavigationServer2D`, `NavigationGrid2D` (physics bake), `NavigationAgent2D`, the
editor's Bake/Clear and Visible Navigation are current state: [Navigation](../navigation.md) (ADRs 0227 and 0228). This
proposal keeps what is left: the tile map source and the later phases.

## Design

### G3.6 TileMapLayer source (with G2b)

[2D content](2d-content.md) (G2b) adds `TileSet`/`TileMapLayer` and reserves this hook ("Navigation hook (G3)"
there): navigation reads a layer only through its public API (`GetUsedCells`, `GetUsedRect`, `GetCellTileData`,
`MapToLocal`, `TileSet.TileSize`, the `Changed` signal) and G2 adds no navigation fields. With
`Source = TileMapLayer` the grid:

- copies `TileSet.TileSize` as `CellSize`, the layer's position as its own, and `GetUsedRect()` as `Region` (unless
  `Region` was set by hand). Grid cell `(x, y)` is map cell `(x, y)`, so `MapToLocal` and `GetPointPosition` agree;
- marks a cell solid when its tile has collision on the physics layer `TilePhysicsLayer` (new export, default 0), or
  when its tile's bool custom data layer named `SolidDataLayer` (default `"solid"`, G2's own example) is true; empty
  cells are walkable;
- reads weights from an optional float custom data layer `WeightDataLayer` (default empty = all 1);
- re-reads the layer when it raises `Changed`, at most once per sync. With G2's signal as designed (no dirty rect)
  that is O(used cells) with a reused `List<Vector2I>`, allocation-free once sized; a 256 × 256 layer re-reads in
  well under a millisecond. If that proves too slow for games that dig every frame, a `CellsChanged(Rect2I)` signal
  is the one addition navigation would ask of G2 ([Open questions](#open-questions)).

Only `TileShape.Square` layers are accepted (the only shape G2 ships); others log a warning and leave the grid empty.
Per-tile navigation polygons (Godot's `TileSetNavigationLayer`, a name G2 reserves) belong to 2D navmeshes (G3.9).

What is already in place for it ([Navigation → Grid sources](../navigation.md#grid-sources-and-what-g36-plugs-in)):
`NavigationGridSource.TileMapLayer`, the exports `TileMapLayerPath` (typed `Node2D` until `TileMapLayer` exists, then
narrowed), `TilePhysicsLayer`, `SolidDataLayer` and `WeightDataLayer`, and the `INavigationGridBaker` seam. G3.6 adds a
`TileMapLayerBaker` registered with `NavigationGridBakers.Register(NavigationGridSource.TileMapLayer, ...)` in the engine:
`PrepareLayout` copies the layer's tile size, position and used rectangle; `Bake` marks cells from tile collision and
custom data; `Attach`/`Detach` subscribe to the layer's `Changed` and call `NavigationGrid2D.MarkSourceChanged()` (the
live grid re-reads at the next sync, at most once); `SourceHash` may stay null (the layer is the data). No navigation
API changes. From the tile map it needs only: `TileSet.TileSize`, `GetUsedRect()`, `GetUsedCells()`,
`GetCellTileData()` (collision per physics layer, bool and float custom data by name), `MapToLocal` agreeing with
`AStarGrid2D.GetPointPosition`, the `TileShape` (only `Square` is accepted) and `Changed` (or `CellsChanged(Rect2I)`).

### Threading

- **Shipped (G3.1–G3.5): main thread.** Queries run inside the caller's `OnPhysicsProcess`; bakes run on the main thread
  because physics queries are main-thread only. A 128 × 128 query is expected well under 0.1 ms (benchmarked in
  G3.1), so most games never need more.
- **G3.10: async queries (opt-in).** `NavigationMap2D.QueryPathAsync(in query, NavigationPath2D result)` returns a
  `NavigationPathRequest` handle (a struct). Workers run on the .NET thread pool through a cached
  `IThreadPoolWorkItem` (no per-request allocation), each with its own `GridSearch` scratch, against an immutable
  snapshot of the grid taken at the last sync (copy-on-write: a changed grid allocates a new snapshot only on the
  tick it changed). Results are delivered at the **next sync point**, which waits for requests submitted before it.
  Delivery therefore always lands exactly one tick later, so async queries stay deterministic. Agents with
  `AsyncPath = true` keep following the old path until the new one arrives, then raise `PathChanged`. A
  `navigation` section in `project.mfproj` (format bump + migration) holds the worker count and the default.

### Later phases

- **Avoidance (G3.7).** ORCA/RVO for agents and `NavigationObstacle2D` (radius or polygon, static or moving), run in
  the server's `FixedStep` after paths: `AvoidanceEnabled`, `Radius`, `MaxSpeed`, `NeighborDistance`,
  `MaxNeighbors`, `TimeHorizonAgents`, `TimeHorizonObstacles`, `AvoidanceLayers`/`Mask`, `AvoidancePriority` (Godot
  names). Results raise `VelocityComputed` before the next tick's `OnPhysicsProcess`. Neighbour search uses a uniform
  grid owned by the map (no allocation). The algorithm source is an open question.
- **2D navmesh polygons (G3.9).** `NavigationPolygon : Resource` (outlines → convex polygons) and
  `NavigationRegion2D` (Godot names), A* over polygon adjacency plus the funnel algorithm, `NavigationLink2D` for
  jumps and teleports, regions joined by shared edges. Baking from physics shapes and tile outlines with agent-radius
  offsetting. This is the step between grids and 3D navmeshes, and it tests the map/region/agent split on
  non-grid data.
- **3D (G3.11–G3.12).** `NavigationServer3D` with the same map/region/agent split, `NavigationAgent3D`
  (`Vector3`, plus `PathHeightOffset`), and first a `NavigationGrid3D`: an `AStarGrid2D` on the XZ plane at a
  height, baked with `IntersectShape` against 3D static bodies — enough for top-down 3D games, tower defence and RTS.
  Then `NavigationMesh` + `NavigationRegion3D` with baking from collision shapes and meshes; the baking library is
  undecided ([Open questions](#open-questions)).

## Testing

- G3.6: `TileMapLayer` source from tile collision, a `"solid"` bool and a float weight custom data layer, re-read on
  `Changed`, non-square layers rejected; a navigating enemy in G2's `tiles_2d` Demo scene.
- G3.7: head-on pairs, crowds, the allocation gate with avoidance on. G3.10: delivery exactly one sync later, snapshot
  copy-on-write, determinism under thread scheduling.

## Task list

1. **G3.6 TileMapLayer source** (after G2b.2/G2b.3): solid/weight from tile collision and custom data layers,
   re-read on `Changed`; tests; a navigating enemy in G2's `tiles_2d` Demo scene.
2. **G3.7 Avoidance.** ORCA for agents, `NavigationObstacle2D`; tests (head-on pairs, crowds, allocation gate).
3. **G3.8 Grid polish.** Isometric cell shapes if G2 needs them; HPA* only if a game needs it; cell painting in the
   inspector if tile maps do not cover it.
4. **G3.9 2D navmesh polygons.** `NavigationPolygon`, `NavigationRegion2D`, funnel, `NavigationLink2D`, bake.
5. **G3.10 Async queries.** Snapshots, worker scratch, next-sync delivery, `project.mfproj` section; tests.
6. **G3.11 3D grid.** `NavigationServer3D`, `NavigationGrid3D`, `NavigationAgent3D`; Demo 3D variant.
7. **G3.12 3D navmesh.** `NavigationMesh`, `NavigationRegion3D`, baking — after the dependency decision.

## Open questions

1. **3D navmesh baking library.** Candidates, none assumed:
   - *Recast/Detour* (C++, zlib licence, actively maintained by the recastnavigation project; Godot bakes with
     Recast). Needs a native shim built per RID in `natives.yml` like `mfrmlui` ([natives.md](../natives.md)), and
     static linking for iOS ([mobile.md](mobile.md)).
   - *DotRecast* (C# port of Recast/Detour, zlib licence, NuGet; mostly one maintainer). No native build; check AOT
     and trimming, per-query allocation, and release cadence before relying on it.
   - Our own voxel/region/contour pipeline: no dependency, but months of work.
2. **Polygon clipping for 2D navmeshes** (agent-radius offsetting, merging obstacles): Clipper2 is now an approved
   dependency (ADR 0203, `Geometry2D`), so the question is only whether it covers the offsetting needs.
3. **Avoidance algorithm source.** Write ORCA from the paper, or port RVO2 (Apache 2.0, the library Godot uses) with
   attribution. Either way it is plain C#; the question is licence notice and test vectors.
4. **Tile map dirty rects.** Is G2's plain `Changed` signal enough (re-read the used cells), or should G2 add
   `CellsChanged(Rect2I)` for games that dig or build every frame?

Answered by G3a ([ADR 0227](../../../memory/decisions/0227-grid-navigation-core.md),
[ADR 0228](../../../memory/decisions/0228-navigation-server-agents-and-sources.md)): JPS ignores weights like Godot;
grids stay axis-aligned; no cell painting in the editor yet; Godot's pixel defaults; played games use the dev overlay.

## Related

- [Navigation](../navigation.md) (current state), [Physics](../physics.md), [Networking](../networking.md),
  [Dev overlay](../dev-overlay.md), [Editor](../editor.md), [Demo](../demo.md)
- Gameplay toolkit siblings: [2D content](2d-content.md) (G2), [Keyframe animation](keyframe-animation.md) (G1)
