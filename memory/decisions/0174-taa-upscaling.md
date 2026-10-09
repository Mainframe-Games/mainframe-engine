# ADR 0174 — TAA upscaling: render and output resolution, TAAU, FSR 1 and the mip bias

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/taau`, forest slice wave 7)
- **Milestone:** Gameplay toolkit G8e.8 (the forest's "Unreal look", ADR 0164)
- **Spec:** docs/design/future/forest-visual-quality.md (G8e.8); current state in docs/design/post-processing.md (Render
  and output resolution, TAA), forest.md (Performance), project-and-gamehost.md (rendering settings)

## Context

The G8e budget assumes the Forest renders its 3D scene at 0.75 scale (1920 × 1080 for a 2560 × 1440 window) and upscales
it. Every pass of the frame ran at the swapchain's size. ADR 0166's TAA resolve was written in input pixels with an
output/input ratio so TAAU would "only change the input size and the weights". DLSS, XeSS and MetalFX are closed or
unreachable through MoltenVK; FSR 2/3 are compute (after M11); FSR 1 is MIT and fragment-shader friendly.

## Decision

- **Settings under Godot's names:** `rendering.scaling3DMode` (`Bilinear` default, `Fsr`, `Taau`),
  `rendering.scaling3DScale` (0.25–1, default 1), `rendering.fsrSharpness` (0–2 stops, default 0.2) →
  `IVulkanContext.Scaling3DMode`/`Scaling3DScale`/`FsrSharpness`, applied by `GameHost`. Optional keys, so no project
  migration. Below a scale of 1 only; no supersampling. Public helpers: `RenderScaling`, `Scaling3DMode`.
- **Two resolutions for the main view.** `IVulkanContext.RenderExtent` (swapchain × scale, halves rounded up) sizes the
  scene target, so the scene pass, the depth prepass and velocity, SSAO and contact shadows, the water scene copy and
  SSR, volumetric fog and depth of field all run there (`FrameContext.Extent` and `frame.viewport` of view 0 follow).
  An output-size `scene output` colour exists only while upscaling; auto exposure, glow, light shafts, the tonemap, LUT,
  film effects and every `AfterTonemap` effect run at the output size, and the 2D canvas, gizmos and UI are never scaled.
  Sub-viewports (the editor's views) and the picking pass stay at their own size.
- **The split is by stage and order, not per effect.** `PostEffectContext` gets a render pool and an output pool; before
  each call the stack points `Targets`, `Scene.Color` and `Scene.Extent` at the effect's resolution
  (`RunsAtOutputResolution`: `BeforeTonemap` effects after `PostEffectOrder.Taa`, all `AfterTonemap` ones). Existing
  effects needed no change beyond two shaders that read render-size depth or velocity from an output pass (light shafts'
  mask, the velocity view). **Depth of field moves before TAA** (`DepthOfField` 75): it reads the scene depth texel for
  texel, belongs at render resolution, and G8e.4's order already put it there.
- **TAAU in lane T's resolve** (no second resolve): the history lives in the output pool, the result is copied into the
  output colour (`CopyToOutputColor`), and with `TaaParams` flag 4 the reconstruction uses a narrower filter
  (`UpscaleFilterFalloff`: 0.75 / ratio² output pixels, at least 0.4, weights relative to the nearest sample) and the
  clip box is the neighbourhood's min/max (a sparser frame misses thin lines more often; the variance box clipped them out
  of the history). Catmull–Rom reprojection, YCoCg clipping, the depth test and the reactive water mask are unchanged.
  `Taau` turns TAA on while upscaling; with TAA on, the resolve always upscales.
- **Halton length** 8 / scale², rounded up to a power of two (`RenderScaling.JitterSampleCount`: 16 at 0.75), jitter in
  render pixels.
- **FSR 1 as the spatial mode** (TAA off): `SpatialUpscaleEffect` at `PostEffectOrder.Upscale` (110) runs EASU
  (`Post/Upscale.vk.frag`, FsrEasuF re-implemented in Slang with loads instead of gathers, on a reversible tonemap of the
  HDR colour), then `TaaSharpenEffect` (already RCAS since ADR 0166) runs with `exp2(−fsrSharpness)`. `Bilinear` is one
  tap. The same effect is the fallback on a TAAU frame the resolve skipped. Godot's FSR 1 runs after its tonemap; ours runs
  before so glow and exposure see the output resolution, as the spec's budget assumes.
- **Mip bias:** `FrameData` grows to 688 bytes with `mipBias` (log2 scale) and `mipScale` (its exp2) for view 0 while
  upscaling; `materialGrad` (a select: untouched at native) scales the UV gradients of every material `SampleGrad` (mesh,
  foliage, terrain splat, water) and falls and spray use `SampleBias`. 0 / 1 everywhere else. The prepass alpha test
  stays unbiased: reading the frame block in `MeshDepth` moved 0.6 % of `tree-forest`'s leaf-edge pixels at native
  resolution on MoltenVK, so upscaled cutout coverage comes from the render resolution's mip.
- **Scale changes** apply at the next frame start like a resize: device idle, targets resized through the deletion queue,
  sets rewritten in `OnResize`, histories reset.
- **The Forest** renders at 0.75 with TAAU; `++ --scale 1` is native; the benchmark reports both resolutions.

## Measurements (Apple M5, MoltenVK)

- Against a 4× supersampled frame at 0.75: thin bars and sub-pixel spokes TAAU 29.9 dB vs native TAA 33.1 dB and no AA
  22.6 dB (after 16 frames 28.4 vs 31.5); swaying dithered leaves TAAU 27.1 dB vs native TAA 28.8 dB. EASU 23.2 dB vs
  bilinear 22.5 dB on the edges. The spec's "within 1.5 dB of native" holds for neither scene exactly (−3.2 dB on
  sub-pixel spokes, −1.6 dB on leaves); the render tests assert 4 dB and 2 dB. Tried and dropped: a confidence weight on
  the current frame (−1 to −3 dB), a filter grown with the input spacing (−1 dB), trusting still pixels' history
  (+0.15 dB; ghosting risk).
- `just forest-bench` (no other GPU process): window 2560 × 1308 (the display's limit for "1440p"), p50 19.9 / 19.3 ms at
  0.75 vs 26.3 / 26.5 ms native, p99 32.6 vs 41.5 ms; 1920 × 1080, p50 15.3 vs 18.2 ms, p99 23.9 vs 32.5 ms. With G8e.5's
  foliage merged: 2560 × 1308 p50 21.0 vs 28.0 ms, p99 32.5 vs 40.9 ms; 1920 × 1080 p50 16.9 / 16.7 vs 20.9 / 20.6 ms.
  0 B per frame.

## Consequences

- The R1–R5 reference shots and anything rendered by the Forest now go through TAAU; they are regenerated after the wave
  merges.
- The G8e target (p50 ≤ 14 ms at 2560 × 1440) is not met on a base M5; it was set for an M-series Pro.
- Output effects that read depth or velocity must map their texel to the render size (the two built-ins do).
- `forest-mini` with TAAU and a quiet-machine baseline on both machines remain for the final G8e pass.
- Dynamic resolution (open question 12) would reuse `ApplyRenderScale`, but each change costs a device wait today.
