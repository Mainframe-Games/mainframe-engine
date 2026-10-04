# ADR 0020 — Physics: single precision, no lockstep determinism (opt-in reproducible solver)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M6 (W3 lane B)

## Context

The physics proposal left two open questions: offer Jitter2's deterministic solve mode for lockstep games, and
double-precision builds (`Jitter2.Double`) for large worlds. The M0–M10 plan's default: no deterministic mode,
single precision. Golden-image render tests, however, need the same frame N on every run, and Jitter2's regular
solver is non-deterministic even on one thread (its contact reorder pass depends on hash-set order); two runs of
the physics test scene differed in ~17 % of pixels.

## Decision

1. **Single precision** everywhere: `Jitter2` (float) and Box2D.NET (float). No `Jitter2.Double` package.
2. **No lockstep/cross-platform determinism** is offered. The default 3D solver is Jitter2's regular (parallel,
   fastest) solver; Box2D steps single-threaded.
3. `PhysicsSettings3D.Deterministic` (default **off**) selects Jitter2's island-based `SolveMode.Deterministic`.
   It is documented as "identical results run to run with the same build on the same machine" — for golden
   tests and replays — not as a networking guarantee. The physics render tests use it on one thread.

## Consequences

- Multiplayer stays server-authoritative (M5): the server simulates, clients interpolate snapshots.
- Large worlds need origin shifting later; revisit doubles only with a concrete need.
- Physics render goldens are reproducible locally (`PhysicsCapturesAreDeterministicAcrossRuns`); lavapipe goldens
  must be recorded on CI like the others.
