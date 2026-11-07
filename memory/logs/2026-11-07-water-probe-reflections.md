---
summary: >-
  G8e.6 rest: water's sky reflection takes the light probes' specular occlusion (waterSkyReflection, both water shaders),
  so SSR misses under a canopy show the probes' bounce instead of the bright sky; wet banks, rendering.sceneTextures and a
  caustics-only test deferred (ADR 0279).
---

# 2026-11-07: Water reflections under probes (G8e.6 rest)

Branch `feature/water-probe-reflections`, cut from `origin/main`. Plan: [Water probe reflections](../plans/2026-11-07-water-probe-reflections.md).
As built: [Water → Reflections under probes](../../docs/design/water.md#reflections-under-probes).

## What changed

- **Shaders:** `include/water.slang` `waterSkyReflection`; `Water.vk.frag` and `WaterScene.vk.frag` use it (their `.spv`
  and lock lines; `Waterfall.vk.frag`'s lock line follows the include).
- **Tests:** `water-ssr --count 3` (a roof over half the pool, a baked volume) and `--count 4` (no volume) with
  self-checks; `SkyReflectionsUnderARoofTakeTheProbesOcclusion`, golden `water-roof-probes_frame0030.png` on moltenvk
  and lavapipe.
- **Docs:** water.md (Reflections under probes, Not yet).

## Decisions

- [ADR 0279](../decisions/0279-water-sky-reflection-under-probes.md).

## Dead ends

- `just shaders` rewrote the four unrelated `.spv` files again; restored.

## Verified

- Under the roof the water reflects 0.362 of the open water's light with probes, 1.000 without. The golden inspected:
  the covered half dark blue-black, the open half sky blue, the pillar unchanged.
- `forest-mini` (water and probes), terrain, probe and water goldens unchanged on both drivers.

## Not done

- Wet banks (needs a terrain vertex stream or a free texture channel), `rendering.sceneTextures`, a caustics-only test.

## State changes

- milestones G8e: in the status cell, after "G8e.6 water SSR/refraction/falls (0173)", add "with probe-occluded sky
  reflections ([ADR 0279](../decisions/0279-water-sky-reflection-under-probes.md)); wet banks not built".
