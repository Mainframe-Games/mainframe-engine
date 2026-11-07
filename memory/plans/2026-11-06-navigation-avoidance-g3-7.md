# Plan: G3.7 local avoidance for 2D navigation agents

**Status:** done, see [log](../logs/2026-11-06-navigation-avoidance.md)
**Docs:** [Navigation proposal, later phases](../../docs/design/navigation.md#changes-from-the-proposal) (§ Avoidance (G3.7), Testing, task 2,
open question 3), [Navigation](../../docs/design/navigation.md) (what G3a shipped),
[ADR 0227](../decisions/0227-grid-navigation-core.md), [ADR 0228](../decisions/0228-navigation-server-agents-and-sources.md)
**Started:** 2026-11-06

## Goal

- ORCA (a port of RVO2 2.0's 2D solver, Apache-2.0, notice kept) for `NavigationAgent2D`: `AvoidanceEnabled`, `Radius`,
  `MaxSpeed`, `NeighborDistance`, `MaxNeighbors`, `TimeHorizonAgents`, `TimeHorizonObstacles`, `AvoidanceLayers`,
  `AvoidanceMask`, `AvoidancePriority` (Godot names). `SetVelocity` hands the preferred velocity to the server;
  `VelocityComputed` brings the safe velocity back from the server's fixed step, after the physics step.
- `NavigationObstacle2D`: a radius (moving, with `Velocity`) and/or a polygon (`Vertices`, placed by the global
  transform each step), `AvoidanceEnabled`, `AvoidanceLayers`.
- `NavigationServer2D.FixedStep`: sync, then avoidance per map. Neighbours from a uniform hash grid owned by the map;
  deterministic (ties by registration index, scalar maths); 0 B per tick once warm.
- Debug draw and editor gizmos for agent radius and obstacle shapes. Demo `navigation_2d` shows it.

## Steps

1. [x] Solver `Avoidance/Orca2D.cs` (RVO2 port) + `Avoidance/AvoidanceSimulation2D.cs` (entities, hash grid, obstacle
   edges, scratch). Verify: unit tests below.
2. [x] `NavigationObstacle2D`, agent exports, map registration, server step. Verify: `NavigationAvoidanceTests` —
   head-on pair passes without touching; crowd through a gap arrives with no overlap and nobody in the walls; static
   polygon and moving radius obstacle never touched; two runs bit-identical; layers/mask and priority; pass-through
   when avoidance is off.
3. [x] Allocation gate case with avoidance on (64 agents, moving obstacles): 0 B. Benchmark `AvoidanceBenchmarks`.
4. [x] Debug draw (agent radius, obstacles) and editor gizmo (selected agent radius, selected obstacle shape) +
   editor test.
5. [x] Demo: chasers avoid each other, a patrolling radius obstacle and a pillar polygon; panel toggle "Avoidance";
   `Demo.Tests` still pass; `just demo-screenshots`, commit only `navigation_2d`'s PNG.
6. [x] Docs: `navigation.md` Avoidance section + Changes from the proposal; proposal trimmed; milestones G3b row; README
   and landing page lines; THIRD_PARTY_NOTICES (RVO2); ADR 0250 (RVO2 port, answers open question 3).
7. [x] Gates (`.agents/workflow.md`), log, state, check.sh, one push, PR, Auto-fix.

## Done when

- `NavigationAvoidanceTests` and the new allocation case pass in `just test`; the editor gizmo test passes.
- `AvoidanceBenchmarks` runs and reports 0 B.
- `docs/images/demo/navigation_2d.png` shows agents avoiding, and is the only demo PNG in the diff.
- All gates in `.agents/workflow.md` pass locally; the PR is open with Auto-fix on.
