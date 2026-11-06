---
summary: >-
  G3.9 (ADR 0261): 2D navigation meshes — NavigationPolygon baked through Clipper2 and an own convex partition,
  NavigationRegion2D, NavigationLink2D, one map mesh rebuilt at the sync, polygon A* + funnel, LinkReached; editor
  Bake/Clear, Demo scene. G3.8 closed with nothing to build.
---

# 2026-11-07: 2D navigation meshes (G3.9)

Branch `feature/navigation-polygons`. Plan: [G3.9 2D navigation meshes](../plans/2026-11-07-navigation-polygons-g3-9.md).
Proposal: [Navigation, later phases](../../docs/design/future/navigation.md); as built:
[Navigation → Navigation meshes](../../docs/design/navigation.md#navigation-meshes-g39).

## What changed

- **Engine:** `Polygons/` — `NavigationPolygon` (resource), `NavigationPolygonBuilder` (Clipper2: outlines even-odd,
  baking rect, agent-radius offsets with miter joins, difference into a poly tree), `PolygonPartition` (hole bridging,
  exact ear clipping, Hertel–Mehlhorn), `NavMesh2D` (the map's mesh: welding, shared edges, edge margin connections,
  links, lookup grid), `NavMeshSearch` (polygon A* with Godot's costs, funnel, midpoints, entry points, closest polygon);
  `NavigationRegion2D`, `NavigationLink2D`, `Baking/NavigationPolygonBaker2D` (static body shapes, tile collision,
  `Polygon2D`, obstacles with the new `AffectNavigationMesh`); `NavigationMap2D` gains `PolygonRegions`, `Links`,
  `PolygonCount`, `CellSize`, `EdgeConnectionMargin`, `LinkConnectionRadius`, `UseEdgeConnections`; queries fall back to
  the mesh where no grid contains the start; `NavigationPath2D.Owners`; `NavigationAgent2D.LinkReached`
  (`NavigationLinkReached2D`); debug draw of polygons, borders, edge connections and links.
- **Editor:** `NavigationRegionInspector` (Bake/Clear, undoable, status line); gizmos for the selected region (outlines,
  dashed baking rect) and link (line, arrows, reach circles).
- **Demo:** `navmesh_2d` (Navigation Mesh 2D): slanted walls, pillars, a carved pond, a river crossed by two ferry links
  to a second region; chasers hop on `LinkReached`; panel; screenshot; es translations; `NavChaser` hops links.
- **Tests and benchmarks:** `NavigationPolygonBuilderTests` (13), `NavigationMeshQueryTests` (16), `NavigationPolygonBakeTests`
  (7), `NavigationPolygonResourceTests` (2), `NavigationMeshAgentTests` (4, with the allocation gate's navmesh case),
  two editor tests, `NavigationMesh2DSceneTests` (5); `NavmeshBenchmarks` (query ≈ 11 µs, rebuild ≈ 0.2 ms, both 0 B;
  machine busy, not in the baseline).
- **Docs:** navigation (section, types, editor, tests, benchmarks, changes), proposal trimmed (G3.8 closed, G3.9 moved,
  open question 2 answered), milestones G3b row, demo, AGENTS.md, README, landing page.

## Decisions

- [ADR 0261](../decisions/0261-navigation-meshes-2d.md): own convex partition on Clipper2's integer grid (answers open
  question 2), node-parsing bakes, one map-wide mesh rebuilt at the sync, Godot's cost model and the funnel.
- G3.8 closed as not needed (the owner agreed on 2026-11-07): its items were conditional and none of the conditions holds (square-only tile maps, no game
  asking for HPA*, cell painting covered by the live tile source).

## Dead ends

- Region transforms from notifications: the tree's transform sync runs after the navigation server's sync, and fires once
  after entering without a change, so moves landed a tick late and every new region rebuilt twice. The map now polls
  each region's and link's global transform at the sync.
- `Array.Sort` with an `IComparer` allocates a comparison delegate per call (64 B per rebuild in the gate):
  `FreeEdge : IComparable` and `Span.Sort()`.
- In the Demo, avoidance pushed chasers into the river at the crowded ferry landings and out of the open floor: the river
  became a static body with an avoidance obstacle, and outer walls bound the level (neither is parsed by a region).

## Verified

- See the PR body: build, Release build, unit tests, render tests, format, shaders, check.sh; Demo tests.

## Not verified

- The Demo scene's feel on screen beyond the screenshot (crowding at the ferries).

## Next

- G3.10 async queries, G3.11 3D grid + `NavigationAgent3D`; G3.12 on DotRecast (the owner's choice, 2026-11-07).
