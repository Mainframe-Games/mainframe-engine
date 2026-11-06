# ADR 0252 — World-space UI panels are segments of the UI command list, replayed before the scene

- **Date:** 2026-11-06
- **Status:** accepted
- **Milestone:** G11 (G11.5, G11.6 rendering)
- **Docs:** docs/design/world-space-ui.md#panels, memory/plans/2026-11-06-world-ui-panels.md

## Context

`UiPanel3D` needs a full RmlUi context rendered into a texture that meshes sample in the same frame. The UI renderer
records every context into one CPU command list during the tree's frame step and replays it in
`IOverlayRenderer.RecordOffscreen`, inside `BeginOverlayPass`, after the scene pass: too late for the scene. Its layer
stack, filter targets and stencil are swapchain-sized and shared. Materials sample a GPU image only through the
sub-viewport borrow path (ADR 0216).

## Decision

- **Segments.** `UiServer.Process` renders the due panels first, each after `VulkanUiRenderer.BeginTarget(target)`, then
  the screen layers after `BeginScreen()`. A panel segment draws through the existing region path at the stack's top-left,
  clipped to its size. Gradient uniforms are addressed by their index in the whole list, so two replays share the frame
  slot's region.
- **`IPreSceneRenderer`** (`IVulkanContext.AddPreSceneRenderer`/`RecordPreScene`): `RenderServer.RenderOffscreen` runs it
  after the skies' LUTs and before the sub-viewports. The UI renderer replays each panel segment into the shared stack,
  whose extent becomes the maximum of the swapchain and the largest registered panel (the overlay samples its top-left).
  A resolve pass (`UiPanelResolve.vk.frag.slang`) composites the layer over the panel's background, or un-premultiplies it
  for a transparent panel, into the panel's own `R8G8B8A8_UNORM` image (`MUTABLE_FORMAT`, full mip chain). Blits build the
  mips, and an explicit barrier hands every level to fragment and vertex shaders (MoltenVK ignores render-pass
  dependencies). The overlay still replays only the screen segment.
- **Images are created when a panel is recorded** (in `Process`, before `PrepareFrame`), so materials bind the image the
  pre-scene pass renders into that frame. `UiPanelTarget.SampledImage` hides an image that never resolved. A segment that
  never reached the GPU (a skipped frame) marks its panel `NeedsRender`.
- **`Texture2D.FromGpuSource`** (internal) and the `TextureGpu` borrow path, shared with viewport textures: an sRGB view for
  colour slots and a sampler over the image's mips. Panel textures clamp, so the edges do not bleed.
- **Gating:** at most 8 panels render per frame. Owed renders go first, then the nearest. `UpdateMode` `Always` panels
  render while within `MaxUpdateDistance` and inside the viewport camera's frustum, `Once` panels render once.
  Hovered or focused panels always render.

## Consequences

- Every panel effect works: filters, clip masks, gradients and box shadows go through the same replay code.
- One stack shared by panels and the HUD: a panel larger than the window grows it for the screen UI too. That memory
  is resident while the panel exists.
- Mip generation averages sRGB-encoded values (a small gamma error at distance).
- Panels are scene geometry: exposure, tonemap, TAA and glow apply. Open question 7 is left to the owner.
