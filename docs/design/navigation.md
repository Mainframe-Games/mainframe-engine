# Navigation (grid A*, point graphs, `NavigationServer2D`, agents)

**Status:** shipped for 2D grids (Gameplay toolkit G3a, G3.1–G3.5; [ADR 0227](../../memory/decisions/0227-grid-navigation-core.md),
[ADR 0228](../../memory/decisions/0228-navigation-server-agents-and-sources.md)). The tile map source (G3.6, after the
tile maps of G2b) and the later phases (avoidance, 2D navmeshes, async queries, 3D; G3b) are still a
[proposal](future/navigation.md).

Godot 4's navigation API on grids: `AStarGrid2D` and the point graphs `AStar2D`/`AStar3D` (plain classes, usable without
a tree), a `NavigationServer2D` with one map per `World2D`, the `NavigationGrid2D` region baked from static bodies, and
`NavigationAgent2D` for path following. Queries allocate nothing and give the same path on every OS and CPU.

![The Demo's Navigation 2D maze in the editor with Visible Navigation on and the grid's inspector](../images/editor-navigation.png)

## Types

| Type | File (`MainframeEngine/Src/Navigation/`) | Role |
|---|---|---|
| `AStarGrid2D`, `GridSearch` | `AStarGrid2D.cs`, `AStarGrid2D.Jps.cs`, `GridSearch.cs` | The grid pathfinder (A*, jump point search) and its search scratch |
| `AStar2D`, `AStar3D`, `PointGraph` | `AStar2D.cs`, `AStar3D.cs`, `PointGraph.cs` | Point-graph A* (waypoints) |
| `NavigationServer2D`, `NavigationMap2D` | `NavigationServer2D.cs`, `NavigationMap2D.cs` | The server, maps, sync, queries, post-processing (`GridLineOfSight`) |
| `NavigationPath2D`, `NavigationPathQuery2D` | `NavigationPath2D.cs`, `NavigationTypes.cs` | A reusable query result; the query |
| `NavigationGrid2D`, `NavigationGridData` | `Nodes2D/` | The grid region node and its baked data resource |
| `NavigationAgent2D` | `Nodes2D/NavigationAgent2D.cs` | Path following |
| `INavigationGridBaker`, `NavigationGridBakers`, `GridPhysicsBaker2D` | `Baking/` | Where a grid's cells come from |
| `NavigationDebugColors`, enums | `NavigationTypes.cs`, `NavigationDebugDraw.cs` | Debug draw colours; `GridDiagonalMode`, `GridHeuristic`, `GridPathStatus`, `NavigationPathPostprocessing`, `NavigationGridSource`, `NavigationWaypoint2D` |

Navigation never calls Box2D.NET or Jitter2: the bake reads collision through `PhysicsDirectSpaceState2D`.

## `AStarGrid2D`

```csharp
var grid = new AStarGrid2D { Region = new Rect2I(0, 0, 64, 32), CellSize = new Vector2(16),
    DiagonalMode = GridDiagonalMode.OnlyIfNoObstacles, DefaultComputeHeuristic = GridHeuristic.Octile,
    DefaultEstimateHeuristic = GridHeuristic.Octile };
grid.Update();                                   // allocates storage + scratch; queries after it allocate nothing
grid.FillSolidRegion(new Rect2I(10, 0, 1, 24));
grid.SetPointWeightScale(new Vector2I(4, 4), 3f);
Span<Vector2I> path = stackalloc Vector2I[256];
var status = grid.GetIdPath(new Vector2I(1, 1), new Vector2I(60, 30), path, out var count, allowPartialPath: true);
```

