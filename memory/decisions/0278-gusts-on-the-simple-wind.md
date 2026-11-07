# ADR 0278 — Grass gusts with the trees: WindGusts scales the simple wind by the shared gust noise

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8e (G8e.5 rest)
- **Docs:** docs/design/materials-and-meshes.md (Wind), docs/design/procedural-trees.md#hierarchical-wind,
  memory/plans/2026-11-07-wind-gusts.md

## Context

The G8e proposal (ADR 0164) gives foliage one gust field: "a 256² tileable gust texture … sampled per instance in the
vertex stage, so gusts roll across the meadow and the treetops together; grass (G8a) uses it too". ADR 0172 built the
gusts as simplex noise inside `windHierarchy` only (no texture, no binding), so hierarchical trees gust and grass, which
runs the simple wind (`windOffset`), sways at a steady strength.

## Decision

- **`windGust(p, dir, t)`** in `include/wind.slang` is the one gust field (the noise ADR 0172 inlined): simplex noise
  over world XZ (× 0.015: features of about 60 m) rolling downwind, −1..1. `windHierarchy` keeps `1 + 0.4 · gust` at
  the instance origin, unchanged.
- **`WorldEnvironment.WindGusts`** (0..1, default 0) goes into the unused `frame.windParams.w`; the simple wind
  multiplies its strength (flutter and bend) by `1 + WindGusts · windGust(worldPos)`. At 0 the shader takes the old
  path, so every existing golden is unchanged. The prepass's velocity uses last frame's `windParams` as before.
- **The Forest sets 0.4**, the trees' amplitude, so the meadow and the canopy gust with the same field.
- **`--bake-tree-clusters` is not built:** the cluster atlases already bake on first use into the per-machine tree bake
  cache (ADR 0172), so a command would only warm one machine's cache.

## Ruled out

- **A gust texture in set 0** (the proposal): a binding in every foliage vertex stage for what one noise call gives.
- **Gusting the simple wind always:** every foliage golden and every game's grass would change; the look is a choice.

## Consequences

- Grass sways harder in passing gusts in the Forest; the cost is one simplex noise per grass vertex when on.
- The `foliage-gusts` golden on both drivers; `TreeWindTests` still covers the hierarchy's ordering on the CPU (the
  proposal's render check "trunk tip moves less than branch tips" stays a unit test).

## Revisit when

- Wind needs to be authored per area (a calm hollow): a gust/strength map would replace the uniform field.
