# Plan: Volumetric fog quality tier (G8e.3 rest)

**Status:** done, see [log](../logs/2026-11-07-volumetric-fog-quality.md)
**Docs:** [Post-processing → Volumetric fog](../../docs/design/post-processing.md#volumetric-fog), [ADR 0171](../decisions/0171-volumetric-fog.md)
("no `rendering.volumetricFogQuality` (Medium's ¼ res × 12) yet"), ADR 0164's budget
**Started:** 2026-11-07

## Goal

A project can choose a cheaper volumetric fog march: `rendering.volumetricFogQuality = Medium` marches 12 steps at quarter
resolution; High (the default) stays exactly as it is.

## Not in scope

- A Low tier, froxels, mapping the Forest's quality presets to the setting.

## Steps

- [x] `VolumetricFogQuality`, the project setting (read, write, editor row), `RenderServer.VolumetricFogQuality`, GameHost
      and the editor view applying it. Verified by: the setting's round-trip unit test.
- [x] `VolumetricFogEffect` tiers (targets and sets per tier, made on first use), steps and scale per tier; the shaders
      take the scale. Verified by: unit tests of the push blocks and descs; `volumetric-fog` render tests (High golden
      unchanged; Medium run with the beam checks and a golden on both drivers).
- [x] Docs (post-processing.md, project-and-gamehost.md), ADR 0276, log. Verified by: check.sh, state-guard.

## Done when

- Gates pass; the High golden is unchanged; the Medium golden shows the beam.

## Open questions

- None.
