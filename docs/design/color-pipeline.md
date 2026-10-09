# Color Pipeline

## Purpose

How colour flows from authored values to the display: linear-space lighting in an HDR offscreen
target, an exposure + ACES tonemap pass, sRGB encoding for the swapchain, and the UI composited after
tonemapping so it looks exactly as authored. Implemented in
[`VulkanRenderer.Presentation.cs`](../../MainframeEngine/Src/Rendering/Vulkan/VulkanRenderer.Presentation.cs),
[`RenderTarget`](../../MainframeEngine/Src/Rendering/Vulkan/RenderTarget.cs),
[`ColorSpace`](../../MainframeEngine/Src/Rendering/ColorSpace.cs) and
[`Post/Tonemap.vk.frag`](../../MainframeEngine/Content/Shaders/Post/Tonemap.vk.frag). Decisions:
[ADR 0006](../../memory/decisions/0006-color-pipeline-aces-and-unorm-swapchain.md).

Before M3 the swapchain was UNORM, lighting ran on gamma-encoded values (overlapping lights clipped to
white), sky textures were sRGB and came out darker, and Spine's premultiplied alpha was applied twice.

## Frame

```mermaid
flowchart LR
    S["Scene pass<br/>RenderTarget: R16G16B16A16_SFLOAT + depth<br/>sky, grid, shapes, Spine (linear)"] --> T["Tonemap pass<br/>texel × exposure → ACES fitted → sRGB encode"]
    S -. "PostProcessSettings ≠ Default (ADR 0124)" .-> AE["Auto exposure (ADR 0154)<br/>log2 luminance 64² → 1², adapted 1×1"] -.-> G["Glow chain<br/>7 levels, ½ … 1/128 res"] -.-> T2["Post tonemap pass<br/>× exposure (× auto) → glow → engine or Godot ACES → sRGB encode"]
    T -. "AntiAliasing.Fxaa (ADR 0154)" .-> F["FXAA input (R8G8B8A8_UNORM, sRGB-encoded)<br/>→ FXAA 3.11 into the swapchain"] -.-> O
    T --> O["Overlay (same pass)<br/>canvas, screen gizmos, UI, dev overlay: sRGB-authored, unchanged"]
    O --> P["present (B8G8R8A8_UNORM)"]
```

| Step | Who | Detail |
|---|---|---|
| `BeginRenderPass()` | Engine, after the shadow pass | Begins the scene target pass; clears colour to `SetClearColor` **converted to linear**, depth to 1 |
| scene draws | `RenderServer.RenderMain` (the scene tree), then game `OnRenderMainPass` | Pipelines built against `IVulkanContext.RenderPass` (the scene pass) write linear HDR colour |
| `BeginOverlayPass()` | `EndFrame` | Ends the scene pass; begins the swapchain pass; draws the fullscreen tonemap triangle; leaves the pass open for the overlay |
| overlay draws | canvas, `ScreenGizmos`, UI layers, dev overlay (`OverlayOrder`) | Pipelines built against `IVulkanContext.OverlayRenderPass` |
| `EndFrame()` | Engine | Runs whatever step is missing (a frame always ends tonemapped and presentable), ends the pass, optional frame capture |

## Formats and colour spaces

