# ADR 0074 — Alpha-tested shadow casters for cutout materials

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M4 (W4 lane m4)

## Context

M3 left "cutout materials cast solid shadows": the instanced caster pipelines read only positions and had no
fragment work. Fences and foliage therefore cast solid blocks.

## Decision

- **Caster items:** a caster surface whose material is `AlphaMode.Cutout` carries its `MaterialGpu`. The caster
  sort key includes the material id, after cull and mirror: opaque casters first, then cutout casters grouped by
  material.
- **Pipelines:** `ShadowSystem.GetInstancedCasterPipeline(point, cull, mirrored, cutout: true)` builds variants
  that:
  - also read the UV (location 5);
  - run `ShadowCutout.vk.frag` / `ShadowPointCutout.vk.frag`, which discard below the material's alpha cutoff
    through the shared `material.glsl`. The include's set is now a `MATERIAL_SET` define (default 2, here 1).
- **Layouts:** the cutout layouts are set 0 the light view-projection plus set 1 the mesh material set layout. The
  mesh renderer registers that layout once (`SetMaterialSetLayout`). The push range equals the plain caster
  layouts' range, so the light-matrix set and the point light's push constants stay valid when the pipelines
  switch.
- **Blended materials** still cast no shadow.

## Consequences

- Cutout shadows have holes (the `shadow-cutout` render test compares them with an opaque fence). They work for
  point lights too.
- A material switch inside a shadow pass costs one descriptor bind. Opaque casters are unaffected.
