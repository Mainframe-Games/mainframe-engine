# ADR 0171 — Volumetric fog is a half-resolution ray march through the sun's cascades, on the post stages

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/vf`, forest slice wave 6)
- **Milestone:** Gameplay toolkit G8e.3 (ADR 0164's plan)
- **Spec:** docs/design/future/forest-visual-quality.md#g8e3-volumetric-light; current state in
  docs/design/post-processing.md#volumetric-fog and docs/design/color-pipeline.md#volumetric-fog-adr-0171

## Context

The Forest's light shafts were screen-space (ADR 0160): a radial blur of the sky around the sun. They exist only while
the sun is on screen, streak from the sun's screen position whatever is in between, and their bloom plus the height
fog's sun scatter made the R1 glade shot milky. ADR 0164 chose a half-resolution ray march through the sun's shadow
cascades (24 steps, temporal accumulation; froxels wait for M11's compute) with Godot's `VolumetricFog*` names, so
shafts fall through gaps in the canopy from any view angle.

## Decision

- **Settings on `WorldEnvironment`, with the fog** (Godot's `Environment.volumetric_fog_*`), not on
  `PostProcessProfile`: `VolumetricFogEnabled` (false), `Density` (0.05), `Albedo` (white), `Emission` (black),
  `EmissionEnergy` (1), `Anisotropy` (0.2), `Length` (64 m), `DetailSpread` (2), `AmbientInject` (0), `SkyAffect` (1),
  `TemporalReprojectionEnabled` (true), `TemporalReprojectionAmount` (0.9), plus `NoiseScale` (8 m) and
  `NoiseStrength` (0). The density takes the height fog's shape (`FogHeight`, `FogHeightDensity`), and turning it on
  changes the analytic fog itself (it starts at the length), so it is part of the world's air, not of the camera's look;
  a shared post profile could not describe that. The render server packs it into `PostEffectSettings.VolumetricFog`
  (main view: `IPostProcessHost.VolumetricFog`; a post-processed `SubViewport`: its world's).
- **One `PostEffect`, three passes, all in `BeforeTonemap`** (`VolumetricFogEffect`, `PostEffectOrder.VolumetricFog` 50,
  before TAA), from the scene pass's own depth. The proposal put the march in `AfterPrepass`; it does not need to be
  there (one graphics queue: nothing overlaps), and in `BeforeTonemap` it needs no prepass, sees what the prepass does not
  draw, and TAA then smooths the fogged image with everything else.
  1. **March** (½ res, `RGBA16F`: in-scatter, transmittance): from the camera to min(depth, length) in 24 steps whose
     ends are `(i/n)^spread` of the ray, each sampled at an interleaved-gradient-noise offset (rotating per frame with
     temporal reprojection, fixed without). Extinction = density × the height profile × a 32³ tiling value-noise volume
     drifting with the wind; in-scatter = albedo × extinction × (sun × visibility × Henyey–Greenstein + sky ambient ×
     ambient inject) + emission × extinction, integrated per step as Hillaire (2015). The pass binds the **view's frame
     set (set 0) and the shadow set (set 1)** through `FrameContext.CreatePipelineLayout`, so it reads exactly what the lit
     shaders read: the sun's colour, the cascades (staggered ones with their cached matrices), the far shadow and the sky
     irradiance (`iblDiffuse`, so G8e.1's SH move needs no change here). Visibility is **one comparison tap** (2 × 2 PCF)
     of the covering cascade or the far shadow, biased towards the light; no PCSS, no blend band.
  2. **Temporal** (½ res, a pooled ping-pong history): the texel's surface point (or the point at the length, for the
     sky) through last frame's view-projection (frame set), the history clipped to the 3 × 3 variance box (γ 1.5, inside
     min/max), blended by the amount. Reset on the first frame, resizes, cuts and views without motion history.
  3. **Composite** (full res): a 4-tap depth-aware upsample (bilinear × depth similarity, closest-depth fallback; sky only
     matches sky), `colour · T + in-scatter` (the sky × `SkyAffect`), alpha kept (TAA's reactive mask), into a pooled
     target and back with `CopyToSceneColor` (one extra full-resolution copy; `SceneColorCopy` stays as it is).
- **The analytic fog starts where the march ends.** `fogColor.a` (0 = fog off) becomes 1 + the start distance:
  `applyFog` and `applySkyFog` integrate from there. `WorldEnvironment.GetFrameEnvironment(volumetricFog)` sets it only
  for a view that runs the march, so other views keep the whole integral. `FrameData` keeps its size.
- **Sun only, no froxels.** The light is the directional light with the cascades (else the first one, unshadowed);
  local lights and correct fog on transparent surfaces wait for M11's compute (froxels). Water writes no depth, so the
  march runs to the stream bed (≤ 2 m of "air" under the surface; accepted).
- **Ambient** is the mean of the sky irradiance up and down × `AmbientInject`, with no occlusion: the air under the
  canopy is as lit by the sky as the glade's until G8e.1's probes give a sky visibility (the hook is `iblDiffuse`).
- **Screen-space light shafts stay** an independent `PostProcessProfile` effect (off by default). The Forest turns them
  off with the volumetric fog on: the march draws the same shafts from the real shadows, and the radial blur on top
  doubled the streaks around the sun and added the milky bloom. (The proposal's Medium tier would keep them instead of the
  march; there is no `rendering.volumetricFogQuality` yet: the march is always ½ res × 24.)

- **The Forest** turns it on: density 0.018, anisotropy 0.75, length 64 m, sky affect 0.2, ambient inject 0.02, noise
  0.5 at 8 m; screen-space shafts off; the height fog's sun scatter 0.3 → 0.1 and its density 0.003 → 0.005 (past the
  march the valley hazes over). Chosen from eleven variants of R1, R1b (60° off the sun), R3 and R4: more density or
  ambient inject brought the milky veil back over the glade's trees; less anisotropy spread the sun's glow; with the
  shafts simply off and no volumetrics the glade was already clear, so the old milkiness was the radial blur's bloom
  and the sun scatter.

## Consequences

- **Cost** (`RenderServer.VolumetricFogGpuMilliseconds`, the Forest benchmark at 1920 × 1080, Apple M5, MoltenVK): 1.27
  and 1.41 ms GPU for the three passes, measured while other lanes' GPU and Docker work ran (the sun shadows read
  3.6–4.6 ms in the same runs against 2.0 ms quiet, so a quiet frame is likely ≈ 0.7 ms; the proposal's budget is 0.9).
  Interleaved runs, old look (shafts, no volumetrics) against the new: p50 19.35 / 16.70 ms against 18.26 / 16.72 ms,
  p99 36.3 / 30.3 ms against 46.5 / 31.4 ms; the frame moves within the machine's noise (the light shafts' three passes
  are gone). 0 B per frame. Memory at 1080p: the march target and the two-target history at ½ res (3 × 4 MB) and the
  full-resolution composite (16.6 MB); the noise volume 128 KB.
- **Stability:** the R1 shot with the wind swaying the canopy changes 2.31 codes per pixel frame to frame with the fog,
  2.54 without (1.98 against 2.72 around the sun), so the shafts add no crawl; the `volumetric-fog` scene's still frames
  differ by < 0.25 once converged. Without temporal reprojection the march's fixed noise shows as a fine dither.
- Defaults unchanged: off, so no existing golden moves (`fog.slang`'s start distance is 0 then). `FrameData` keeps its
  576 bytes (`fogColor.a` carries the start). New public API: the `WorldEnvironment.VolumetricFog*` exports and
  `GetFrameEnvironment`, `RenderServer.VolumetricFogGpuMilliseconds`.
- **For G8e.1:** the march's ambient is `iblDiffuse(±Y)`; once probes exist, multiply it by their sky visibility at the
  step (set 0), so the air under the canopy is darker than the glade's. The march's layout is built from
  `FrameContext.SetLayout`, so new set-0 bindings need no change here (it declares 3 of its own images).
- Known limits: sun only; one shadow tap per step (a faint step at a cascade boundary in thin fog); transparent
  surfaces and water are fogged as the air behind them; off-axis shafts (60° from the sun) stay subtle with one HG lobe;
  no `rendering.volumetricFogQuality` (Medium's ¼ res × 12) yet; froxels for local lights wait for M11.