| Asset | Format / space | Converted where |
|---|---|---|
| Scene target | `R16G16B16A16_SFLOAT`, linear Rec.709 | — |
| Swapchain | `B8G8R8A8_UNORM` (or `R8G8B8A8_UNORM`), sRGB-encoded by the tonemap shader | see [Swapchain](#swapchain) |
| Albedo, sky, straight-alpha Spine atlas | `R8G8B8A8_SRGB` (`TextureColorSpace.Srgb`) | sampler decodes |
| Data textures (normals, masks, font coverage) | `R8G8B8A8_UNORM` (`TextureColorSpace.Linear`) | — |
| Premultiplied Spine atlas (`pma: true`) | `R8G8B8A8_UNORM` | `SpineLit.vk.frag`: un-premultiply → decode → re-premultiply |
| Light colour, ambient | authored sRGB (`Light.Color`, `LightEnvironment.AmbientColor`) | once, when set (`Light.LinearColor`) |
| Shape colour (`System.Drawing.Color`), grid line colour, Spine slot tint, sky gradient/sun colour | authored sRGB | in the shader (`srgbToLinear`, `include/common.slang`) |
| Clear colour | authored sRGB | `BeginRenderPass` |
| Screen-gizmo vertex colours | authored sRGB | not converted (drawn after the tonemap into a UNORM target) |

`ColorSpace.SrgbToLinear/LinearToSrgb` (C#) and `srgbToLinear/linearToSrgb` (Slang, `include/common.slang`) are the exact
IEC 61966-2-1 curves.

## Tonemap

- **Curve:** ACES filmic, Stephen Hill's fit of the RRT + sRGB ODT (input matrix, rational fit, output
  matrix), the usual "ACES fitted". Greys stay grey, black stays black, highlights roll off below 1
  (the procedural sky's `SunIntensity = 20` no longer clips). `ColorSpace.AcesFitted` is a C# mirror the
  render tests compare against. Bloom/glow and Godot's ACES are opt-in per world ([ADR 0124](../../memory/decisions/0124-godot-tonemap-and-glow.md); see below).
- **Exposure:** `IVulkanContext.Exposure` multiplies the HDR colour first. Default
  `IVulkanContext.DefaultExposure = 1.3`, calibrated so a white surface lit at ~0.75 (the test scenes'
  sun) shows about as bright as before the HDR pipeline; adjust it with the Demo's Basic 3D **Exposure** slider
  (or `RendererDebugWindow`).
- **Ambient default:** `LightEnvironment.DefaultAmbientColor` is sRGB (0.22, 0.22, 0.25) — linear
  ≈ 0.04, which after ACES' toe lights unlit surfaces like the old gamma-space (0.08, 0.08, 0.10) did.
  `WorldEnvironment.AmbientColor` defaults to the same value.
- The pass reads the scene with `texelFetch` at `gl_FragCoord` (one texel per pixel, no filtering),
  viewport not flipped (the scene image already has +Y up).

## Swapchain

`ChooseSurfaceFormat` (unit-tested) picks, in order:

| Encoding | When | Tonemap | Overlays (gizmos, UI) |
|---|---|---|---|
| `UnormShaderEncode` | default: an 8-bit UNORM format in `SRGB_NONLINEAR` | encodes sRGB in the shader | same pass, colours written as-is — **byte-identical** to before M3, including blending |
| `SrgbWithUnormOverlay` | only sRGB formats + `VK_KHR_swapchain_mutable_format` (or `MAINFRAME_SWAPCHAIN_ENCODING=srgb-mutable`) | writes an sRGB view (hardware encode) | separate overlay pass on a UNORM view of the same image (`LoadOp = LOAD`) |
| `SrgbOnly` | only sRGB formats, no extension (or `=srgb`) | sRGB view | linearises its colours (specialization constant); blending then happens in linear space |

The proposal preferred `B8G8R8A8_SRGB`. The UNORM path is the default because it is the only one that
keeps the overlays exact without a second pass over the swapchain image, it is one pass cheaper on tile-based
GPUs, and on MoltenVK the UNORM view of a mutable-format swapchain image intermittently refers to a
stale drawable (the overlay then misses the presented image on some frames — observed in the
`color-pipeline` render test). All three paths produce byte-identical scene pixels; the override exists
for QA.

## Spine premultiplied alpha

Handled once, in `SpineLit.vk.frag`: the CPU passes the slot/skeleton tint straight (never × alpha);
the shader premultiplies (straight atlases, decoded by the sRGB sampler) or un-premultiplies, decodes and
re-premultiplies (PMA atlases, stored UNORM because the exporter multiplied in sRGB space); output is
premultiplied with blend `One, OneMinusSrcAlpha`. The specialization constant `kPremultipliedTexture`
comes from the atlas page's `pma` flag.

## Render targets

`RenderTarget(ctx, RenderTargetDesc, extent)`: any number of colour attachments
(`RenderTargetAttachment(format, finalLayout, usage)`, e.g. `Sampled(R16G16B16A16_SFLOAT)` or
`Readback(R32_UINT)`) plus an optional depth attachment (`SampleDepth` keeps it). Every attachment is
cleared on `Begin(cb, clearValues)`. `Resize(extent)` recreates the images and framebuffer through the
deletion queue but keeps the render pass, so pipelines stay valid (callers rewrite descriptor sets that
sample it). Subpass dependencies order this frame's writes after the previous frame's sampling/copies
of the same images and make the writes visible to fragment sampling and transfers. The renderer's
scene target is one (`IVulkanContext.SceneTarget`); the editor viewport's colour + depth + object-ID
target will be another.

## Testing

- `color-pipeline` render scene: a panoramic sky made from a solid sRGB texture fills the frame, so
  every scene pixel must equal `encode(aces(decode(c) · exposure))` (±2) at two exposures; an opaque
  screen-gizmo rectangle must keep its exact sRGB value and 50 % white over black must give 128 (sRGB-space
  blend, as before). Covers the sRGB texture decode, HDR target, exposure, ACES, encode and the overlay.
- `sky-grid` render scene: every procedural-sky pixel away from the grid lines must equal the CPU reference
  (ray → gradient → exposure → ACES → encode, ±3) — the whole chain on a gradient, on every driver.
- `fxaa` render scene (`FxaaSmoothsEdgesButNotTheOverlay`, golden): FXAA blends the edges of an unshaded box (hundreds of
  grey pixels where no AA has none) and a screen-gizmo square drawn after it stays exact, with nothing bleeding out.
- `auto-exposure` render scene (`AutoExposureBrightensADarkSceneOverTime`, golden at frame 80): the light dims on frame
  20; the frame after is dark and 60 fixed frames later the mean luminance has risen ≥ 1.6× without overshooting.
- `sky-physical --count 2`: 0 B per frame with a moving sun, auto exposure, glow and FXAA (allocation gate).
- Unit tests: transfer curves, ACES properties, default-exposure and sky-ground calibration, swapchain choice;
  auto-exposure blend and clamps, the anti-aliasing setting's defaults and `project.mfproj` round trip.
- Goldens (`moltenvk`) re-recorded for M3 and reviewed: no clipping where lights overlap, coloured
  lights stay saturated, shadows keep the ambient tint.

## Known issues

- The procedural sky's ground gradient was inverted before M3 (ground colour at the horizon); fixed, and
  the ground default recalibrated for ACES ([Sky](sky.md#procedural-parameters)), so the area beyond the test
  scenes' floor is a muted earth tone. The scene grid draws before the floor and writes depth, so grid lines
  over the floor can show the sky behind it where the coplanar z-fight goes the line's way — a pre-existing
  ordering artefact.
- No HDR display output.
- SubViewports always use the engine curve without glow, auto exposure or FXAA (ADR 0124, ADR 0154).

## Godot tonemap and glow (ADR 0124)

A `WorldEnvironment` can ask for Godot 4.7's tonemap and glow (`Tonemapper = GodotAces`, `TonemapExposure`,
`TonemapWhite`; `GlowEnabled`, `GlowLevel1..7`, `GlowNormalized`, `GlowIntensity`, `GlowStrength`, `GlowMix`,
`GlowBloom`, `GlowBlendMode`, `GlowHdrThreshold/Scale/LuminanceCap`, all with Godot's defaults). The render server copies
the root world's `PostProcessSettings` to `IVulkanContext.PostProcess` each frame. With the default settings the frame is
exactly as above. Otherwise `BeginOverlayPass` records `GlowEffect` (two raster passes per level, `GlowBlur.vk.frag`,
Godot's 9-tap kernel, strength per level, exposure + threshold feedback + cap on the first level) between the scene pass
and the swapchain pass, then draws `TonemapPost.vk.frag`: × exposure, glow gathered with Godot's bicubic B-spline and
blended (Screen by default) before the curve, the engine's ACES fit or Godot's (input × 1.8, output ÷ the curve at
1.8 · white), soft-light glow after it, sRGB encode.

## Auto exposure (ADR 0154)

`WorldEnvironment.AutoExposureEnabled` (default off; `AutoExposureScale` 0.4 and `AutoExposureSpeed` 0.5 are Godot's,
`AutoExposureMinLuminance` 0.05, `AutoExposureMaxLuminance` 2) puts the root world's frame on the post tonemap pass and
records [`AutoExposure`](../../MainframeEngine/Src/Rendering/Post/AutoExposure.cs) after the scene pass, before the glow.
Everything is a raster pass and nothing is read back to the CPU:

| Pass | Target | Shader |
|---|---|---|
| Log luminance | 64 × 64 `R16_SFLOAT` | `AutoExposureLuminance`: per texel, the mean `log2(max(Y, 1e-5))` of a 4 × 4 grid of scene texels over its footprint |
| Reduce × 3 | 16², 4², 1² `R16_SFLOAT` | `AutoExposureReduce`: the mean of a 4 × 4 block |
| Adapt | 1 × 1 `R32_SFLOAT` | `AutoExposureAdapt`: `L = clamp(2^mean, min, max)`; `adapted = prev + (L − prev) · (1 − e^(−dt·speed))`, snapping on the first frame after it is enabled |
| Copy | 1 × 1 `R32_SFLOAT` | `AutoExposureReduce` again: adapted → previous, for the next frame |

`TonemapPost` and the glow's first level multiply the scene by `exposure × AutoExposureScale / adapted`: the manual
exposure (`IVulkanContext.Exposure`, or `TonemapExposure` for the Godot curve) is compensation on top. `dt` is
`IVulkanContext.FrameDeltaTime`, which `Engine` sets from `GameTime.DeltaTime` (fixed under `--fixed-fps`, so adaptation is
deterministic in render tests). Nothing blends, so no format needs blend support. Proposal G8d.3 describes a 64-bin
histogram with percentile clipping; this first version uses the mean log luminance (the histogram can replace the reduce
passes later without changing the tonemap side).

## FXAA (ADR 0154)

`AntiAliasing { None, Fxaa }` — `rendering.antiAliasing` in `project.mfproj` (`GameHost` applies it),
`EngineOptions.AntiAliasing`, or `IVulkanContext.AntiAliasing` at runtime; default `None`. With `Fxaa` the tonemap pass
(either pipeline, a copy built against the FXAA input's render pass) writes sRGB-encoded values into an
`R8G8B8A8_UNORM` image of the swapchain's size ([`FxaaPass`](../../MainframeEngine/Src/Rendering/Post/FxaaPass.cs)); the
present pass then draws `Post/Fxaa.vk.frag` (FXAA 3.11, PC quality preset 12, luma computed from the encoded colour) and
the overlay renderers follow in the same pass, so the 2D canvas, gizmos, UI and dev overlay are never filtered. On an
sRGB swapchain view the FXAA shader decodes before writing (the view encodes again). TAA (G8d.8) is not built.

## Related docs

[Vulkan renderer](vulkan-renderer.md) · [GPU resources](gpu-resources.md) · [Shaders](shaders.md) ·
[Coordinate conventions](coordinate-conventions.md#color-space) · [Spine](spine.md) · [Sky](sky.md) ·
[Lighting](lighting.md)
