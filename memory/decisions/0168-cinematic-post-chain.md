# ADR 0168 — The cinematic post chain: tonemappers, colour grading, HQ glow, bokeh DoF, film effects

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/cp`, forest slice wave 5)
- **Milestone:** Gameplay toolkit G8e (G8e.4)
- **Spec:** docs/design/future/forest-visual-quality.md (G8e.4, ADR 0164); current state in docs/design/color-pipeline.md
  and post-processing.md

## Context

The Forest should look "made in Unreal". Its post chain was a plain one: the engine's ACES fit or Godot 4.7's ACES
([ADR 0124](0124-godot-tonemap-and-glow.md)), Godot's glow (no anti-firefly first level), auto exposure, light shafts
and FXAA; no LUT, depth of field, vignette or grain. ADR 0163 gave the frame post stages (`AfterPrepass`,
`BeforeTonemap`, `AfterTonemap`), a target pool and `CopyToSceneColor`. The engine had no 3D texture, which the LUT
(and G8e.1's probes) need.

## Decision

- **`Texture3D`** (resource, `Rgba8` or `Rgba16F`, trilinear or nearest, clamp, no mips) and `Texture3DGpu`; `GpuImage`
  gains `GpuImageDesc.Depth` (a `VK_IMAGE_TYPE_3D` image when the view type is `3D`), `UploadQueue.UploadImage` copies
  every slice, and `GpuTexture.Create3D` takes any uncompressed format. Generic so G8e.1's probe volume reuses it.
- **`.cube` LUTs:** `CubeLut` parses Adobe/Resolve `.cube` (`TITLE`, `LUT_3D_SIZE` 2–256, `LUT_1D_SIZE`, `DOMAIN_MIN/MAX`,
  `LUT_*_INPUT_RANGE`, comments, CRLF, vendor keywords ignored; errors name the file and line), samples like the GPU
  (trilinear, domain-clamped), writes `.cube`, and converts to an `Rgba16F` `Texture3D` (a unit-domain 3D table entry
  for entry; a 1D table or another domain resampled into 33³). `CubeLutImporter` registers `.cube` with
  `ResourceLoader`, so scenes refer to a LUT by path.
- **Tonemappers:** `Tonemapper` gains `Linear`, `Reinhard`, `Filmic`, `Agx` after `Engine` and `GodotAces` (ordinals
  kept), ported from Godot 4.4's `tonemap.glsl` (MIT) into `TonemapPost`; `TonemapCurves` is the C# mirror. The Godot
  curves use `TonemapExposure` (`PostProcessSettings.ExposureFor`); Reinhard and filmic use `max(1, TonemapWhite)`, AgX
  none (Godot 4.4).
- **Colour grading** on `WorldEnvironment`/`PostProcessSettings`, Godot's names: `AdjustmentEnabled`,
  `AdjustmentBrightness/Contrast/Saturation` (Godot 4's `apply_bcs` on the display value),
  `AdjustmentColorCorrection` (a `Texture3D`, texel-centre mapped), plus the engine's
  `AdjustmentColorCorrectionStrength`.
- **Glow quality:** `GlowQuality { Standard, High }`. Standard is Godot's chain, untouched. High reuses the same images:
  Jimenez's 13-tap downsample per level (Karis-averaged boxes, exposure, threshold and cap on level 0) into `temp[k]`,
  then a 3 × 3 tent upsample (taps 2 lower-level texels apart, so each level spreads about as wide as Godot's Gaussian)
  adding `weight_k · temp[k]` on the way up into `level[k]`; the tonemap reads `level[0]` at weight 1.
- **Lens: `CameraAttributesPractical`** (resource, Godot's DoF names and defaults: `DofBlurFar/NearEnabled`,
  `…Distance` 10/2, `…Transition` 5/1, `DofBlurAmount` 0.1; engine `DofQuality` 16/32 taps; engine film effects
  `VignetteIntensity` 0, `VignetteRoundness` 1, `FilmGrainIntensity` 0, `FilmGrainSize` 1.5, `ChromaticAberrationIntensity`
  0) on `WorldEnvironment.CameraAttributes` or `Camera3D.Attributes` (the root view's current camera wins; the whole lens
  replaces the environment's). `ApplyTo` copies it into the frame's `PostProcessSettings`.
- **`DepthOfFieldEffect`** (`BeforeTonemap`, order 150, after TAA, no prepass needed): half-res prefilter (signed CoC
  of the nearest depth when it is in the near field, else the farthest; colour from the texels that agree), half-res
  golden-angle scatter-as-gather (Gustafsson; near and far in one pass), full-res composite by the pixel's own CoC, then
  `CopyToSceneColor`. Max radius `amount × 64` px at 1080 lines, scaled with height.
- **`ColorGradeEffect`** (`AfterTonemap`, order 150 = after FXAA, so grain is not smoothed): one pass with chromatic
  aberration, adjustments, the LUT, the vignette (in linear light) and value-noise grain seeded by the frame number
  (deterministic under `--fixed-fps`). The LUT's sets are a ring of `MaxFramesInFlight + 1`, so a LUT change never
  rewrites a set in flight.
- Everything is **off by default**: `PostProcessSettings.Default`, a default lens, and existing goldens are unchanged.
- **The Forest** gets `ForestGrade`: a generated 33³ forest-morning LUT (own work, CC0, `--write-scenes`), vignette 0.2,
  grain 0.015, no aberration; R4 and R5 are photo shots with subtle 32-tap DoF (`ReferenceShot.BlurNearUntil/BlurFarFrom`),
  never while walking (proposal open question 7). The tonemapper stays the engine's ACES (G8e.7 picks ACES or AgX).

**Measured** (the Forest, 1920 × 1080, Apple M5, MoltenVK timestamps): the grade pass ≈ 0.16 ms (`AfterTonemap` 0.37–0.39 ms
with it, 0.21–0.22 ms with FXAA alone), within the proposal's 0.35 ms; R5's 32-tap DoF below the timestamps' resolution;
`just forest-bench` p50 13.85 ms with the grade vs 13.84 / 14.39 ms without (other lanes running), 0 B per frame.

## Alternatives considered

- **Grade inside the tonemap pass** (the proposal's "no extra pass"): saves one LDR pass (≈ 0.05 ms at 1080p) but
  would put grain under FXAA and grow the post set and push block further; an `AfterTonemap` effect follows ADR 0163's
  model and leaves the tonemap pass to the curves.
- **Separate near and far DoF layers** (the proposal): a second half-res target (or MRT) per layer and a three-way
  composite. The single gather handles the subtle photo look; a near field over high-frequency backgrounds shows
  half-resolution sparkle at its silhouette (documented). Revisit with TAA (lane T), which also cleans gather noise.
- **`GlowMap` (lens dirt):** not built; it needs another binding in the post set. Left for G8e.7.
- **Godot's 3D LUT lookup with raw UVW:** off by half a texel at the ends; we map onto texel centres, so an identity LUT
  reproduces the input within ±1.

## Consequences

- New public API: `Texture3D`, `Texture3DFormat`, `CubeLut`, `CubeLutImporter`, `CameraAttributesPractical`,
  `GlowQuality`, `DepthOfFieldQuality`, `TonemapCurves`, `GpuTexture.Create3D`, `GpuImageDesc.Depth`; new
  `PostProcessSettings`/`WorldEnvironment` properties; `Camera3D.Attributes`; `PostEffectOrder.ColorGrade` (150).
- Tests: `post-grade` (identity LUT ±1, a channel-rotation LUT and strength 0.5 exactly, Godot's adjustments, every
  tonemapper against `TonemapCurves` ±2), `post-dof` (the far wall blurs and the subject does not; near blur), `post-film`
  (vignette against its formula, grain statistics, temporal and deterministic, aberration fringes), glow High, a 0 B
  allocation gate and a resize with the whole chain on; unit tests for the parser, `Texture3D`, the curves and settings.
- Open: lens dirt, separate DoF layers, a photo mode toggling the lens at run time, AgX for the Forest (G8e.7).
