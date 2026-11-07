---
summary: >-
  G3.11 (ADR 0263): NavigationServer3D, NavigationGrid3D (an AStarGrid2D on the XZ plane, baked in a height band against
  3D static bodies) and NavigationAgent3D with PathHeightOffset; the 2D grid query shared; editor Bake/Clear; Navigation
  3D Demo scene; 0 B.
---

# 2026-11-07: 3D grids and NavigationAgent3D (G3.11)

Branch `feature/navigation-3d` (stacked on `feature/navigation-async`). Plan:
[G3.11 3D grids](../plans/2026-11-07-navigation-3d-grids-g3-11.md). As built:
[Navigation → 3D grids](../../docs/design/navigation.md#3d-grids-g311).

## What changed

- **Engine:** `NavigationServer3D`, `NavigationMap3D`, `NavigationPath3D`, `NavigationPathQuery3D`,
  `NavigationWaypoint3D`, `Nodes3D/NavigationGrid3D`, `Nodes3D/NavigationAgent3D`, `Baking/GridPhysicsBaker3D`,
  `NavigationDebugDraw3D`; `World3D.NavigationMap`; the 2D map's grid query extracted (`QueryGrid`, `ClosestOnGrid`);
  the engine and `HeadlessHost` register the 3D server; the dev overlay's Navigation checkbox switches both servers.
- **Editor:** `NavigationGrid3DInspector` (Bake/Clear with undo, status); Visible Navigation reaches the 3D server.
- **Demo:** `navigation_3d` (Navigation 3D, in the 3D category): walls on a floor, a half-metre grid, eight capsule
  chasers; screenshot; es translations.
- **Tests:** `Navigation3DTests` (7, with the 3D allocation case), an editor case, `Navigation3DSceneTests` (3).
- **Docs:** navigation (section, types, editor, tests, changes), proposal trimmed, demo, AGENTS.md, README, landing page.

## Decisions

- [ADR 0263](../decisions/0263-navigation-3d-grids.md): separate 3D types over the shared grid code; the bake's height
  band; `PathHeightOffset` both ways; no 3D avoidance or async agents yet.

## Dead ends

- A first Demo framing put the chasers' start behind the panel; the camera now looks left of the courtyard's centre and
  the chasers start at the near-left corner.

## State changes

For the state sync (ADR 0280); this PR leaves `docs/milestones.md` and `memory/context/current-state.md` as on `main`.

- milestones G3b: after G3.10's entry add "G3.11 3D grids ✅ (ADR 0263: `NavigationServer3D`, `NavigationGrid3D`,
  `NavigationAgent3D`, Demo Navigation 3D)"; "G3.11 ⬜" goes.
- state: the navigation entry in What exists gains "3D grids with `NavigationAgent3D` (G3.11, ADR 0263)".

## Verified

- See the PR body.

## Not verified

- The Demo scene's look beyond the screenshot.

## Next

- G3.12 3D navigation meshes on DotRecast.
