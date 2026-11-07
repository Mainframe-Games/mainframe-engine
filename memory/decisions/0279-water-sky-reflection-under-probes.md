# ADR 0279 — Water's sky reflection takes the probes' specular occlusion; wet banks deferred

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8e (G8e.6 rest)
- **Docs:** docs/design/water.md#reflections-under-probes, memory/plans/2026-11-07-water-probe-reflections.md

## Context

ADR 0173 shipped G8e.6's water (scene copy, refraction, SSR, caustics, falls) with two follow-ups: "reflections exist
only for what is on screen; misses show the sky, too bright under a canopy until G8e.1's probes give a sky visibility to
multiply by", and the proposal's wet banks ("the terrain splat darkens albedo and lowers roughness within 0.3 m above
the water level"). G8e.1's probes (ADR 0170) now store the sky visibility per probe, and lit PBR surfaces already use it
for their specular occlusion.

## Decision

- **`waterSkyReflection`** (`include/water.slang`), used by `Water.vk.frag` and `WaterScene.vk.frag` in place of
  `iblSpecular(R, roughness)`: with probes, `sky × occlusion + bounce(R) × (1 − occlusion)`, the occlusion being the
  probes' sky visibility along R blended towards the visibility around N by roughness (the lit surfaces' formula). SSR
  hits still replace it. It samples the probes directly, not through `probeLighting`, whose bent normal reads GTAO at a
  pixel that is the bed's (water is not in the depth prepass).
- **Wet banks are not built.** The splat's fragment stage is at 16 sampled images (ADR 0170's budget) and terrain
  vertices carry no stream a CPU-computed wetness could ride in; it needs a terrain vertex variant with `Custom0` (or a
  free channel in a terrain texture), which is G8a work.
- **`rendering.sceneTextures` (Off/Half) and a dedicated caustics test are not built:** the copy stays full resolution
  per refracting material; caustics are covered by the `water-fall` golden.

## Consequences

- Water under trees reflects their bounce light instead of the sky on SSR misses; existing goldens are unchanged (the
  `forest-mini` and terrain goldens stay within tolerance on both drivers), `water-roof-probes` is new.
- Four or five texel fetches more per water pixel when a volume is bound.

## Revisit when

- Terrain chunks gain a second vertex stream (G8a): wet banks become a `Custom0` value from the water layer and carves.
