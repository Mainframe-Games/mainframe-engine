# Navigation (grid A*, point graphs, navigation meshes, `NavigationServer2D`, agents)

**Status:** shipped for 2D grids (Gameplay toolkit G3a, G3.1–G3.6; [ADR 0227](../../memory/decisions/0227-grid-navigation-core.md),
[ADR 0228](../../memory/decisions/0228-navigation-server-agents-and-sources.md)), including the tile map source (G3.6),
local avoidance (G3.7, [ADR 0250](../../memory/decisions/0250-navigation-avoidance-orca.md)) and 2D navigation meshes
(G3.9, [ADR 0261](../../memory/decisions/0261-navigation-meshes-2d.md)). G3.8 (grid polish) closed as not needed (the owner
agreed): its conditions did not arise. Async queries and 3D (G3.10–G3.12) are still a [proposal](future/navigation.md).

Godot 4's navigation API on grids: `AStarGrid2D` and the point graphs `AStar2D`/`AStar3D` (plain classes, usable without
a tree), a `NavigationServer2D` with one map per `World2D`, the `NavigationGrid2D` region baked from static bodies or read
live from a `TileMapLayer`, and
`NavigationAgent2D` for path following, with ORCA local avoidance between agents and `NavigationObstacle2D`s. Navigation
meshes (`NavigationPolygon`, `NavigationRegion2D`, `NavigationLink2D`) cover levels that are not grids: any shape, any
rotation, baked from the collision shapes. Queries and avoidance allocate nothing and give the same result on every run.

![The Demo's Navigation 2D maze in the editor with Visible Navigation on and the grid's inspector](../images/editor-navigation.png)

## Types

| Type | File (`MainframeEngine/Src/Navigation/`) | Role |
|---|---|---|
| `AStarGrid2D`, `GridSearch` | `AStarGrid2D.cs`, `AStarGrid2D.Jps.cs`, `GridSearch.cs` | The grid pathfinder (A*, jump point search) and its search scratch |
| `AStar2D`, `AStar3D`, `PointGraph` | `AStar2D.cs`, `AStar3D.cs`, `PointGraph.cs` | Point-graph A* (waypoints) |
| `NavigationServer2D`, `NavigationMap2D` | `NavigationServer2D.cs`, `NavigationMap2D.cs` | The server, maps, sync, queries, post-processing (`GridLineOfSight`) |
| `NavigationPath2D`, `NavigationPathQuery2D` | `NavigationPath2D.cs`, `NavigationTypes.cs` | A reusable query result; the query |
| `NavigationGrid2D`, `NavigationGridData` | `Nodes2D/` | The grid region node and its baked data resource |
| `NavigationAgent2D` | `Nodes2D/NavigationAgent2D.cs` | Path following; avoidance settings and `VelocityComputed` |
| `NavigationObstacle2D` | `Nodes2D/NavigationObstacle2D.cs` | An avoidance obstacle: a radius (moving) and/or a polygon |
| `Orca2D`, `AvoidanceSimulation2D` | `Avoidance/` | The ORCA solver (an RVO2 2.0 port, Apache-2.0, `LICENSE-RVO2.txt`) and each map's avoidance step |
| `INavigationGridBaker`, `NavigationGridBakers`, `GridPhysicsBaker2D`, `TileMapLayerBaker` | `Baking/` | Where a grid's cells come from |
| `NavigationPolygon`, `NavigationParsedGeometryType`, `NavigationSourceGeometryMode` | `Polygons/NavigationPolygon.cs` | G3.9: a navigation mesh resource (vertices, convex polygons, outlines, bake settings) |
| `NavigationRegion2D`, `NavigationLink2D` | `Nodes2D/` | G3.9: a navigation mesh region; a link (jump, teleport) between two points of the mesh |
| `NavMesh2D`, `NavMeshSearch` | `Polygons/` | G3.9 (internal): the map's mesh built at the sync; the polygon A*, funnel and point lookups |
| `NavigationPolygonBuilder`, `PolygonPartition`, `NavigationPolygonBaker2D` | `Polygons/`, `Baking/` | G3.9: outlines → convex polygons (Clipper2, ear clipping, Hertel–Mehlhorn); the scene parse |
| `NavigationDebugColors`, enums | `NavigationTypes.cs`, `NavigationDebugDraw.cs` | Debug draw colours; `GridDiagonalMode`, `GridHeuristic`, `GridPathStatus`, `NavigationPathPostprocessing`, `NavigationGridSource`, `NavigationWaypoint2D`, `NavigationLinkReached2D` |

