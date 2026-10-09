# ADR 0163 — Post-processing stages, depth prepass and motion vectors

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/p`, forest slice: the foundation for SSAO and TAA)
- **Milestone:** Gameplay toolkit G6.6 / G8d.8 (their shared foundation); ahead of M11
- **Spec:** docs/design/future/rendering-features.md (G6.6 SSAO), docs/design/future/forest-showcase.md (G8d.8 TAA);
  current state in docs/design/post-processing.md, color-pipeline.md and vulkan-renderer.md

## Context

Brogan asked to "add SSAO and TAA next" and pointed out that the engine needs a general post-processing system. Until
now each post effect was wired into the renderer by hand: glow (ADR 0124), auto exposure and FXAA (ADR 0154), light
shafts (ADR 0160), each with its own creation, resize and recording code in `VulkanRenderer.Presentation.cs`, composited
by the post tonemap pass.

SSAO (GTAO, G6.6) needs depth before lighting and must feed the lit shaders; TAA (G8d.8) needs a projection jitter,
motion vectors for the camera, moving nodes and wind-animated foliage, a history that survives frames, and a way to
replace the HDR image the later effects read. The proposals parked both until **M11 step 1** (the encoder API):
rendering-features.md's "Vulkan now or after M11", forest-showcase.md's "What waits on M11" table and decisions, and
water.md decision 1 (`SceneTextures` after M11 step 1).

**Brogan's request overrides that decision**: SSAO and TAA are built now, on Vulkan, ahead of M11. The engine stays
Vulkan-only for now. This ADR builds the foundation; SSAO and TAA themselves are two parallel lanes on top of it.

## Decision

- **Three stages, one stack per view.** `PostStage.AfterPrepass` (depth and velocity ready, nothing lit: SSAO),
  `BeforeTonemap` (linear HDR: TAA, auto exposure, glow, light shafts, later depth of field) and `AfterTonemap` (LDR,
  before the UI: FXAA, later sharpening). `PostEffect` (internal, public-ready): stage, order (`PostEffectOrder`),
  `Needs` (`DepthPrepass`, `Velocity`, `Jitter`), `IsEnabled(PostEffectSettings)`, and `OnCreate` (lazy, first enabled
  frame), `OnBeginFrame`, `OnResize`, `OnRecord`, `OnDispose`. `PostProcessStack` keeps them sorted by stage, order and
  registration; effect instances belong to a view, so their fields are per-view state (TAA history). Only the main view
  has a stack, like glow before it.
- **`PostEffectContext`** per stage: the command buffer, `SceneTextures` (HDR colour, sampleable depth, RG16F velocity,
  the LDR image; no normals: GTAO rebuilds them from depth), a target pool (`PostTargetPool`: scene-relative named
  targets and `PostHistory` ping-pong pairs with validity tracking), the camera (`PostCamera`: unjittered and jittered
  matrices, last frame's view-projection, both jitters) and frame data.
- **The existing effects migrate into it unchanged.** Auto exposure, glow and light shafts are `BeforeTonemap` effects
  enabled exactly when the post tonemap ran before (the world's settings are not the default), FXAA is an `AfterTonemap`
  effect; the post tonemap still composites their outputs. With default settings nothing runs. Every golden is
  pixel-identical (checked exactly, not only within tolerance).
- **An HDR effect writes back into the scene colour** (`CopyToSceneColor`: one fullscreen pass), so later effects and the
  tonemap, whose sets bind the scene colour, need no per-source sets. **`AfterTonemap` chains** through an LDR ping-pong;
  the last effect draws into the swapchain pass and the overlay renderers follow in it.
- **Depth prepass, shared**, run when an enabled effect needs it (or `RenderServer.ForceDepthPrepass`,
  `MAINFRAME_DEPTH_PREPASS=1`). It draws the main view's opaque and cutout surfaces of lit, foliage (with wind) and
  terrain-splat materials, multimeshes included, into **the scene target's own depth** plus an `R16G16_SFLOAT` velocity
  image, then the sky's velocity (a far-plane triangle, depth EQUAL). The scene pass then begins with a compatible
  render pass that loads the depth; prepassed surfaces use a pipeline variant (`PipelineKey.Prepassed`) with no depth
  writes and LESS_OR_EQUAL, and **cutouts test EQUAL without `discard`** (the prepass already alpha-tested them).
- **Motion vectors**: screen UV this frame minus last frame, both unjittered. Camera motion from `FrameData`'s new
  `prevViewProjection`; per-node motion from **last frame's model matrix at vertex binding 3**: a block of previous
  `MeshInstanceData` appended to the prepassed view's instance allocation (the 12 spare bytes of the 80-byte instance
  cannot hold a matrix and G6.3 claims them), tracked per node by `MotionHistory`; multimeshes are static (binding 3 is
  their own buffer); foliage evaluates the wind at both times (`prevWind`, `prevWindParams`, `temporal.x`); the sky
  moves with the camera rotation.
- **`FrameData` grows to 560 bytes** (`prevViewProjection`, `jitter` xy/zw, `temporal`, `prevWind`, `prevWindParams`);
  `FrameContext` keeps a `ViewHistory` per view.
- **Projection jitter**: Halton (2, 3), 8 samples, from the frame number; applied by `FrameContext` to view 0's set-0
  matrices only when an effect asks (`Needs.Jitter`). Culling, shadows, shafts and the UI keep the camera's matrices.
- **SSAO binding**: set 0 binding 5 `ssaoTexture` (`include/ambient_occlusion.slang`, `ambientOcclusion(screenUv)`), a
  white 1×1 image unless an SSAO effect sets its output (`FrameContext.SetAmbientOcclusion`, latched per frame).
- **A velocity debug view** (`RenderServer.DebugView = RenderDebugView.Velocity`) as the last `AfterTonemap` effect,
  encoding velocity as exact display values, for tests and tuning.

## Measurements

The Forest (`just forest-bench`, 1920 × 1080, Apple M-series, MoltenVK, 1800 frames, two runs each): p50 13.09/13.20 ms
→ 11.71/11.76 ms and mean 12.84/12.81 → 11.72/11.71 ms with the prepass forced on. GPU timestamps: scene pass 9.69 ms
alone; prepass 3.23 ms + scene pass 4.95 ms with it. The prepass saves ≈ 1.1 ms per frame there (no hidden leaf shading,
no `discard` in the lit foliage shader). Prepass on vs off differs within the render tests' tolerance (≤ 0.12 % of pixels
beyond ±4 on `tree-forest`; equal-depth ties, cutout edge pixels, a code value of shading), with no holes.

## Alternatives considered

- **Wait for M11 step 1** (the proposals' decision): overridden by Brogan. The passes are written with
  `RenderTarget`/render-pass load and store intents stated, so the M11 port maps them one to one.
- **A separate prepass depth** copied or sampled by the scene pass: an extra depth image and no early-Z savings in the
  scene pass. Sharing the scene depth keeps light shafts' binding and saves memory.
- **EQUAL for every prepassed surface**: LESS_OR_EQUAL for opaque is as exact and forgives a smaller depth; EQUAL is
  needed only where the discard is skipped.
- **Keep `discard` in the main pass for cutouts**: loses most of the savings and still needs exact depth.
- **`invariant`/`precise` positions**: Slang 2026.1 emits neither `Invariant` nor `NoContraction`; identical expressions
  and the parity test stand in for them.
- **A previous-model buffer per node, or a flag in the instance padding**: a separate buffer needs its own indexing and
  the padding cannot hold a matrix; the appended block reuses the instance allocator and the instance index.
- **Ping-pong scene colour with per-source descriptor sets** in every effect and the tonemap: no copy, but every
  consumer needs sets per possible source. The write-back costs one fullscreen pass and keeps consumers simple; revisit
  if several full-resolution HDR effects chain.
- **A normals attachment in the prepass**: GTAO reconstructs normals from depth (rendering-features.md); a second
  attachment would change every prepass pipeline.

## Consequences

- SSAO (lane S) and TAA (lane T) are effects on this stack; docs/design/post-processing.md has their integration guide.
- Every shader including `frame.slang` was recompiled for the new block layout (goldens unchanged).
- The fragment stage uses 14 images and 11 samplers (terrain splat: 15 and 12) of MoltenVK's 16.
- `FxaaPass` became `FxaaEffect`; `AutoExposure`, `GlowEffect` and `LightShafts` derive from `PostEffect`; the renderer
  owns the LDR images the `AfterTonemap` stage uses.
- What the prepass does not draw (Spine, the grid, transparent surfaces, water, particles) has no velocity of its own;
  sub-viewports have no post effects. Game-defined effects (a Godot `CompositorEffect`-like hook) are designed in
  post-processing.md, not built.
