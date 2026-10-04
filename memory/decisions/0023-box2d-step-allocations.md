# ADR 0023 — Accept Box2D.NET's per-step allocations; gate the engine's share

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M6

## Context

The M6 lane requires zero managed allocations per steady-state physics step (~500 bodies). Measured on the pinned
Box2D.NET 3.1.654: `b2World_Step` itself allocates every step — `new B2StepContext()` and solver arrays such as
`new ArraySegment<B2SolverBlock>[24]` (≈350 B per step for a small world, ≈850 B with 300 bodies; 352 B in the 1k-body
benchmark). `b2World_CastRayClosest` also allocates (`new B2RayResult()`). Patching the library is not allowed
(approved packages only, no vendored forks); Box2D.NET is the approved 2D engine.

## Decision

- Keep Box2D.NET 3.1.654. The engine avoids every allocating Box2D API it can (closest-ray via `b2World_CastRay` with a
  static callback, contacts/sensors read into reused arrays, static filter/query delegates with the space as context).
- `PhysicsSpace2D.MeasureLibraryAllocations` (internal test hook) totals bytes allocated inside `b2World_Step`; the 2D
  allocation test requires *everything else* in a steady-state frame to allocate 0 bytes.
- 3D (Jitter2) is held to the strict zero-allocation gate single-threaded, including the render-test Sandbox scene.
  Multi-threaded, Jitter2's worker pool allocates 56 B inside `World.Step` about once per 5 000 steps (measured with
  Jitter2 alone); `PhysicsSpace3D.MeasureLibraryAllocations` excludes that share the same way.

## Consequences

- 2D games produce a little garbage per physics step (Gen0 only, ~20–50 KB/s at 60 Hz).
- Revisit when a Box2D.NET release pools its step context; update the pin deliberately and re-measure.
