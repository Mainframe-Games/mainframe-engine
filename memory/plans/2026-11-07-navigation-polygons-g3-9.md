# Plan: G3.9 2D navigation meshes (polygons, regions, funnel, links, bake)

**Status:** in progress
**Docs:** [Navigation proposal, later phases](../../docs/design/future/navigation.md) (§ Later phases: 2D navmesh polygons,
task 4, open question 2), [Navigation](../../docs/design/navigation.md) (the shipped grids, server, agents, avoidance),
[ADR 0227](../decisions/0227-grid-navigation-core.md), [ADR 0228](../decisions/0228-navigation-server-agents-and-sources.md),
[ADR 0203](../decisions/0203-bitmask-and-geometry2d-on-clipper2.md) (Clipper2)
**Started:** 2026-11-07

## Goal

- `NavigationPolygon : Resource` (Godot's names): `Vertices`, `Polygons` (convex, indices), `Outlines` (traversable
  outlines, the bake's input), `AgentRadius` (10 px), `CellSize` (1 px, vertex quantum), `BakingRect`,
  `ParsedCollisionMask`, `SourceGeometryMode` + `SourceGeometryGroupName`; `MakePolygonsFromOutlines()`.
- `NavigationRegion2D : Node2D`: `NavigationPolygon`, `Enabled`, `NavigationLayers`, `EnterCost`, `TravelCost`,
  `UseEdgeConnections`; full global transform (rotation and scale allowed); `Bake()`/`BakeData()`.
- `NavigationLink2D : Node2D`: `StartPosition`, `EndPosition`, `Bidirectional`, `Enabled`, `NavigationLayers`,
  `EnterCost`, `TravelCost`.
- The map builds a navmesh at sync: polygons in global space, vertices welded on the map's `CellSize`, shared edges joined
  (inside and across regions), near edges of different regions joined within `EdgeConnectionMargin`, links attached to the
  nearest polygon within `LinkConnectionRadius`; a polygon lookup grid. Queries: A* over polygons with Godot's entry
  points, enter and travel costs, layers; funnel (`CorridorFunnel`), portal midpoints (`EdgeCentered`), entry points
  (`None`); partial paths to the polygon closest to the target; a start off the mesh snaps to it. Grid regions still win
  where they contain the start. 0 B per query once warm; deterministic (fixed orders, scalar maths).
- Bake: traversable outlines (or `BakingRect`) shrunk by the agent radius, minus obstructions grown by it — static
  bodies' collision shapes, tile layers' collision polygons, `NavigationObstacle2D`s with `AffectNavigationMesh` — through
  Clipper2 (integer, miter joins), then ear clipping with hole bridging and Hertel–Mehlhorn into convex polygons.
- `NavigationAgent2D` follows navmesh paths and raises `LinkReached`; `NavigationPath2D.Owners` per point.
- Debug draw (polygon edges, region borders, edge connections, links), editor inspector Bake/Clear, link gizmo, Demo scene.

## Not in scope

- An outline editor with viewport handles (outlines are edited in the inspector, or replaced by `BakingRect`).
- Baking on a thread (G3.10 is about queries), navmesh avoidance changes, 3D (G3.11/G3.12).

## Steps

1. [ ] Geometry: `Polygons/NavigationPolygonBuilder` (Clipper64 offsets and booleans, hole bridging, exact ear clipping,
   Hertel–Mehlhorn). Verify: `NavigationPolygonBuilderTests` — area kept, every polygon convex and counter-clockwise,
   holes left out, deterministic, degenerate input.
2. [ ] `NavigationPolygon` resource + `.mfres`/inline round trip. Verify: `NavigationPolygonTests`.
3. [ ] `NavigationRegion2D`, `NavigationLink2D`, map navmesh build at sync, queries, funnel, links, costs, layers,
   partial paths, closest point. Verify: `NavigationMeshQueryTests`.
4. [ ] Bake from static bodies, tile layers and carving obstacles, agent radius, baking rect, groups. Verify:
   `NavigationPolygonBakeTests` (`SerialBox2D` where bodies are needed).
5. [ ] Agent on navmeshes: `Owners`, `LinkReached`. Verify: `NavigationAgent2DTests` additions; allocation gate case
   (agents over a navmesh, 0 B); `NavmeshBenchmarks`.
6. [ ] Debug draw and editor: inspector Bake/Clear + status, link gizmo. Verify: `NavigationEditorTests` additions.
7. [ ] Demo `navigation_mesh_2d` scene + tests + screenshot (only its PNG in the diff).
8. [ ] Docs (`navigation.md` section + Changes from the proposal, proposal trimmed, milestones row, README, landing
   page, AGENTS.md), ADR via mf-decide, log; gates; one push; PR; Auto-fix.

## Done when

- The new test classes pass in `just test`; the navmesh allocation case reports 0 B.
- `docs/images/demo/navigation_mesh_2d.png` shows a baked navmesh with agents, and is the only demo PNG in the diff.
- All gates in `.agents/workflow.md` pass locally; the PR is open with Auto-fix on.

## Open questions

- None that change what is built: open question 2 (Clipper2 covers offsets) is answered by step 1's tests.
