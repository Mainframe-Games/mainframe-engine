# ADR 0263 — 3D navigation starts with grids on the XZ plane that reuse the 2D grid code

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G3b (part: G3.11)
- **Docs:** docs/design/navigation.md#3d-grids-g311, memory/plans/2026-11-07-navigation-3d-grids-g3-11.md

## Context

G3.11 asked for `NavigationServer3D` with the 2D map/region/agent split, `NavigationAgent3D` (`Vector3`, plus
`PathHeightOffset`) and a `NavigationGrid3D`: an `AStarGrid2D` on the XZ plane at a height, baked with `IntersectShape`
against 3D static bodies, for top-down 3D games, tower defence and strategy. 3D navigation meshes come in G3.12.

## Decision

- **Separate 3D types, shared grid code.** `NavigationServer3D`/`NavigationMap3D`/`NavigationPath3D` mirror the 2D ones
  (one map per `World3D`, the same sync points, `IterationId`), and the grid part of a query is the 2D map's, extracted
  as `NavigationMap2D.QueryGrid` and `ClosestOnGrid`: the XZ plane is the grid's plane, points are lifted to the grid's
  height. Grids keep `NavigationGridData`, so bakes, the RLE format and its tests serve both.
- **The bake's height band.** Each cell's overlap box spans from `AgentMaxClimb` (0.25 m) to `AgentHeight` (1.5 m) above
  the grid — Godot's agent defaults — so the floor slab and low steps under a grid do not block it and overhead geometry
  does not either; only `StaticBody3D` hits count, through `PhysicsDirectSpaceState3D` only (no Jitter2 calls).
- **`PathHeightOffset` both ways.** It is the body origin's height above the surface: queries and distances use the
  origin lowered by it, and `GetNextPathPosition` returns path points raised by it, so a capsule's
  `GetNextPathPosition() - GlobalPosition` is level.
- **No 3D avoidance or async 3D agents yet.** `SetVelocity` raises `VelocityComputed` at once; the async workers (ADR
  0262) serve 2D maps. Both can follow when a 3D game needs them.

## Ruled out

- **One generic server/map over both dimensions:** every 2D call site and test would change for a type parameter, and
  G3.12's navmesh is 3D-only anyway.
- **A 3D grid that rotates or follows terrain:** the proposal's scope is flat top-down levels; uneven ground is G3.12's.

## Consequences

- 3D games get grids, baking, agents and the debug draw with little new code; 2D and 3D grids behave the same.
- Two agent classes to keep in step (as in Godot).

## Revisit when

- A 3D game needs avoidance or async paths, or grids on slopes.
