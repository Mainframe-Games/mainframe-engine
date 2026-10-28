# ADR 0227 — Grid navigation core: Godot's AStarGrid2D with spans, centre positions and deterministic JPS

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/navigation.md (from the proposal docs/design/future/navigation.md, G3.1–G3.2)

## Context

G3a needs a grid pathfinder (`AStarGrid2D`) and point graphs (`AStar2D`/`AStar3D`) that games, tools and the navigation
server share. The proposal asks for Godot's behaviour, allocation-free queries and identical paths on every machine,
and leaves open whether jump point search should honour weights (open question 4) and whether grids may rotate (5).

## Decision

- **Godot's model, span results.** Diagonal modes, the four heuristics as step cost and estimate, weight scales on
  the entered cell, solid cells, partial paths (Godot's rule: the visited cell with the lowest estimate, then the lowest
  cost) and `MaxSearchNodes`. Queries write into caller spans and return a `GridPathStatus` (`BufferTooSmall` reports the
  size needed); allocating overloads exist for tools and tests. After `Update()`, queries, cell edits and fills allocate
  nothing (`GridSearch`: g, h, parent, a per-query stamp, an indexed binary heap; stamps clear only when they wrap).
- **Positions are cell centres** (`Offset + (id + 0.5) · CellSize`), not Godot's corner + offset: every caller wanted
  centres, and `GetPointAt` is the exact inverse. **Only `Region` marks the grid dirty**, and `Update()` keeps the cells
  when the region did not change, so moving a grid (its `Offset`) or changing the search settings never wipes cells.
- **Jump point search for every diagonal mode** with the pruning and forced-neighbour rules of the matching
  PathFinding.js (MIT) variants (always / at most one obstacle / no obstacles / never), re-implemented iteratively (no
  deep recursion on 2048-cell lines); every proposed step still goes through the same `CanStep` as plain A*, so a rule
  can only cost speed, never an illegal move. **JPS ignores weights, like Godot** (open question 4, the design text's
  choice): the documentation says so, and weighted grids leave `JumpingEnabled` off. A failed JPS search with
  `allowPartialPath` reruns plain A* for the partial path (JPS skips the cells Godot's rule needs). JPS paths list
  every cell, so output is the same shape with JPS on or off. Tests: on 200 seeded random grids per mode JPS costs what
  A* costs, and A* costs what a brute-force Dijkstra costs for every admissible compute/estimate pair.
- **Determinism.** Costs use only `+`, `*` and `MathF.Sqrt` on floats; the heap breaks ties by f, then h, then the lower
  index; neighbours expand in a fixed order; point graphs compute distances with scalar code (no SIMD dot product, whose
  summation order could differ between CPUs). A golden maze locks the A* and JPS paths (ids and hash).
- **Overridable costs without reflection.** `ComputeCost`/`EstimateCost` are virtual (Godot's `_compute_cost`); the
  built-in fast path applies when the instance's type is exactly `AStarGrid2D` (or `AStar2D`/`AStar3D`), so trimming and
  AOT need no reflection.
- **Axis-aligned grids** (open question 5): `NavigationGrid2D` ignores its global rotation and scale and warns once.
  Rotated grids can come with navmesh polygons (G3.9).

## Consequences

- JPS is a large win on mazes (a 256² maze: ~0.2 ms vs ~3–4 ms for A*) and a loss on open fields (~0.24 ms vs ~0.02 ms:
  the straight-line octile A* barely expands), so it stays opt-in per grid.
- `AStar2D` stores positions as `Vector3` with z = 0 (one shared `PointGraph`); distances are unchanged.
- A path is identical for the same grid and query on every OS and CPU; physics is still not cross-machine
  deterministic (physics.md), so movement is not.
