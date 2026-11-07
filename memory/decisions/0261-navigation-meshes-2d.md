# ADR 0261 — 2D navigation meshes: our own convex partition on Clipper2, one mesh per map rebuilt at the sync

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G3b (part: G3.9)
- **Docs:** docs/design/navigation.md#navigation-meshes-g39, memory/plans/2026-11-07-navigation-polygons-g3-9.md

## Context

G3.9 asked for Godot's 2D navigation meshes: `NavigationPolygon` (outlines → convex polygons), `NavigationRegion2D`, A*
over polygon adjacency with the funnel, `NavigationLink2D`, regions joined by shared edges, and baking from physics
shapes and tile outlines with agent-radius offsetting. Open question 2 asked whether Clipper2 (ADR 0203, already a
dependency) covers the offsetting. Clipper2 1.5.4 has booleans and offsets but no triangulator, and Godot partitions with
polypartition (another library). No new packages; queries 0 B and deterministic like the grids.

## Decision

- **Clipper2 for the polygon maths, our own partition.** The bake is Godot's recipe on Clipper2's integer grid (one unit
  per `NavigationPolygon.CellSize`): traversable outlines (even-odd) clipped to `BakingRect`, shrunk by the agent radius;
  obstructions (non-zero) grown by it; miter joins with limit 2 (no trigonometry, so the same bake on every CPU). The
  difference's poly tree is split into convex polygons by `PolygonPartition`: holes bridged into the outer ring (Eberly),
  ear clipping in exact 64-bit integer arithmetic, then Hertel–Mehlhorn merging. This answers open question 2: Clipper2
  covers the offsetting and merging; only triangulation is ours (≈ 400 lines, no licence to carry).
- **Bakes parse nodes, not Box2D.** `NavigationPolygonBaker2D` reads `StaticBody2D` collision shapes, tile layers'
  collision polygons (masked by the tile set's physics layers), `Polygon2D`s and obstacles with `AffectNavigationMesh`, in
  the region's local space, from its descendants or a group (Godot's source geometry modes). The default mode needs no
  tree, so a scene built in code (the Demo) saves a baked polygon.
- **One navigation mesh per map, rebuilt at the sync.** `NavMesh2D` puts every enabled region's polygons in global space
  (any transform), welds vertices on the map's cell size, connects exact shared edges inside and across regions, then
  free edges of different regions within `EdgeConnectionMargin` (a sweep over edges sorted by x; the overlap is the
  portal), then links to the closest polygon within `LinkConnectionRadius`; a uniform grid of polygon bounds answers point
  lookups. Region and link transforms are polled at each sync (a notification can arrive after the tick's sync and
  without a change). The rebuild reuses its arrays: 0 B once warm, ≈ 0.2 ms for the benchmark's 2048² hall with 64 pillars.
- **Godot's cost model and A* on polygons, then the funnel.** Entry points are the closest point of each portal to the
  previous one; cost is distance × the region's `TravelCost`, plus `EnterCost` on entering another region and a link's
  own costs; the estimate is the straight-line distance; the heap breaks ties by polygon index. The funnel (Mononen's
  simple stupid funnel) runs per stretch between links; `EdgeCentered` gives portal midpoints, `None` the entry points.
  Off-mesh starts and targets snap to the closest point on the query's layers; an off-mesh or unreachable target gives
  `Partial`, as on grids.
- **Grids and meshes share the map.** A grid region that contains the start answers first, else the mesh. Paths gain
  `NavigationPath2D.Owners` (the grid, region or link behind each point) and agents `LinkReached`.

## Ruled out

- A Clipper2 triangulation: there is none in the pinned 1.5.4; upgrading a pinned dependency for one
  function was not worth it, and its output would still need merging into convex polygons.
- A polypartition or earcut port: licence text to carry for code that is short to write from the algorithms; exact
  integer arithmetic was wanted anyway.
- A constrained Delaunay triangulation: better-shaped triangles, but the merge makes shape matter little, and it is
  several times the code.
- Per-region graphs joined at query time: every query would pay for the joins; Godot also builds one map-wide graph.

## Consequences

- Rotated, scaled and irregular levels navigate without grids; links make jumps and teleports pathable.
- Hertel–Mehlhorn leaves thin fan-shaped polygons; the funnel does not care, but the debug draw shows them.
- Moving a region rebuilds the whole map's mesh at the next sync (fine for doors and moving platforms of a few regions,
  not for hundreds of moving regions).
- G3.10's async queries can give each worker its own `NavMeshSearch`; G3.12's 3D mesh can reuse the map/region/link split.

## Revisit when

- A level's bake or rebuild shows in the profiler (an earcut-style z-order index for ear tests, incremental rebuilds).
- The pinned Clipper2 is upgraded to a version with triangulation.
