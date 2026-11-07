---
summary: >-
  G3.7 (ADR 0250): ORCA local avoidance for NavigationAgent2D (an RVO2 2.0 port, Apache-2.0 notice kept) and
  NavigationObstacle2D (radius, polygon, moving), run per map in the server's fixed step after physics; 0 B, deterministic.
---

# 2026-11-06: Navigation avoidance (G3.7)

Branch `feature/navigation-avoidance`. Plan: [G3.7 local avoidance](../plans/2026-11-06-navigation-avoidance-g3-7.md).
Proposal: [Navigation, later phases](../../docs/design/navigation.md#changes-from-the-proposal); as built:
[Navigation → Avoidance](../../docs/design/navigation.md#avoidance-g37).

## What changed

- **Engine:** `Avoidance/Orca2D.cs` (RVO2's `computeNewVelocity` and linear programs, ported; `LICENSE-RVO2.txt`),
  `Avoidance/AvoidanceSimulation2D.cs` (per map: entities, hash-grid neighbours, polygon edges, owned buffers),
  `NavigationObstacle2D`; `NavigationAgent2D` gains Godot's avoidance exports, `SafeVelocity`, `SubmittedVelocity`,
  `SetVelocityForced`, and `SetVelocity` submits when avoiding; `NavigationMap2D.Obstacles` and `StepAvoidance`;
  `NavigationServer2D.FixedStep` runs it after the sync; profiler scope `Navigation avoidance`; debug draw of agent
  radii, safe velocities and obstacles (`NavigationDebugColors.Avoidance`, `.Obstacle`).
- **Editor:** the selected obstacle draws its circle and polygon; an avoiding agent's gizmo adds its radius.
- **Demo:** `navigation_2d` chasers avoid each other, the walls (an obstacle rectangle per wall) and `NavPatrol`, a
  rolling boulder; arrived chasers hold their spot and make way; panel toggle Avoidance; two target rings; screenshot.
- **Tests and benchmarks:** `NavigationAvoidanceTests` (12), a second allocation-gate case, an editor gizmo test, a Demo
  test; `AvoidanceBenchmarks` (64 agents ≈ 43 µs, 256 ≈ 0.23 ms per tick, 0 B; machine busy, not in the baseline).
- **Docs:** navigation (Avoidance section, Changes from the proposal), the proposal trimmed, milestones G3b row, demo,
  AGENTS.md, README, landing page, THIRD_PARTY_NOTICES (RVO2).

## Decisions

- [ADR 0250](../decisions/0250-navigation-avoidance-orca.md): port RVO2 (answers the proposal's open question 3);
  per-neighbour responsibility; deterministic head-on symmetry breaking; bounds-culled polygon edges, no k-d tree.

## Dead ends

- Plain ORCA stalls exactly head-on pairs face to face (truncation projection only slows them). RVO2's examples add
  random noise; a 1 % tilt of exactly collinear approaching relative velocities fixes it deterministically.
- In the Demo, arrived chasers that only stood formed a wall late arrivals could not pass (ORCA is local): arrived
  chasers now keep submitting a hold-the-spot velocity, so they make way and return. Bodies sliding on walls broke the
  others' predictions (pairs at 13 px): giving each wall an obstacle polygon fixed it (≥ 17 px in the test).
- The allocation gate needed 600 warm-up ticks, not 300: avoidance moves the bodies off the 300-tick target cycle, so
  path buffers keep growing for longer.

## Verified

- See the PR body: build, Release build, unit tests, render tests, format, shaders, check.sh; Demo tests.
