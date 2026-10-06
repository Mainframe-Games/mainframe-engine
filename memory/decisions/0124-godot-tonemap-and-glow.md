# ADR 0124 — Godot 4.7's tonemap and glow on WorldEnvironment

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM1 / E4 (the game's `docs/porting.md`)

## Context

The Driving Range world uses Godot's `Environment` with ACES tonemapping and glow (`glow_normalized`,
`glow_strength = 0.75`, every other glow property at Godot 4.7's defaults). The engine had neither: no bloom (ADR 0006),
and its ACES is Stephen Hill's fit scaled by the project exposure (1.3), while Godot 4.7's ACES is the same fit with a 1.8
input bias and its output divided by the curve at the white point (≈ 0.781). Godot maps mid-grey 0.18 to 0.300, the
engine to 0.154: the ported world rendered far darker and more saturated than in Godot, glow or not.

Godot 4.7 Forward+ glow (`copy_effects.cpp` `gaussian_glow`, `copy.glsl`, `tonemap.glsl`): level k (0..6) is the scene
at `1 / 2^(k+1)` resolution, each built from the previous level by a 2×2-box downsample and a separable 9-tap gaussian;
every level is multiplied by `glow_strength`; the first level tames fireflies, applies the exposure, the
`smoothstep(threshold, threshold + scale, max(rgb))` feedback (or `glow_bloom`) and the luminance cap. The tonemap pass
gathers the levels with bicubic B-spline sampling, weighted by `glow_levels` (divided by their sum when normalized),
times `glow_intensity`, and blends them after exposure and before the curve (Screen by default; soft light after the
curve).

## Decisions

1. **`PostProcessSettings`** (a record struct) carries Godot's tonemap (`Tonemapper`: `Engine` or `GodotAces`,
   `TonemapExposure`, `TonemapWhite`) and glow settings with Godot 4.7's defaults; **`WorldEnvironment` exports them**
   (Tonemap and Glow groups, `glow_levels/1..7` as `GlowLevel1..7`). The render server copies the tree's root world's
   settings to `IVulkanContext.PostProcess` each frame.
2. **Opt-in**: `PostProcessSettings.Default` (engine curve, no glow) keeps the existing tonemap pass (`Tonemap.vk.frag`)
   and every golden. Anything else uses a second pass, `TonemapPost.vk.frag`: exposure (`TonemapExposure` for Godot's
   curve, the project exposure for the engine's), glow, curve, soft-light glow, sRGB encode, ported from Godot's
   `tonemap.glsl` (MIT).
3. **Glow as raster passes** (`GlowEffect`, `GlowBlur.vk.frag`), not compute: per level a horizontal pass (downsample at
   destination pixel centres, clamped to the edge, + horizontal blur, into a temp target) and a vertical pass (vertical
   blur + Godot's per-level and first-level steps, into the level target), with Godot's kernel. Each level is its own
   `RenderTarget` (barriers, resize and deletion as for the scene target). Only levels up to the highest weight above
   0.01 are drawn (Godot's `max_glow_index`); all seven are cleared once after (re)creation so the tonemap can bind them.
4. **Bicubic gather** as Godot's desktop default (`rendering/environment/glow/upscale_mode`).

## Consequences

- Supersedes ADR 0006's "no bloom".
- Render test `GlowTests.GlowBleedsAroundBrightAreasOnlyAboveTheThreshold` (scene `glow`): glow brightens the pixels next to
  an HDR emitter, leaves a far corner unchanged and adds nothing below the threshold; golden `glow_frame0006` (moltenvk).
  Unit tests: `PostProcessSettingsTests` (defaults, ordinals, normalisation, max level, white, level sizes, scene round trip).
- Known issues: SubViewports still tonemap with `Tonemap.vk.frag` (engine curve, no glow); no glow map, no
  auto-exposure; Godot's other tonemappers (Linear, Reinhard, Filmic, AgX) are not ported.
