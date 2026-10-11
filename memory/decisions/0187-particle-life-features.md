# ADR 0187 — GpuParticles2D: one-shot bursts, explosiveness and per-life curves

- **Date:** 2026-10-11
- **Status:** accepted
- **Milestone:** Crash Site Defense art polish
- **Docs:** docs/design/canvas.md#particles

## Context

The 2D particles (ADR 0116 era) covered looping ambient weather only: every particle restarted on an even phase with one
colour, so smoke could not fade, dust could not puff once, chips could not burst and slow. Crash Site Defense's art pass
needs footstep dust, chop/mine chips, campfire fire, embers and smoke, and wreck smoke.

## Decision

Godot's names, on the CPU simulation:

- `GpuParticles2D.OneShot` (each particle emits once; then `Emitting` goes false and `Finished` fires),
  `Explosiveness` (phases squeezed toward 0), `Restart()` reusing the buffers (no allocation once sized).
- `ParticleProcessMaterial.DampingMin/Max`, `AngularVelocityMin/Max`, `LifetimeRandomness`, `ScaleCurve` and
  `ColorRamp`. Godot types these as CurveTexture / GradientTexture1D; here they are a `Curve` (new resource: linear
  points) and a `Gradient` directly, sampled at age / life.
- A particle with no lifetime randomness still lives until its restart, so existing emitters (and goldens) are unchanged.
- `LiveCount` no longer allocates.

## Consequences

Unit tests in `GpuParticles2DTests` (one-shot, explosiveness, restart, randomness + damping, curve sampling). Still no
GPU simulation, no sub-emitters, no turbulence.
