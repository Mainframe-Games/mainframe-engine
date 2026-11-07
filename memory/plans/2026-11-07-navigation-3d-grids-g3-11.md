# Plan: G3.11 3D grids and NavigationAgent3D

**Status:** done, see [log](../logs/2026-11-07-navigation-3d-grids.md)
**Docs:** [Navigation proposal, later phases](../../docs/design/future/navigation.md) (§ Later phases: 3D, task 6),
[Navigation](../../docs/design/navigation.md) (the 2D server, maps, grids and agents)
**Started:** 2026-11-07

## Goal

- `NavigationServer3D` with one `NavigationMap3D` per `World3D` (the 2D server's timing and sync points), registered after
  `PhysicsServer3D` by the engine and `HeadlessHost`.
- `NavigationGrid3D`: an `AStarGrid2D` on the XZ plane at the node's height, the 2D grid's exports and `NavigationGridData`,
  baked with one overlap box per cell against 3D static bodies (between the climb height and the agent height).
- `NavigationAgent3D`: the 2D agent's rules and signals in `Vector3`, plus `PathHeightOffset`.
- Queries share the 2D map's grid code; 0 B per tick; debug draw; editor Bake/Clear; a Demo 3D scene.

## Not in scope

- 3D avoidance, async 3D agents, an agent gizmo in the 3D view; 3D navigation meshes (G3.12).

## Steps

- [x] Extract the 2D map's grid query (`QueryGrid`, `ClosestOnGrid`). Verify: the 2D navigation tests unchanged.
- [x] Server, map, path, grid, baker, agent, debug draw; registration; dev overlay and editor toggles. Verify:
  `Navigation3DTests`.
- [x] Editor `NavigationGrid3DInspector`. Verify: `NavigationEditorTests` case.
- [x] Demo `navigation_3d` + tests + screenshot (only its PNG among the demo images).
- [x] Docs, ADR 0263, log; gates; PR.

## Done when

- `Navigation3DTests` (with the 0-byte case), the editor case and `Navigation3DSceneTests` pass.
- All gates pass locally; the PR is open with Auto-fix on.
