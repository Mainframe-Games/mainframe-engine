---
summary: >-
  G3.12 (ADR 0264): 3D navigation meshes — NavigationMesh baked with Recast through DotRecast (the owner's choice, AOT and
  allocation checked first), NavigationRegion3D on the 3D map, Detour queries at 0 B, editor Bake/Clear, Navigation Mesh
  3D Demo scene. The G3 proposal is fully shipped and moved into navigation.md.
---

# 2026-11-07: 3D navigation meshes on DotRecast (G3.12)

Branch `feature/navigation-mesh-3d` (stacked on `feature/navigation-3d`). Plan:
[G3.12 3D navigation meshes](../plans/2026-11-07-navigation-meshes-3d-g3-12.md). As built:
[Navigation → 3D navigation meshes](../../docs/design/navigation.md#3d-navigation-meshes-g312).

## What changed

- **Dependency:** DotRecast 2026.3.1 (`DotRecast.Recast`, `DotRecast.Detour`, zlib) in `Directory.Packages.props` and
  the engine; THIRD_PARTY_NOTICES (its LICENSE.txt); AGENTS.md's dependency list.
- **Engine:** `Meshes3D/NavigationMesh` (resource), `Meshes3D/RecastBaker` (scene parse to a triangle soup, solid
  interiors as convex volumes, Recast build, Detour tile write/read), `Meshes3D/DetourMesh3D` (a region's `DtNavMesh` and
  query, local-space queries, straight paths), `Nodes3D/NavigationRegion3D`; `NavigationMap3D` holds mesh regions, loads
  them at the sync and answers queries where no 3D grid contains the start; `NavigationPath3D` gains navmesh points; the
  3D debug draw draws polygons.
- **Editor:** `NavigationRegion3DInspector` (Bake/Clear with undo, status).
- **Demo:** `navmesh_3d` (Navigation Mesh 3D): a terrace by a ramp, a deck by stairs, a crate and pillars; six capsules;
  `NavigationDemo3D.KeepHitHeight` and `Ring`; screenshot; es translations.
- **Tests:** `NavigationMesh3DTests` (9, with the navmesh allocation case), a resource round trip, an editor case,
  `NavigationMesh3DSceneTests` (3).
- **Docs:** navigation (section, types, editor, tests, changes, Open questions), the proposal
  `docs/design/future/navigation.md` moved into it and its links repointed (milestones row link target, tile maps, ADRs
  0227/0228, plans and logs), demo, README, landing page.

## Decisions

- [ADR 0264](../decisions/0264-navigation-meshes-3d-dotrecast.md): DotRecast, the tile in the resource, solid interiors
  marked, paths within one region.

## Dead ends

- Recast's closed-mesh artefact: the 2 m of air inside a pillar was walkable floor (an island in the pillar). Each solid
  primitive now marks its footprint unwalkable below its lowest top.
- Stairs at the floor's very edge were unreachable: erosion by the agent radius removed the bottom step. The Demo's
  stairs now climb the deck's open side with floor in front.

## State changes

For the state sync (ADR 0280); this PR leaves `docs/milestones.md` and `memory/context/current-state.md` as on `main`
(only the G3b row's second link target moved, with the proposal).

- milestones G3b: status 🟡 → ✅; the notes become "G3.7 avoidance (ADR 0250); G3.8 grid polish closed as not needed
  (owner, 2026-11-07); G3.9 2D navigation meshes (ADR 0261); G3.10 async queries (ADR 0262); G3.11 3D grids and
  `NavigationAgent3D` (ADR 0263); G3.12 3D navigation meshes on DotRecast (ADR 0264)"; first link
  `design/navigation.md#3d-navigation-meshes-g312`.
- state: the navigation entry in What exists gains "3D navigation meshes on DotRecast (G3.12, ADR 0264)"; Next steps
  drops "G3b's rest"; Dependencies (if listed) gain DotRecast.

## Verified

- See the PR body.

## Not verified

- Bake times on large levels; the Demo's look beyond the screenshot.

## Next

- G3b is complete. Follow-ups when a game needs them: 3D region joins and links, 3D avoidance, threaded or tiled bakes.
