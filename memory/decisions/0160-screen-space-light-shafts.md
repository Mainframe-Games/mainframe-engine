# ADR 0160 — Screen-space light shafts

- **Date:** 2026-10-08
- **Status:** accepted (implemented on `forest/i`, forest slice wave 2)
- **Milestone:** Gameplay toolkit G8d (G8d.6)
- **Spec:** docs/design/future/forest-showcase.md (G8d.6); current state in docs/design/color-pipeline.md and
  vulkan-renderer.md

## Context

The forest showcase ([ADR 0149](0149-terrain-trees-water-engine-features.md)) wants god rays through the canopy. The
renderer has no depth prepass and no `SceneTextures` yet (G6.6/G8c), no compute (M11), and the scene pass discarded its
depth (store op DontCare). The post chain already has auto exposure, glow and FXAA as raster passes
([ADR 0124](0124-godot-tonemap-and-glow.md), [ADR 0154](0154-physical-sky-fxaa-auto-exposure.md)).

## Decision

- **The scene pass stores its depth, every frame** (`RenderTargetDesc.SampleDepth`, final layout
  `DEPTH_STENCIL_READ_ONLY_OPTIMAL`, covered by `RenderTarget.End`'s barrier). Measured with timestamps around the scene
  pass at 2560 × 1440 on an Apple M5 (MoltenVK), store vs DontCare over 600-frame averages: +0.017 ms on `pbr`
  (0.93 → 0.95 ms) and within noise on `terrain` (2.46 ms) and `multimesh` (0.87 ms). That is cheap enough on a
  tile-based GPU to keep it unconditional, which avoids a second, compatible render pass switched by the setting and
  leaves the depth ready for later passes. Light shafts need no prepass.
- **Settings** on `WorldEnvironment` and `PostProcessSettings` (group "Light Shafts"): `LightShaftsEnabled` (false),
  `LightShaftsIntensity` (1), `LightShaftsDecay` (0.96), `LightShaftsDensity` (0.8), `LightShaftsSamples` (16, clamped
  to 4–64 per pass). Turning them on makes the settings non-default, so the post tonemap pass runs.
- **The sun is the world's first `DirectionalLight3D`**, as for the physical sky. The render server projects its
  direction (at infinity) with the root camera each frame into `IVulkanContext.LightShaftsSun`
  (`LightShaftsSun.Compute`: scene-image UV and a fade).
- **Three half-resolution fragment passes** (`LightShafts`, `Post/LightShaftsMask` and `Post/LightShaftsBlur`), after
  auto exposure and glow:
  1. **Mask:** the mean of each texel's 2 × 2 scene texels whose depth is still the cleared 1 (sky), each capped at
     luminance 8, × a squared smoothstep falloff 0.6 screen heights around the sun. The mask is the sky's own colour
     near the sun, so its tint follows the sky and needs no separate sun colour.
  2. **Fine and coarse radial blur** (GPU Gems 3 ch. 13): `n` taps each, steps `density / n²` and `density / n`,
     per-tap decays `decay^(16/n²)` and `decay^(16/n)`, so a ray gets `n²` effective taps (256 by default) for 32 per
     texel. `LightShaftsDecay` is the weight left after each sixteenth of a ray, independent of the sample count.
     Samples past the edges read black (clamp-to-border).
- **Composite alongside glow:** `TonemapPost` adds the bilinear result × intensity × fade to the HDR scene before
  exposure. Glow and auto exposure read the scene without shafts.
- **Fade:** 1 on screen, `1 − smoothstep(0, 0.3, outside)` past an edge, 0 behind the camera, at 90° or with an
  orthographic camera. With fade 0 or the shafts off no shaft pass is recorded.
- Resources are created the first frame shafts are enabled. The post set binds the glow's smallest level as a
  placeholder at the new binding 3, and a second post set binds the shafts, so no descriptor set in flight is rewritten.
- Default off: existing scenes and goldens are unchanged. Per frame: 0 B managed, validation clean.

## Alternatives considered

- **Store the depth only while shafts are on:** the same pixels for ≤ 0.02 ms, at the cost of a second render pass
  and a per-frame switch. Revisit with the mobile tiers (M12/M13) if their bandwidth says so.
- **Add the shafts into the scene target before glow** (a load pass blending at full resolution): the shafts would bloom
  and move auto exposure, and it costs a full-resolution pass. Adding them in the tonemap is free.
- **An angular falloff around the sun** (per-pixel view rays from the inverse projection): equivalent on screen for
  the usual fields of view; the screen-space distance needs no camera data in the shader.
- **One pass of 256 taps** (or three passes): one pass is 8× the texture reads; three passes add an encoder on Apple
  GPUs for little gain (the proposal's reasoning).
- **Froxel volumetric fog** (forest-showcase decision 2): needs compute (M11).

## Consequences

- `IVulkanContext` gains `LightShaftsSun`; the post tonemap set gains binding 3 and its push block a `shafts` scale.
- The scene target's depth is sampleable after the scene pass; later passes (water, contact shadows before the
  prepass exists) can bind `SceneTarget.Depth` in `DEPTH_STENCIL_READ_ONLY_OPTIMAL` after `BeginOverlayPass` ends it.
- SubViewports (the editor viewport) draw no shafts, like glow and auto exposure.
- Open: `rendering.lightShaftsQuality` (Low = the coarse pass only), tinting by the fog's sun in-scattering,
  `ColorGradingLut` (G8d.4's other half) — not built.
