# ADR 0185 — 2D canvas post-processing (CanvasEnvironment)

- **Date:** 2026-10-11
- **Status:** accepted
- **Milestone:** Crash Site Defense art polish (a vertical-slice look for a 2D game)
- **Docs:** docs/design/canvas.md#post-processing-adr-0185

## Context

Crash Site Defense (2D, on this engine) is getting an art pass toward a survival-horror vertical slice: a muted,
graded day, a dark night where fire and bioluminescence glow. The canvas had no post-processing at all: the engine's
glow, colour grade and the rest of the `PostProcessProfile` run on the 3D HDR scene before the tonemap, and the canvas is
composited after it. Godot covers this with a `WorldEnvironment` whose environment uses the Canvas background mode
(glow and adjustments over the canvas layers up to `background_canvas_max_layer`).

## Decision

- **A 2D node, `CanvasEnvironment`**, in the root viewport (the first one wins; its properties are read every frame, so
  a game can animate them): `Enabled`, `MaxLayer` (Godot's `background_canvas_max_layer`), Glow (intensity, threshold,
  soft knee, spread), Adjustments (brightness, contrast, saturation — Godot's — plus lift/gamma/gain and a tint),
  Vignette (strength, radius, softness, colour) and Film (grain amount and size, chromatic aberration). The engine's
  `WorldEnvironment` stays 3D-only; putting 2D settings on it would mix two pipelines in one node.
- **Where it runs.** The canvas server copies the settings into the frame (`CanvasFrame.Post`) and marks where the
  canvases above `MaxLayer` start in the main pass (`CanvasPass.PostSplit`; batches never merge across it). The renderer
  draws the batches before the split into the canvas layer, runs the glow chain (R16G16B16A16F levels from half size,
  13-tap downsample with a soft-knee bright-pass on the first, tent upsample), writes the graded result into a second
  RGBA8 target (`CanvasPost.vk.frag.slang`), draws the rest of the batches into it with a load pass (render-pass
  compatible with every canvas pipeline), and composites that target. Sub-viewports and clip-group targets are not
  post-processed; the game UI draws later and is never touched.
- **Gamma space, no HDR canvas.** The layer stays RGBA8 (Godot's `hdr_2d` off). Glow takes pixels above a gamma-space
  luma threshold, so emissive art is art that ends up bright after the canvas modulation (an unshaded or brightly
  modulated sprite at night). An HDR canvas target was considered and left out: it changes every canvas target's format
  for a gain the threshold already gives.
- **Off by default.** Without an environment nothing changes: the `canvas` golden is byte-identical.
- `CanvasPostMath` mirrors the grade, vignette and bright-pass on the CPU for tests.

## Consequences

- Render tests `canvas-post` (graded colour against `CanvasPostMath`, the layer above exact, glow) and its golden; unit
  tests `CanvasPostTests`.
- Two more render passes (load, glow) and three pipelines are created with the canvas renderer; targets are created on
  first use and on resize, nothing per frame.
- Not done: Godot's glow blend modes and per-level weights, the colour-correction LUT, HDR 2D.
