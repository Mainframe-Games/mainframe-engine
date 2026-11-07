# Plan: Water reflections under probes (G8e.6 rest)

**Status:** done, see [log](../logs/2026-11-07-water-probe-reflections.md)
**Docs:** [Water](../../docs/design/water.md), [ADR 0173](../decisions/0173-water-scene-copy-refraction-ssr-falls.md),
[ADR 0170](../decisions/0170-light-probe-volume.md)
**Started:** 2026-11-07

## Goal

Water's sky reflection (every SSR miss, and water without SSR) is occluded by the light probes, so water under a canopy
or a roof stops mirroring the bright sky.

## Not in scope

- Wet banks, `rendering.sceneTextures`, a caustics-only test (ADR 0279 says why).

## Steps

- [x] `waterSkyReflection` in `water.slang`, used by both water fragment shaders. Verified by: water, probe, terrain
      and `forest-mini` goldens unchanged on both drivers.
- [x] `water-ssr --count 3/4` (a roof, probes or none, SSR off) and `SkyReflectionsUnderARoofTakeTheProbesOcclusion`
      with goldens on both drivers. Verified by: ratio < 0.7 with probes, > 0.85 without.
- [x] Docs (water.md), ADR 0279, log. Verified by: check.sh, state-guard.

## Done when

- Gates pass and the roof test passes on both drivers.

## Open questions

- None.