- **Layout.** `Region` (cells, at most 2048² = `MaxCells`), `CellSize`, `Offset` (cell (0, 0)'s corner).
  `GetPointPosition(id)` is the cell **centre** (`Offset + (id + 0.5) · CellSize`), `GetPointAt(position)` its inverse.
  Only `Region` marks the grid dirty (`IsDirty`); `Update()` reallocates and clears the cells only when the region
  changed. Cells and queries throw on a dirty grid.
- **Cells.** `SetPointSolid`, `IsPointSolid`, `SetPointWeightScale` (≥ 0; weights are stored once one differs from 1),
  `FillSolidRegion`, `FillWeightScaleRegion` (clipped), `ClearCells`, `Clear` (empty region); `SolidCount`,
  `WeightedCount`, and `Version` (bumped by every change). Solidity is a `ulong` bitset.
- **Diagonal modes** (Godot's): `Always` (even between two solid cells), `AtLeastOneWalkable`, `OnlyIfNoObstacles` (no
  corner cutting), `Never` (4-way). **Heuristics:** Euclidean, Manhattan, Octile, Chebyshev, in cells, used as step cost
  (`DefaultComputeHeuristic`, times the entered cell's weight) and estimate (`DefaultEstimateHeuristic`).
  `ComputeCost`/`EstimateCost` are virtual (Godot's `_compute_cost`/`_estimate_cost`); the built-in fast path applies
  when the instance is exactly an `AStarGrid2D`.
- **Results.** `GetIdPath`/`GetPointPath` into spans return a `GridPathStatus`: `Found`; `Partial` (with
  `allowPartialPath`, the path to the visited cell with the lowest estimate, then the lowest cost — Godot's rule; also
  when `MaxSearchNodes` stops the search); `NotFound`; `InvalidPoint` (start solid or outside, target outside);
  `BufferTooSmall` (`count` is the size needed). A solid target is `NotFound` at once without partial paths. Allocating
  overloads return arrays (tools, tests).
- **Jump point search** (`JumpingEnabled`) for every diagonal mode, with the pruning rules of the matching PathFinding.js
  variants; every step still passes the same legality check as A*. Like Godot it **ignores weight scales**. Its paths
  list every cell (Godot returns only jump points). A failed JPS search with partial paths reruns plain A* for the
  partial result. JPS is much faster in mazes and slower on open fields (see Benchmarks), so it is opt-in.
- **Memory.** Per cell: g, h, parent, a stamp and a heap slot (≈ 24 bytes, plus a bit of solidity and 4 bytes of weight
  once weighted). Each query bumps a stamp instead of clearing arrays; stamps clear only when the counter wraps.
- **Determinism.** Only `+`, `*` and `MathF.Sqrt` on floats; ties break by f, then h, then the lower cell index;
  neighbours expand right, down, left, up, then the diagonals. A golden maze locks the A* and JPS paths.

## `AStar2D` and `AStar3D`

Godot's point graphs: `AddPoint(id, position, weightScale)` (also moves an existing point), `RemovePoint`,
`ConnectPoints(a, b, bidirectional = true)`, `DisconnectPoints`, `ArePointsConnected`, `SetPointDisabled`,
`GetClosestPoint(position, includeDisabled)` (the lowest id among equally close points), `GetClosestPositionInSegment`,
`ReserveSpace`, `GetAvailablePointId`, `GetPointConnections`, and the same span/array `GetIdPath`/`GetPointPath` with
`GridPathStatus`. Step cost: the distance times the entered point's weight; estimate: the distance (both virtual).
Storage is a `Dictionary<long, int>` from id to a dense slot plus slot arrays (`PointGraph`, positions as `Vector3`
with z = 0 in 2D; distances in scalar code so every CPU agrees). Adding points allocates, queries do not.

## `NavigationServer2D` and maps

```csharp
var map = NavigationServer2D.For(tree).GetMap(viewport.World2D);
var path = new NavigationPath2D();                     // reuse it: grows, never shrinks
map.QueryPath(new NavigationPathQuery2D(start, target), path);
foreach (var point in path.Points) { /* ... */ }
```

- The engine registers the server right after `PhysicsServer2D` (its fixed step runs after the physics step), and so
  does `HeadlessHost` (dedicated servers run agents). `EngineOptions.DebugNavigation` starts the debug draw on.
- One `NavigationMap2D` per `World2D` (`World2D.NavigationMap`), created by the first region or agent; a map whose
  viewport left the tree and has nothing registered is released.
- **Sync points.** Region registration, bakes, runtime cell edits, layer changes and grid moves are queued; every map
  syncs after each fixed step, or in `Process` on frames without one (edit mode, a paused tree). A sync that changed
  anything bumps `IterationId`. Every query of a tick sees the same map. `ForceSync()` applies changes now.
- **Query.** The region is the first (registration order) whose `NavigationLayers` intersect the query's and that
  contains the start; a start in a solid cell moves to the nearest walkable cell within 2 cells. A target outside the
  region or in a solid cell gives the path to the closest reachable cell (`Partial`) when `AllowPartialPath`. Points
  start at the start and end at the target when it was reached (else the last cell's centre); `Cells` holds the cell
  behind each point. `GetClosestPoint(point, layers)` snaps a point onto walkable ground.
- **Post-processing** (Godot's enum names): `CorridorFunnel` pulls strings — from each kept cell to the farthest cell
  still in line of sight, an exact integer walk that applies the diagonal mode where the line passes a cell corner and
  never crosses a cell heavier than the path cells it replaces; `EdgeCentered` keeps the corners; `None` every cell.
- **Debug draw** (`DebugDrawEnabled`) into the viewport's `DebugLines`: each region's outline, cell lines, solid cells
  (outline and cross), weighted cells (dashed), and each agent's path, next path position and target, in
  `NavigationDebugColors` (static, settable). Culled to the view (the editor camera or the current `Camera2D`); a view
  of more than 32 768 cells of one grid draws its outline only. Allocation-free. An agent with `DebugEnabled` draws its
  path while the server's draw is off. In a 2D game (a root viewport without a 3D camera) debug lines draw over the
  canvas ([Canvas](canvas.md#debug-lines)).

## `NavigationGrid2D` and `NavigationGridData`

A `Node2D` that owns an `AStarGrid2D` and registers it with its viewport's map while in the tree. Cell (x, y) covers
`GlobalPosition + (x, y) · CellSize` to `+ CellSize`; global rotation and scale are ignored (a warning, once).

| Export | Default | |
|---|---|---|
| `Region`, `CellSize` | (0, 0, 32, 32), 32 × 32 px | the layout |
| `DiagonalMode`, `Heuristic`, `JumpingEnabled` | `OnlyIfNoObstacles`, `Octile`, off | a body cannot squeeze between two diagonal wall corners; octile is exact for 8-way grids |
| `NavigationLayers` | 1 | |
| `Source` (Bake group) | `Physics` | `Manual`, `Physics`, `TileMapLayer` (G3.6) |
| `BakeCollisionMask`, `AgentRadius` | layer 1, 0 | physics: the static bodies read; each cell's overlap box grown by the radius |
| `TileMapLayerPath`, `TilePhysicsLayer`, `SolidDataLayer`, `WeightDataLayer` | —, 0, "solid", "" | the tile layer source (read by G3.6's baker) |
| `BakeOnReady` | off | bake once in a deferred call when ready in a running game (procedural levels) |
| `Data` | null | the baked cells (null: every cell walkable) |

- `Grid` is the live grid queries search (outside the tree it is brought up to date when read). `SetCellSolid` and
  `SetCellWeight` edit the live grid (not `Data`: doors, destroyed walls) and apply at the next sync, so agents repath;
  a layout or `Data` change re-applies `Data` and drops those edits. `CellAt`/`CellCenter` convert global positions.
- `Bake()` bakes from `Source` and replaces `Data` (main thread, needs the tree; `Manual` clears). `BakeData(out error)`
  returns the data without assigning it (the editor's undoable bake). `IsBakeOutOfDate()` compares the data's
  `SourceHash` with the scene now (null: unknown). `LastBakeMilliseconds`.
- **`NavigationGridData`** (inline or `.mres`): `Region`, `Solid` (`rle1:` + base64 of LEB128 runs alternating walkable
  and solid cells, row-major, walkable first), `Weights` (`rle1:` + base64 of skip/run/float32 triples), `BakedFrom`,
  `SourceHash`, `SolidCount`, `WeightedCount`. A 256 × 256 grid is a handful of lines. Corrupt data or a region
  mismatch logs a warning and leaves the grid walkable. Plain code: trimming and AOT are fine.
- **Physics bake** (`GridPhysicsBaker2D`): one `IntersectShape` per cell with one reused rectangle (the cell, grown by
  `AgentRadius`, inset 0.1 px so a wall that only touches a cell's edge leaves it walkable), masked by
  `BakeCollisionMask`; only `StaticBody2D` hits count (rigid and character bodies move; areas are never reported). Its
  `SourceHash` hashes the static bodies one overlap query over the region finds (transforms, layers, shapes).

### Grid sources (and what G3.6 plugs in)

`INavigationGridBaker` is the seam: `PrepareLayout(node)` (a source that owns the layout sets `Region`, `CellSize` and
the position), `Bake(node, cells, out failure)`, `Attach`/`Detach(node)` (a live source subscribes to its changes and
calls `NavigationGrid2D.MarkSourceChanged()`, which re-bakes the live grid — not `Data` — at the next sync, at most
once), and `SourceHash(node)`. `NavigationGridBakers.Register(source, baker)` sets the baker of a source; `Physics` is
built in, `TileMapLayer` has none until G3.6 (baking then warns). Grids attach to live sources when they enter the tree.

## `NavigationAgent2D`

A `Node` child of the body it steers; it moves nothing. The body's `OnPhysicsProcess` asks it where to go:

```csharp
public sealed class Chaser : CharacterBody2D
{
    private NavigationAgent2D _agent = null!;
    protected override void OnReady() => _agent = GetNode<NavigationAgent2D>("Agent");
    protected override void OnPhysicsProcess(float delta)
    {
        if (_agent.IsNavigationFinished())
            return;
        Velocity = GlobalPosition.DirectionTo(_agent.GetNextPathPosition()) * 140f;
        MoveAndSlide();
    }
}
```

- Exports: `TargetPosition`, `PathDesiredDistance` (20 px, Godot's), `TargetDesiredDistance` (10 px), `PathMaxDistance`
  (100 px), `TargetRepathDistance` (new, 0 = Godot), `NavigationLayers`, `PathPostprocessing` (`CorridorFunnel`),
  `DebugEnabled`. API: `GetNextPathPosition`, `IsNavigationFinished`, `IsTargetReached`, `IsTargetReachable`,
  `DistanceToTarget`, `GetFinalPosition`, `GetCurrentNavigationPath`, `GetCurrentNavigationPathIndex`, `CurrentPath`,
  `RequestRepath`, `SetVelocity`.
- **Repath** (lazily, inside `GetNextPathPosition`/`IsNavigationFinished`): a new target (moved more than
  `TargetRepathDistance` from the path's), a new map iteration, farther than `PathMaxDistance` from the current path
  segment, or `RequestRepath()`. An idle agent costs nothing. A query that finds nothing (no region yet — maps register
  regions at their first sync — or none at the start) leaves the agent standing and retrying at the next map change.
- **Waypoints and signals** (Godot 4.3): within `PathDesiredDistance` of the current point it advances, raising
  `WaypointReached(NavigationWaypoint2D)` (position, index, cell; a struct, no allocation; the last point once); within
  `TargetDesiredDistance` of the target `TargetReached` then `NavigationFinished`; at the end of a partial path
  `NavigationFinished` alone. Once finished nothing updates until a new target. `PathChanged` after every repath.
  `SetVelocity` raises `VelocityComputed` at once (a pass-through until avoidance, G3.7).
- **Path storage:** each agent owns a `NavigationPath2D` that grows to the longest path and is reused: 0 bytes per tick.
- Agents run where the body's movement runs (the server or host in multiplayer); paths depend only on the map at the
  last sync and the query.

## Editor

- **Visible Navigation** (toolbar view group, icon `route`, View menu): the editor tree's server draws every edited
  scene's grids, paths and targets; on by default, like collision shapes (`EditorApp`: `DebugNavigation`).
- **`NavigationGridInspector`**: icon buttons **Bake** (`wand`) and **Clear** (`trash`), each one undoable change of
  `Data`, and a status line — size, solid and weighted cells, source, the last bake's time, "out of date" when a static
  body in the region changed since the bake. Only the current source's bake rows show.
- **Agent gizmo:** the selected `NavigationAgent2D` draws its `TargetDesiredDistance` and `PathDesiredDistance` circles
  around its body, and a line and circle at its target, in the overlay.
- Running games: a **Navigation** checkbox in the dev overlay's Physics panel. The editor's toggle does not reach played
  games.

## Profiler

Scopes `Navigation sync`, `Navigation query`, `Navigation bake` and `Navigation debug draw`, plus the server's own
`NavigationServer2D.FixedStep` and `NavigationServer2D` frame-server scopes ([Profiler](profiler.md)).

## Tests

- `Tests/MainframeEngine.Tests/Navigation/`: `AStarGrid2DTests` (each diagonal mode's corner rules with and without JPS,
  heuristics, weights, invalid points, partial paths, `BufferTooSmall`, dirty grids, positions, fills, stamp
  wrap-around, `MaxSearchNodes`, overridden costs), `AStarGridOptimalityTests` (200 seeded grids: A* = Dijkstra for every
  admissible heuristic pair; JPS = A* for every mode; a golden maze for A* and JPS), `AStarPointGraphTests`,
  `NavigationMapTests` (sync points, edits, edit mode and pause, queries, post-processing, line of sight, regions by
  layer and position, closest point, debug draw), `NavigationGridBakeTests` (`SerialBox2D`: shapes, agent radius, mask,
  moving bodies, staleness, `BakeOnReady`, sources, a live source), `NavigationGridDataTests` (codec, `.mres`, inline in
  a scene, corrupt data), `NavigationAgent2DTests` (a `CharacterBody2D` round a baked wall with signals in order, repath
  on a new wall, `TargetRepathDistance`, `PathMaxDistance`, unreachable targets, no map) and `NavigationAllocationTests`
  (**0 bytes** over 300 ticks of 64 agents chasing moving targets on a 128 × 128 grid with cell edits every 30 ticks and
  the debug draw on; 0-byte grid queries).
- Editor (`NavigationEditorTests`): the toggle, Bake/Clear with undo and staleness, source rows, the agent gizmo.
- Render test `navigation` (`NavigationRenderTests`): grid, solid cells, mud, paths and targets over the collision
  shapes through a `Camera2D` (golden).
- Demo: `Navigation2DSceneTests` (the saved grid equals a real bake, every chaser reaches the target, clicked walls
  reroute, the panel).
- Benchmarks (`NavigationBenchmarks`, Apple M5): 256² open field A* 8-way ≈ 25 µs, JPS ≈ 0.21 ms, A* 4-way ≈ 31 µs; 256²
  maze A* 8-way ≈ 3.9 ms, JPS ≈ 0.23 ms, 4-way 2.6 ms / 0.4 ms; a tick with 64 agents repathing on a 128² maze ≈ 15 ms;
  all 0 B.

## Changes from the proposal

- `AStarGrid2D.Update()` keeps cells when the region is unchanged, and only `Region` marks the grid dirty (the proposal
  listed every layout property): moving a grid never wipes it. `ClearCells()` is new (`Clear()` empties the region, as
  Godot's).
- JPS partial paths come from a plain A* rerun; `MaxSearchNodes` counts expanded cells.
- `NavigationPath2D` also has `Count` and `MapIteration`; `NavigationMap2D` exposes `Regions`, `Agents` and `Viewport`.
- Agents that find no path (the map's regions not synced yet) wait for the next map iteration instead of finishing;
  finished agents stop updating until a new target (Godot 4.3); `NavigationAgent2D.CurrentPath` is new.
- `NavigationGrid2D` gained `BakeData`, `IsBakeOutOfDate`, `MarkSourceChanged`, `CellAt`, `CellCenter`,
  `LastBakeMilliseconds`; `NavigationGridData` gained `SourceHash` and the counts. `TileMapLayerPath` is typed `Node2D`
  until `TileMapLayer` exists.
- Sources are pluggable (`INavigationGridBaker`, `NavigationGridBakers`) so G3.6 adds a baker, not API.
- 2D games' debug lines draw over the canvas (they drew under it before, unseen).
- The Demo tab has no number key: keys 1–9 and 0 already go to the first ten tabs.

## Related

[Physics](physics.md) (queries), [Canvas](canvas.md), [Editor](editor.md), [Dev overlay](dev-overlay.md),
[Demo](demo.md), [Proposal: later phases and the tile map source](future/navigation.md)
