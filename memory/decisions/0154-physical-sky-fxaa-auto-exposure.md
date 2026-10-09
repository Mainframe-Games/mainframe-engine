# ADR 0154 — Physical sky, FXAA and auto exposure

- **Date:** 2026-10-08
- **Status:** accepted (implemented on `forest/d`, forest slice wave 1)
- **Milestone:** Gameplay toolkit G8d (G8d.1, G8d.3, the FXAA half of G8d.4)
- **Spec:** docs/design/future/forest-showcase.md; current state in docs/design/sky.md and color-pipeline.md

## Context

The forest showcase ([ADR 0149](0149-terrain-trees-water-engine-features.md)) needs a sky, sun, fog and IBL that agree,
anti-aliasing on thin branches, and an exposure that keeps the sky and the shade under the canopy readable. The engine
had a procedural gradient sky whose sun was independent of the scene's light, no anti-aliasing and a fixed exposure.
There is no compute in the renderer yet (M11), so every GPU pass must be a raster pass.

## Decision

- **`Sky.Mode = Physical`** (`SkyEnvironmentType.Physical`, `SkyPhysical`) is a Hillaire 2020 atmosphere:
  - Transmittance (256 × 64) and multiple-scattering (32 × 32, 64 directions looped per texel) LUTs render when the
    atmosphere changes; the sky-view LUT (192 × 108, relative to the sun's azimuth) when the atmosphere or the sun's
    elevation changes. All are fragment passes with a fullscreen triangle, recorded by `SkyEnvironment.Prepare` at the
    start of `RenderServer.RenderOffscreen`, before any pass draws a sky.
  - The sky pass samples the sky-view LUT through `skyRay` and adds a sun disc coloured by the transmittance towards the
    sun, computed on the CPU (`AtmosphereModel`). It binds the LUT as set 1 binding 0 like a textured sky and depends
    only on the frame set's camera, so a cube-face capture can draw it (`FragmentShaderPath`, `ResourceSetLayout`,
    `BindResources`).
  - Parameters use Godot's `PhysicalSkyMaterial` names on `Sky` and scale Hillaire's Earth constants by their ratio to
    Godot's defaults; `GroundColor` is shared with the procedural sky; `AltitudeMeters` (300) is the camera's height.
  - **The sun is the world's first `DirectionalLight3D`**; `Sky.SunDirection`/`SunColor` (energy 1) apply only without
    one. The procedural sky is unchanged.
  - **Units:** physical radiance per unit illuminance × π, because the engine's lights light a white Lambert surface to
    radiance `Energy` (no 1/π). The disc is `illuminance × transmittance × 2000`, not the physical ≈ 46 000, to stay
    finite in half floats.
- **`AntiAliasing { None, Fxaa }`**: `rendering.antiAliasing`, `EngineOptions.AntiAliasing` and
  `IVulkanContext.AntiAliasing`, default `None`. FXAA 3.11 (quality preset 12) reads an `R8G8B8A8_UNORM` intermediate
  that the tonemap writes sRGB-encoded, and draws into the swapchain pass before the overlay renderers, so the canvas,
  gizmos and UI are never filtered. TAA is not part of the enum until it exists.
- **Auto exposure** on `WorldEnvironment` / `PostProcessSettings` (`AutoExposureEnabled`, `Scale` 0.4, `Speed` 0.5,
  `MinLuminance` 0.05, `MaxLuminance` 2): the HDR scene's mean log2 luminance through a 64² → 16² → 4² → 1² raster
  chain, adapted in a 1 × 1 `R32_SFLOAT` (approached by `1 − e^(−dt·speed)`, snapping when enabled, copied to a
  "previous" texel for the next frame) and read by the post tonemap and the glow's first level as
  `exposure × Scale / adapted`. The manual exposure becomes compensation. `IVulkanContext.FrameDeltaTime` (set by
  `Engine`) drives it, so fixed-step runs are deterministic. Nothing is read back to the CPU and nothing blends.
- Everything is off by default, so existing scenes and goldens are unchanged.

## Alternatives considered

- **Compute for the multiple-scattering LUT and a luminance histogram** (Hillaire's and Unreal's way): no compute
  before M11. The fragment loop costs more per texel but runs only when the atmosphere changes.
- **A histogram with percentile clipping** (forest-showcase G8d.3): the mean log luminance is simpler and enough for
  the slice; the histogram can replace the reduce passes later without touching the tonemap side.
- **Godot's Preetham-style `PhysicalSkyMaterial` shader**: cheaper, but no multiple scattering, no planet shadow and
  no aerial perspective path for the fog and IBL work that follows.
- **Sky radiance in "physical" units with a camera exposure model**: the engine's lights and exposure are not
  photometric; scaling by π keeps today's lights, exposure and materials valid.
- **Luma in the LDR intermediate's alpha** (as FXAA 3.11 suggests): computing luma from RGB in the FXAA shader keeps
  the tonemap shaders unchanged.

## Consequences

- `RenderServer.RenderOffscreen` now prepares skies first; lane A1's IBL capture must run after it and can draw the
  physical sky with the hooks above.
- The post tonemap pass gains binding 2 (the adapted luminance) and the glow set binding 1; both are bound even when
  auto exposure is off (the texel is cleared once).
- `IVulkanContext` gains `AntiAliasing` and `FrameDeltaTime`; the editor's Project Settings gains Rendering ›
  Anti-Aliasing.
- Sub-viewports (the editor viewport) keep the engine tonemap without FXAA or auto exposure, as with glow
  (forest-showcase open question 12); the physical sky itself draws there.
- Open: the light colour following the atmosphere (open question 9, left authored; `AtmosphereModel.SunTransmittance`
  is ready for it), the histogram, TAA, LUT grading.
