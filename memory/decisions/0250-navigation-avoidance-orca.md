# ADR 0250 — Navigation avoidance: an RVO2 port run by the server after the physics step

- **Date:** 2026-11-06
- **Status:** accepted
- **Milestone:** G3b (part: G3.7)
- **Docs:** docs/design/navigation.md#avoidance-g37, memory/plans/2026-11-06-navigation-avoidance-g3-7.md

## Context

The navigation proposal (G3.7) asked for ORCA avoidance for `NavigationAgent2D` and a `NavigationObstacle2D`, with
Godot's names, run per map in the server's fixed step, deterministic and allocation-free, and left the algorithm source
open (open question 3): write ORCA from the paper, or port RVO2 (Apache-2.0, what Godot uses). No new NuGet packages.

## Decision

- **Port RVO2 2.0's 2D solver** (`computeNewVelocity`, `linearProgram1`–`3`) to C# in `Avoidance/Orca2D.cs`, keeping its
  copyright and Apache-2.0 notice in the file, the licence text beside it (`LICENSE-RVO2.txt`, the canonical
  `LICENSE-2.0.txt`) and a section in `THIRD_PARTY_NOTICES.md`. A port keeps RVO2's tested edge cases (non-convex
  vertices, foreign legs, the 3D linear program) that a fresh implementation would have to rediscover.
- **Changes to RVO2**, listed in the file: caller-owned buffers; a per-neighbour responsibility (0.5 when the
  neighbour avoids back this tick, else 1); exactly head-on approaching relative velocities tilted 1 % to the left of
  the line between the agents (RVO2's examples add random noise, which would break determinism); scalar dot and cross
  products; guards where RVO2 divides by zero.
- **Run** in `NavigationServer2D.FixedStep` after every map synced, which follows the physics step: all results first,
  then `VelocityComputed` per agent in registration order. Only agents that called `SetVelocity` this tick are solved;
  avoiding agents that did not stand still for the others.
- **Neighbours** through a uniform hash grid per map (counting sort, ring search with a shrinking range); polygon edges
  through per-polygon bounds instead of RVO2's obstacle k-d tree (a level has few edges, and polygons move every tick).
- **Priority and layers:** an agent ignores lower-priority agents and agents off its mask; whoever is not avoided back
  avoids in full.

## Consequences

- Behaviour matches Godot's agents closely enough to port Godot code, with three differences documented in the design
  doc: `TimeHorizonObstacles` defaults to 0.5 s (0 divides by zero), polygon winding never pulls agents in, and head-on
  pairs always pass on the same side.
- ORCA stays local: walls belong in the path (solid cells); obstacles keep bodies off each other and off corners.
- The licence is Apache-2.0 for that one file inside an MIT engine: the notice must travel with distributions, as the
  other third-party notices do.
- The neighbour grid's cell size follows the largest radius, so one huge radius makes the grid coarse; a game with very
  mixed radii may want per-size maps later.