Navigation never calls Box2D.NET or Jitter2: the grid bake reads collision through `PhysicsDirectSpaceState2D`, the
navigation polygon bake reads the collision shape nodes.

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
| `Source` (Bake group) | `Physics` | `Manual`, `Physics`, `TileMapLayer` |
| `BakeCollisionMask`, `AgentRadius` | layer 1, 0 | physics: the static bodies read; each cell's overlap box grown by the radius |
| `TileMapLayerPath`, `TilePhysicsLayer`, `SolidDataLayer`, `WeightDataLayer` | —, 0, "solid", "" | the tile layer source: the layer, the physics layer whose collision is solid, a bool and a float custom data layer ([Tile map source](#tile-map-source)) |
| `BakeOnReady` | off | bake once in a deferred call when ready in a running game (procedural levels) |
| `Data` | null | the baked cells (null: every cell walkable) |

- `Grid` is the live grid queries search (outside the tree it is brought up to date when read). `SetCellSolid` and
  `SetCellWeight` edit the live grid (not `Data`: doors, destroyed walls) and apply at the next sync, so agents repath;
  a layout or `Data` change re-applies `Data` and drops those edits. `CellAt`/`CellCenter` convert global positions.
- `Bake()` bakes from `Source` and replaces `Data` (main thread, needs the tree; `Manual` clears). `BakeData(out error)`
  returns the data without assigning it (the editor's undoable bake). `IsBakeOutOfDate()` compares the data's
  `SourceHash` with the scene now (null: unknown). `LastBakeMilliseconds`.
- **`NavigationGridData`** (inline or `.mfres`): `Region`, `Solid` (`rle1:` + base64 of LEB128 runs alternating walkable
  and solid cells, row-major, walkable first), `Weights` (`rle1:` + base64 of skip/run/float32 triples), `BakedFrom`,
  `SourceHash`, `SolidCount`, `WeightedCount`. A 256 × 256 grid is a handful of lines. Corrupt data or a region
  mismatch logs a warning and leaves the grid walkable. Plain code: trimming and AOT are fine.
- **Physics bake** (`GridPhysicsBaker2D`): one `IntersectShape` per cell with one reused rectangle (the cell, grown by
  `AgentRadius`, inset 0.1 px so a wall that only touches a cell's edge leaves it walkable), masked by
  `BakeCollisionMask`; only `StaticBody2D` hits count (rigid and character bodies move; areas are never reported). Its
  `SourceHash` hashes the static bodies one overlap query over the region finds (transforms, layers, shapes).

### Grid sources

`INavigationGridBaker` is the seam: `PrepareLayout(node)` (a source that owns the layout sets `Region`, `CellSize` and
the position), `Bake(node, cells, out failure)`, `Attach`/`Detach(node)` (a live source subscribes to its changes and
calls `NavigationGrid2D.MarkSourceChanged()`, which re-bakes the live grid — not `Data` — at the next sync, at most
once), and `SourceHash(node)`. `NavigationGridBakers.Register(source, baker)` replaces the baker of a source; `Physics`
and `TileMapLayer` are built in. Grids attach to live sources when they enter the tree, and a live source owns the live
grid's cells: `Data` is not applied to it.

### Tile map source

`Source = TileMapLayer` (G3.6, `TileMapLayerBaker`) reads the layer at `TileMapLayerPath` through the tile map's public
API only ([Tile maps → Navigation hook](tile-maps.md#navigation-hook)):

- **Layout.** The layer owns it: its `TileSize` is `CellSize`, its global position is the grid's, and `GetUsedRect()` is
  `Region` (an empty layer keeps the region). Grid cell (x, y) is map cell (x, y), so `layer.MapToLocal(cell)` and
  `Grid.GetPointPosition(cell)` agree. The layer's rotation and scale are ignored, like the grid's.
- **Cells.** A used cell is solid when its tile has collision on the tile set's physics layer `TilePhysicsLayer`, or when
  its bool custom data layer `SolidDataLayer` (default `"solid"`; absent is fine) is true. The float custom data layer
  `WeightDataLayer` (default "": none) gives the weight scale of walkable cells; a tile that leaves it unset weighs 1.
  Empty cells are walkable.
- **Live.** The grid re-reads the layer when it raises `Changed` (cells, properties or its tile set), at most once per
  map sync, so agents repath on the next tick: a click that adds a wall in the Demo reroutes the chaser. Changing one of
  the four options re-reads too. The re-read is O(used cells) into a reused list and allocates nothing once warm (a
  128 × 128 layer is tested at 0 B). The layer may enter the tree after the grid: the first re-read finds it.
- **Errors** leave the grid all walkable with a warning, and `BakeData` returns the reason: no layer at the path, a
  non-square `TileShape`, a missing physics layer, `SolidDataLayer` not `Bool`, `WeightDataLayer` missing or not `Float`,
  a used rectangle over `AStarGrid2D.MaxCells`.
- **Bake** (the editor's button) still writes `Data` from the layer, but the live grid keeps reading the layer, and
  `IsBakeOutOfDate()` is unknown (the source has no hash). One layer per grid: a level whose blocking tiles span several
  layers puts them on one (the Demo's "Walls" holds stone and water).

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
  `RequestRepath`, `SetVelocity`, `SetVelocityForced`, `SafeVelocity`, `SubmittedVelocity`; the Avoidance group
  ([Avoidance](#avoidance-g37)).
- **Repath** (lazily, inside `GetNextPathPosition`/`IsNavigationFinished`): a new target (moved more than
  `TargetRepathDistance` from the path's), a new map iteration, farther than `PathMaxDistance` from the current path
  segment, or `RequestRepath()`. An idle agent costs nothing. A query that finds nothing (no region yet — maps register
  regions at their first sync — or none at the start) leaves the agent standing and retrying at the next map change.
- **Waypoints and signals** (Godot 4.3): within `PathDesiredDistance` of the current point it advances, raising
  `WaypointReached(NavigationWaypoint2D)` (position, index, cell; a struct, no allocation; the last point once); within
  `TargetDesiredDistance` of the target `TargetReached` then `NavigationFinished`; at the end of a partial path
  `NavigationFinished` alone. Once finished nothing updates until a new target. `PathChanged` after every repath.
  `SetVelocity` raises `VelocityComputed` at once without avoidance; with it, after the physics step (below).
- **Path storage:** each agent owns a `NavigationPath2D` that grows to the longest path and is reused: 0 bytes per tick.
- Agents run where the body's movement runs (the server or host in multiplayer); paths depend only on the map at the
  last sync and the query.

## Avoidance (G3.7)

Godot's agent avoidance on ORCA (optimal reciprocal collision avoidance; van den Berg et al. 2011): each avoiding agent
gets the velocity closest to the one it wants that keeps its radius clear of every neighbour and obstacle for a time
horizon, assuming its neighbours do their half. The solver is a C# port of RVO2 2.0's 2D one (Godot uses the same
library), kept with its Apache-2.0 notice ([Third-party notices](../../THIRD_PARTY_NOTICES.md#rvo2-library)).

```csharp
public sealed class Walker : CharacterBody2D
{
    private NavigationAgent2D _agent = null!;
    protected override void OnReady()
    {
        _agent = GetNode<NavigationAgent2D>("Agent");          // AvoidanceEnabled = true in the scene
        _agent.VelocityComputed += safe => { Velocity = safe; MoveAndSlide(); };
    }
    protected override void OnPhysicsProcess(float delta)
    {
        if (!_agent.IsNavigationFinished())
            _agent.SetVelocity(GlobalPosition.DirectionTo(_agent.GetNextPathPosition()) * 140f);
    }
}
```

- **Agent exports** (group Avoidance, Godot's names and defaults): `AvoidanceEnabled` (off), `Radius` (10 px),
  `NeighborDistance` (500 px), `MaxNeighbors` (10), `TimeHorizonAgents` (1 s), `TimeHorizonObstacles` (0.5 s; Godot's 0
  divides by zero in ORCA's obstacle lines, so 0.01 s is the floor), `MaxSpeed` (100 px/s), `AvoidanceLayers` and
  `AvoidanceMask` (1), `AvoidancePriority` (1).
- **Timing.** `SetVelocity` (from the body's `OnPhysicsProcess`) submits the preferred velocity. The server's fixed
  step, after the physics step and the map sync, computes every submitted agent's safe velocity from the positions
  then, and only then raises `VelocityComputed` on each, in registration order: a handler that moves its body cannot
  change another agent's input. Moving in the handler (`MoveAndSlide` works anywhere in a tick) renders the same as
  moving in the next `OnPhysicsProcess`. Frames without a fixed step (a paused tree, edit mode) compute nothing.
- **Neighbours.** Other avoiding agents and radius obstacles within `NeighborDistance` (centre to centre) whose layers
  meet the agent's mask, the `MaxNeighbors` nearest. An agent ignores agents of lower `AvoidancePriority`, which then
  take the whole avoidance on themselves; equal priorities share it half and half (ORCA's reciprocity). A neighbour that
  does not avoid back this tick (an obstacle, a mask that misses, lower priority, an agent that submitted nothing) is
  avoided in full. An avoiding agent that submits nothing in a tick stands still for the others and gets no signal.
  `SafeVelocity` holds the last result (the velocity the others predict it with); `SetVelocityForced` replaces it (a
  teleport, a knock-back).
- **`NavigationObstacle2D`** (a `Node2D`): `Radius` (unscaled pixels; agents predict it with `Velocity` and make way
  for it alone: set `Velocity` every tick for a mover), `Vertices` (a polygon in local pixels placed by the global
  transform at every step: static or moved, without prediction; two vertices make a wall segment; either winding pushes
  agents out, and an agent inside a polygon ignores it), `AvoidanceEnabled`, `AvoidanceLayers`. Obstacles change no
  path: block the grid's cells too (`SetCellSolid`) where a path must go round one. ORCA is local: an agent sent dead on
  into a flat face stops at it; paths go round walls, avoidance keeps bodies off each other and off corners.
- **Symmetry.** Two agents exactly head-on only slow down in plain ORCA (RVO2's examples add random noise). Here an
  exactly collinear, approaching relative velocity tilts 1 % to the left of the line between them: the same rule seen
  from both sides, so they pass on the same side, deterministically.
- **Neighbour search** is a uniform hash grid owned by the map's `AvoidanceSimulation2D` (cells of 4 × the largest
  radius, at least 8 px; a counting sort into power-of-two buckets; rings of cells outwards until a ring is beyond the
  shrinking range). Polygon edges are culled by each polygon's bounds, then RVO2's rules (edges seen from outside within
  `TimeHorizonObstacles × MaxSpeed + Radius`, nearest first).
- **Determinism and memory.** Entities are numbered in registration order (agents, then radius obstacles); neighbours
  sort by distance, then number; dot and cross products are scalar code (no SIMD reductions), and coincident agents are
  moved apart by number before RVO2 would divide by zero. Every buffer belongs to the map and only grows: a steady crowd
  allocates nothing.
- **Debug draw** (Visible Navigation): an avoiding agent's radius and a quarter second of its safe velocity
  (`NavigationDebugColors.Avoidance`), obstacles' circles and polygons (`NavigationDebugColors.Obstacle`, faded while
  disabled).

## Navigation meshes (G3.9)

Godot's 2D navigation meshes ([ADR 0261](../../memory/decisions/0261-navigation-meshes-2d.md)): a `NavigationRegion2D`
puts the convex polygons of its `NavigationPolygon` on the map; polygons that share an edge are connected, inside a
region and across regions; `NavigationLink2D`s join points the polygons do not (jumps, ladders, teleporters). Agents
follow funnel paths over them exactly as they follow grid paths.

```csharp
var region = new NavigationRegion2D
{
    NavigationPolygon = new NavigationPolygon { BakingRect = new Rect2(0, 0, 1280, 720), AgentRadius = 12f },
};
level.AddChild(region);              // walls (StaticBody2D), tile layers, carving obstacles go under it
region.Bake();                       // or the inspector's Bake button; saved inline or as a .mfres
```

- **`NavigationPolygon`** (a `Resource`, inline or `.mfres`): `Vertices` and `Polygons` (convex, indices; either winding)
  are the mesh; `Outlines` (an outline inside another is a hole), `BakingRect` (clips the outlines, or is the traversable
  area when there are none), `AgentRadius` (10 px), `CellSize` (1 px: the bake's vertex grid), `ParsedGeometryType`
  (`MeshInstances` = `Polygon2D`s, `StaticColliders`, `Both`), `ParsedCollisionMask`, `SourceGeometryMode` (the region's
  descendants, or the nodes of `SourceGeometryGroupName` with or without children) are the bake's input.
  `MakePolygonsFromOutlines()` partitions the outlines alone; `SetMesh`, `AddPolygon`, `AddOutline`, `Clear`, `Version`.
- **Bake** (`NavigationRegion2D.Bake`/`BakeData`, `NavigationPolygonBaker2D`): the traversable outlines shrunk by the
  agent radius, minus the obstructions grown by it — `StaticBody2D` collision shapes (rectangles, circles and capsules as
  24-sided polygons, convex and concave polygons; disabled shapes skipped), the collision polygons of `TileMapLayer`
  tiles on the masked physics layers (with flips and transposes), `Polygon2D`s, and `NavigationObstacle2D`s with
  `AffectNavigationMesh` — in the region's local space. Clipper2 on an integer grid with miter joins, then holes bridged
  into the outer ring, exact ear clipping and Hertel–Mehlhorn merging: the same polygons on every run and CPU. The default
  source mode bakes without a tree (scenes built in code); the group modes need one. `LastBakeMilliseconds`,
  `BakeFinished`.
- **`NavigationRegion2D`** (a `Node2D`): `NavigationPolygon`, `Enabled`, `NavigationLayers`, `EnterCost` (0: added when a
  path enters it from another region), `TravelCost` (1: multiplies distance inside it), `UseEdgeConnections`; placed by
  its full global transform (rotation and scale too). `NavigationPolygonChanged`.
- **`NavigationLink2D`** (a `Node2D`): `StartPosition`, `EndPosition` (local), `Bidirectional`, `Enabled`,
  `NavigationLayers`, `EnterCost`, `TravelCost`, `GetGlobalStartPosition`/`End…`, `IsConnected()` (both ends found a
  polygon at the last sync). Each end attaches to the closest polygon within the map's `LinkConnectionRadius`.
- **The map's mesh** is rebuilt at a sync after any change: a region's polygon, setting or transform (polled at each sync,
  so a move applies at once), a link, a map setting. Vertices are welded on `NavigationMap2D.CellSize` (1 px), exact shared
  edges connect, free edges of different regions within `EdgeConnectionMargin` (1 px) that run against each other connect
  over their overlap, links attach (`LinkConnectionRadius`, 4 px; Godot's defaults). A uniform grid of polygon bounds
  finds polygons. The rebuild reuses its buffers: 0 B once warm. `PolygonRegions`, `Links`, `PolygonCount`.
- **Queries.** A grid region containing the start still answers first; otherwise the mesh does. The start and target snap
  to the closest point of a polygon on the query's layers. A* over polygons with Godot's cost model — entry points are
  the closest point of each portal to the previous entry, cost the distance times the region's `TravelCost`, plus
  `EnterCost` into another region and a link's `EnterCost` + length × `TravelCost` — and the straight-line estimate; ties
  break by polygon index. `CorridorFunnel` runs the funnel per stretch between links; `EdgeCentered` gives portal
  midpoints; `None` the entry points. A target off the mesh, or unreachable, gives `Partial` (the path to the closest
  point of the polygon closest to it) or `NotFound` without partial paths. `GetClosestPoint` includes the mesh.
- **Paths and agents.** `NavigationPath2D.Owners` holds the node behind each point (the grid, the region, or the link at
  its entry and exit). An agent raises `LinkReached(NavigationLinkReached2D)` (link, index, entry, exit) after the
  `WaypointReached` of a link's entry; it moves nothing, so the body jumps or teleports to the exit (the Demo's chasers
  set `GlobalPosition`).
- **Debug draw** (Visible Navigation): inner polygon edges faint (`CellLines`), borders (`Region`), edge connections
  (`NavigationDebugColors.EdgeConnection`, with a cross at the middle), links (`NavigationDebugColors.Link`: a line, end
  circles, an arrow per direction; faded while unconnected). Culled to the view like grids.

## Editor

- **Visible Navigation** (toolbar view group, icon `route`, View menu): the editor tree's server draws every edited
  scene's grids, paths and targets; on by default, like collision shapes (`EditorApp`: `DebugNavigation`).
- **`NavigationGridInspector`**: icon buttons **Bake** (`wand`) and **Clear** (`trash`), each one undoable change of
  `Data`, and a status line — size, solid and weighted cells, source, the last bake's time, "out of date" when a static
  body in the region changed since the bake. Only the current source's bake rows show.
- **Agent gizmo:** the selected `NavigationAgent2D` draws its `TargetDesiredDistance` and `PathDesiredDistance` circles
  around its body, its avoidance `Radius` when avoiding, and a line and circle at its target, in the overlay.
- **Obstacle gizmo:** the selected `NavigationObstacle2D` draws its radius circle and polygon (a dot on each vertex) in
  the selection colour; Visible Navigation draws every obstacle. `Vertices` are edited in the inspector (no handles yet).
- **`NavigationRegionInspector`** (G3.9): **Bake** (`wand`) and **Clear** (`trash`), each one undoable change of the
  polygon's `Vertices` and `Polygons`, and a status line — polygons, vertices, outlines or baking rect, the last bake's
  time. The selected region draws its outlines (a dot per vertex) and a dashed baking rectangle; outlines are edited in
  the inspector (no handles yet). The selected `NavigationLink2D` draws its line, arrow heads and a circle of the link
  connection radius at each end.
- Running games: a **Navigation** checkbox in the dev overlay's Physics panel. The editor's toggle does not reach played
  games.

## Profiler

Scopes `Navigation sync`, `Navigation query`, `Navigation bake`, `Navigation avoidance` and `Navigation debug draw`, plus the server's own
`NavigationServer2D.FixedStep` and `NavigationServer2D` frame-server scopes ([Profiler](profiler.md)).

## Tests

- `Tests/MainframeEngine.Tests/Navigation/`: `AStarGrid2DTests` (each diagonal mode's corner rules with and without JPS,
  heuristics, weights, invalid points, partial paths, `BufferTooSmall`, dirty grids, positions, fills, stamp
  wrap-around, `MaxSearchNodes`, overridden costs), `AStarGridOptimalityTests` (200 seeded grids: A* = Dijkstra for every
  admissible heuristic pair; JPS = A* for every mode; a golden maze for A* and JPS), `AStarPointGraphTests`,
  `NavigationMapTests` (sync points, edits, edit mode and pause, queries, post-processing, line of sight, regions by
  layer and position, closest point, debug draw), `NavigationGridBakeTests` (`SerialBox2D`: shapes, agent radius, mask,
  moving bodies, staleness, `BakeOnReady`, sources, a live source), `NavigationGridDataTests` (codec, `.mfres`, inline in
  a scene, corrupt data), `NavigationAgent2DTests` (a `CharacterBody2D` round a baked wall with signals in order, repath
  on a new wall, `TargetRepathDistance`, `PathMaxDistance`, unreachable targets, no map) and `NavigationAllocationTests`
  (**0 bytes** over 300 ticks of 64 agents chasing moving targets on a 128 × 128 grid with cell edits every 30 ticks and
  the debug draw on; 0-byte grid queries), `TileMapNavigationTests` (the tile map source: layout and cell mapping,
  collision per physics layer, `"solid"` and weight data, re-reads on `Changed` with an agent repathing round a new wall,
  a layer that enters after the grid, detaching, every error, `Bake` into `Data`, a 0-byte re-read of a 128 × 128
  layer), `NavigationAvoidanceTests` (G3.7: a head-on pair passes without touching, a crowd of 16 through a 64 px gap
  arrives without overlap or wall contact, a static polygon is gone round, moving radius and polygon obstacles are
  avoided, two runs are bit-identical, masks and priorities, standing agents, the signal's timing and `MaxSpeed`, and
  without avoidance the pass-through); the allocation gate's second case: **0 bytes** over 300 ticks of 64 avoiding
  agents on paths with four orbiting radius obstacles, a sliding polygon and the debug draw on.
- G3.9: `NavigationPolygonBuilderTests` (a square, an L, holes, islands, carved and radius-grown obstructions, merged and
  splitting obstructions, many bridged holes, the baking rectangle, degenerate input, either winding, a star with holes
  against Clipper's own area, deterministic output; every polygon convex), `NavigationMeshQueryTests` (sync, the funnel
  round a wall both ways, `EdgeCentered`/`None` on the mesh, shared edges and edge margins, links one way and both,
  the funnel either side of a link, travel and enter costs, layers, snapping and partial paths, rotated and scaled
  regions, rebuilds, grids first, bad polygons, determinism and 0-byte queries), `NavigationPolygonBakeTests`
  (`SerialBox2D`: shapes and the agent radius, every shape kind, masks and geometry types, group modes, tiles and carving
  obstacles, transformed regions, errors), `NavigationPolygonResourceTests` (`.mfres` and inline), `NavigationMeshAgentTests`
  (an agent round a wall and across a link with `LinkReached`, repaths when a region turns off, the debug draw, and the
  allocation gate's third case: **0 bytes** over 300 ticks of 64 agents on a navmesh with a door region toggling every 30
  ticks, a link and the debug draw on).
- Editor (`NavigationEditorTests`): the toggle, Bake/Clear with undo and staleness, source rows, the agent gizmo, the
  obstacle gizmo and the avoidance radius; the region inspector's Bake/Clear with undo, the region and link gizmos.
- Render test `navigation` (`NavigationRenderTests`): grid, solid cells, mud, paths and targets over the collision
  shapes through a `Camera2D` (golden).
- Demo: `NavigationMesh2DSceneTests` (the saved polygons equal a bake, every chaser ferries across and arrives without
  wading the river, the pond carved or not, the panel); `Navigation2DSceneTests` (the saved grid equals a real bake, every chaser reaches the target, avoiding chasers
  keep apart and off the boulder, clicked walls reroute, the panel); `Tiles2DSceneTests` (the tile-sourced grid matches the walls layer, the shadow reaches the hero
  without stepping on stone or water, a wall on its path makes it repath).
- Benchmarks (`NavigationBenchmarks`, Apple M5): 256² open field A* 8-way ≈ 25 µs, JPS ≈ 0.21 ms, A* 4-way ≈ 31 µs; 256²
  maze A* 8-way ≈ 3.9 ms, JPS ≈ 0.23 ms, 4-way 2.6 ms / 0.4 ms; a tick with 64 agents repathing on a 128² maze ≈ 15 ms;
  all 0 B. `AvoidanceBenchmarks` (RVO2's circle scenario through four pillars, one fixed step): 64 agents ≈ 43 µs, 256
  agents ≈ 0.23 ms, 0 B (measured with other work running: not in `baseline.json` yet). `NavmeshBenchmarks` (G3.9, a
  2048² hall with 64 slanted pillars): a funnel query corner to corner ≈ 11 µs and 0 B, the map's rebuild ≈ 0.2 ms and
  0 B, `MakePolygonsFromOutlines` ≈ 0.4 ms (allocates its result); also measured on a busy machine.

## Changes from the proposal

- `AStarGrid2D.Update()` keeps cells when the region is unchanged, and only `Region` marks the grid dirty (the proposal
  listed every layout property): moving a grid never wipes it. `ClearCells()` is new (`Clear()` empties the region, as
  Godot's).
- JPS partial paths come from a plain A* rerun; `MaxSearchNodes` counts expanded cells.
- `NavigationPath2D` also has `Count` and `MapIteration`; `NavigationMap2D` exposes `Regions`, `Agents` and `Viewport`.
- Agents that find no path (the map's regions not synced yet) wait for the next map iteration instead of finishing;
  finished agents stop updating until a new target (Godot 4.3); `NavigationAgent2D.CurrentPath` is new.
- `NavigationGrid2D` gained `BakeData`, `IsBakeOutOfDate`, `MarkSourceChanged`, `CellAt`, `CellCenter`,
  `LastBakeMilliseconds`; `NavigationGridData` gained `SourceHash` and the counts.
- Sources are pluggable (`INavigationGridBaker`, `NavigationGridBakers`), so G3.6 added a baker and no API.
- **G3.6:** the tile layer owns `Region` always (the proposal kept a hand-set region, but nothing records that it was
  set by hand); a live source's grid no longer applies `Data`; `AStarGrid2D.ClearCells()` keeps the weight array so a
  re-read allocates nothing; `NavigationGridBakers.Unregister` (tests) puts the built-in back. The plain `Changed`
  signal is enough so far: no `CellsChanged(Rect2I)` was added to tile maps.
- 2D games' debug lines draw over the canvas (they drew under it before, unseen).
- **G3.7 avoidance:** the proposal left the algorithm source open: it is a port of RVO2 2.0 (ADR 0250).
  `TimeHorizonObstacles` defaults to 0.5 s, not Godot's 0. `VelocityComputed` is raised in the server's fixed step
  after the physics step (the proposal: "before the next tick's `OnPhysicsProcess`", which this satisfies). Added
  beyond the proposal: per-neighbour responsibility (full avoidance of neighbours that do not avoid back), deterministic
  head-on symmetry breaking, `SafeVelocity`, `SubmittedVelocity`, `SetVelocityForced`, `NavigationMap2D.Obstacles`.
  Polygon obstacles always push agents out (Godot's winding picks in or out) and are not predicted when moved; radius
  obstacles are. The neighbour grid is the hash grid the proposal named; polygon edges use bounds culling, not RVO2's
  obstacle k-d tree (few edges per level). The editor has no vertex handles for obstacles yet.
- **G3.9 navigation meshes:** the partition is ours (Clipper2 1.5.4 has no triangulator; Godot uses polypartition), on
  Clipper2's integer grid with miter joins (ADR 0261, answering open question 2: Clipper2 covers the offsetting).
  Grids and meshes share one map (a grid containing the start answers first). Added beyond the proposal:
  `NavigationPath2D.Owners`, `NavigationAgent2D.LinkReached` with `NavigationLinkReached2D`, `NavigationLink2D.IsConnected`,
  `NavigationObstacle2D.AffectNavigationMesh` (Godot's), `BakingRect` as the traversable area when there are no outlines,
  bakes of scenes outside the tree (descendant mode), the map's `CellSize`, `EdgeConnectionMargin`,
  `LinkConnectionRadius`, `UseEdgeConnections` and `PolygonCount`. Region and link moves are polled at each sync. Tile
  layers are parsed whether or not their `CollisionEnabled` is on. Not built: an outline editor with viewport handles,
  baking on a thread, `BorderSize` and `BakingRectOffset`, a carve-without-radius mode for obstacles.
- **G3.8 grid polish** closed without code: its three items were conditional — isometric cells if tile maps need them
  (tile maps are square only), HPA* if a game needs it (none does), cell painting if tile maps do not cover it (G3.6's
  live tile source does).
- The Demo scene is in the nav bar's 2D category; number keys open categories, not scenes ([Demo](demo.md#navigation)).

## Related

[Physics](physics.md) (queries), [Canvas](canvas.md), [Editor](editor.md), [Dev overlay](dev-overlay.md),
[Demo](demo.md), [Tile maps](tile-maps.md), [Proposal: later phases](future/navigation.md),
[ADR 0250](../../memory/decisions/0250-navigation-avoidance-orca.md), [ADR 0261](../../memory/decisions/0261-navigation-meshes-2d.md)
