# ADR 0276 — A volumetric fog quality setting: High (½ res, 24 steps) and Medium (¼ res, 12 steps)

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8e (G8e.3 rest)
- **Docs:** docs/design/post-processing.md#volumetric-fog, docs/design/project-and-gamehost.md,
  memory/plans/2026-11-07-volumetric-fog-quality.md

## Context

ADR 0164's budget gives the volumetric march a Medium tier ("¼ res and 12 steps") for base M-series machines, and ADR
0171 shipped only the High march (half resolution, 24 steps) with "no `rendering.volumetricFogQuality` yet". The march's
step count was already a push constant, and the post target pool already had a `Quarter` scale, but the shaders mapped
a fog texel to scene pixels with a hard-coded factor of 2.

## Decision

- **`VolumetricFogQuality { High, Medium }`**, `rendering.volumetricFogQuality` → `RenderServer.VolumetricFogQuality`
  (every view; the editor's view follows the open project, like `waterSsr`). High is the enum's zero, so a default
  `VolumetricFogSettings` is High and nothing that does not ask for Medium changes (every golden but the new one stays).
- **Medium = quarter-resolution march and history targets and 12 steps.** The three passes take the scene-pixels-per-fog-
  texel scale (2 or 4) in an unused push-constant slot (`volumetricPixel(texel, size, scale)`; the composite's
  depth-aware upsample divides by it).
- **One target set per tier, made the first time it is used** (march, history pair, six descriptor sets), so switching
  tiers in the settings menu never reallocates the other tier's and starts a fresh history; the pool holds both tiers'
  sets.
- **No Low tier.** Below Medium a game turns the fog off (the Forest's Low preset does); the screen-space light shafts
  (ADR 0160) are the cheap alternative.

## Ruled out

- **Fewer steps at half resolution:** the march's cost is per texel × steps; a quarter of the texels saves more than
  half the steps, and the composite's depth-aware upsample already hides the lower resolution on surfaces.
- **Recreating the effect on a switch:** the post stack has no recreate path, and a device-idle wait on a menu change
  would stall.

## Consequences

- Medium's shafts are softer at their edges and show more step banding without TAA (golden
  `volumetric-fog-medium_frame0030`).
- The Forest keeps High; its quality presets do not map to this setting yet.

## Revisit when

- Froxel fog arrives with M11's compute: the tiers become froxel grid sizes.
