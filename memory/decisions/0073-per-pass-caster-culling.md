# ADR 0073 — Per-pass shadow caster culling with compact instance writes

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M4 (W4 lane m4)

## Context

M3 drew every shadow caster into every shadow pass, from one instance range written once per frame. With cascades,
atlas tiles and cube faces, a frame can have 39 passes, and most casters are outside most of them. Shadows v2 also
asks for passes without casters to be skipped.

## Decision

- **Two phases:** `ShadowSystem.RenderShadows` plans every pass first. It then calls a `cull(state, in ShadowPass)`
  callback for each pass before recording, and a `draw(state, cb, in ShadowPass)` callback for each pass that
  renders.
- **Culling:** `MeshRenderer.CullShadowCasters` tests each caster's world bounds against the light's sphere (lights
  with a range), then against the pass frustum. It writes the survivors' instance data, in caster sort order, into
  the frame's instance buffer: one compact range per pass, as runs of equal (cull, mirrored, cutout material, mesh
  surface). `DrawShadowCasters` records one instanced draw per run.
- **Empty passes:** they are skipped, and their map is marked lit in the uniforms:
  - an empty cascade is disabled;
  - an empty atlas tile, or a cube with no caster on any face, turns that light's shadow code off;
  - a cube with any caster renders all six faces, because every face is sampled.
- **Cascade pull-back:** the union of the caster bounds feeds the cascades' near-plane pull-back.
- **Unbounded visuals:** visuals drawn one by one (Spine) have no bounds. When one is visible and casting, every
  pass renders and they draw into all of them.

## Consequences

- Instance writes are proportional to the casters actually visible to each pass, not casters × passes.
- 10 000 casters under a 4-cascade sun cost about 0.48 ms of CPU per frame (cull + writes) and allocate nothing.
- Bounds-less casters keep every pass alive. Giving Spine bounds is a follow-up.
