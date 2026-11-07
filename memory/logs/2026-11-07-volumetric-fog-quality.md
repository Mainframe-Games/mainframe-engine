---
summary: >-
  G8e.3 rest: rendering.volumetricFogQuality (High: half resolution, 24 steps, the default and unchanged; Medium: quarter
  resolution, 12 steps), with per-tier targets in VolumetricFogEffect and the fog-texel scale as a push constant (ADR 0276).
---

# 2026-11-07: Volumetric fog quality tier (G8e.3 rest)

Branch `feature/volumetric-fog-quality`, cut from `origin/main` (da67613f). Plan: [Volumetric fog quality](../plans/2026-11-07-volumetric-fog-quality.md).
As built: [Post-processing → Volumetric fog](../../docs/design/post-processing.md#volumetric-fog).

## What changed

- **Engine:** `VolumetricFogQuality` (High = 0, Medium); `VolumetricFogSettings.Quality`; `RenderServer.VolumetricFogQuality`
  (main view and sub-viewports); `rendering.volumetricFogQuality` in `ProjectSettings` (read, written when not High),
  applied by `GameHost`; `VolumetricFogEffect`: `MediumSteps`, `ScaleFor`, `StepsFor`, `MarchMediumDesc`/`HistoryMediumDesc`
  (quarter), a `Tier` per quality made on first use, the pool sized for both.
- **Shaders:** `include/volumetric_fog.slang` `volumetricPixel(texel, size, scale)`; the march (noise.z), temporal
  (scene.w) and composite (params.w) take the scale; `.spv` and `shaders.lock` refreshed for these three only.
- **Editor:** Project Settings › Rendering › Volumetric Fog Quality; the 3D view follows it.
- **Tests:** `VolumetricFogTests` (unit: tiers, scale, the setting's round trip), `ProjectWorkflowTests` (the new Project Settings row); render
  `AtMediumQualityTheBeamStillStandsOut` with golden `volumetric-fog-medium_frame0030.png` on moltenvk and lavapipe; the
  High golden is unchanged.

## Decisions

- [ADR 0276](../decisions/0276-volumetric-fog-quality-tier.md).

## Dead ends

- The first Medium run lost the beam and smeared streaks to the right: the composite's upsample and `volumetricPixel`
  assumed half resolution (× 2). The scale became a push constant.
- `just shaders` rewrote four unrelated `.spv` files (DebugLines, Gizmo, GridAnalytic, SceneGrid) with the local slangc;
  they and their lock lines were restored.

## Verified

- The Medium golden inspected beside High: the same slanted beam, softer and blockier at its edges, a faint step band.

## Not done

- The Forest's quality presets still leave the fog at High; no Low tier.

## State changes

- milestones G8e: in the status cell, after "G8e.3 volumetric fog (0171)", add "with a Medium quality tier
  ([ADR 0276](../decisions/0276-volumetric-fog-quality-tier.md))".
