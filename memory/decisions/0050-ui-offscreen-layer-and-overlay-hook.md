# ADR 0050 — UI rendering: recorded command list, offscreen layer, overlay hook, explicit barriers

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M8 (W4 lane m8)
- **Spec:** docs/design/game-ui.md#rendering-vulkanuirenderer

## Context

The proposal drew v1 inside the main pass and v2 (clip masks, layers, filters) in "its own UI render pass after the
tonemap" with a stencil attachment and offscreen layer targets. M3a had since moved ImGui into an overlay pass on the
swapchain after the tonemap (no stencil attachment, shared with ImGui). RmlUi issues its render callbacks from
`Context::Render`, may create textures (font atlases) while doing so, and with effects needs to switch render targets
mid-stream (push/composite/pop layers), which is impossible inside a single swapchain render pass.

## Decisions

1. **Record, then replay.** `context.Render()` runs in the UI server's frame step (between frames, so new textures go
   through the upload queue without a GPU wait) and appends `UiCommand` structs to a grow-only list, resolving
   geometry and texture handles to buffers/descriptor sets immediately. The Vulkan commands are recorded later.
2. **New `IOverlayRenderer` hook on `IVulkanContext`** (`AddOverlayRenderer`): `BeginOverlayPass` calls
   `RecordOffscreen` after the scene pass ends (no pass active) and `RecordOverlay` inside the overlay pass right after
   the tonemap, before ImGui. Draw order: scene → tonemap → game UI → ImGui.
3. **The UI always renders into an offscreen layer** (`R8G8B8A8_UNORM`, premultiplied sRGB-encoded values, plus a
   shared `S8`/packed stencil) and is composited with one fullscreen premultiplied draw. This gives v1 and v2 one code
   path: clip masks use the layer's stencil, filters and `SaveLayerAsTexture` switch between layer and filter targets
   freely, ImGui's pass is untouched, and frames without UI cost nothing. Blending in sRGB space matches RmlUi's
   backends and browsers; the result is byte-identical to drawing straight into the UNORM swapchain.
4. **Effects ported from RmlUi 6.3's GL3 backend** (the 6.3 Vulkan backend has no effects), using the shim's optional
   callbacks: layers, opacity/blur/drop-shadow/colour-matrix/mask-image filters, gradients.
5. **Explicit pipeline barriers between UI passes.** The passes declare correct external subpass dependencies, but on
   MoltenVK they did not order work on sub-allocated (heap-placed) images: a layer resumed after a filter pass read
   stale data and later draws vanished (deterministically). A `vkCmdPipelineBarrier` before every UI pass and before
   the composite fixed it on MoltenVK and is harmless elsewhere. Dedicated allocations also hid it, but barriers are the
   correct fix. `vkCmdClearAttachments` is implemented by MoltenVK as an internal draw that leaves a different stencil
   reference, so the replay invalidates its cached dynamic state after clears.

## Consequences

- One extra swapchain-sized RGBA8 target (+ stencil, + filter targets on demand) and one fullscreen composite per frame
  that has UI; negligible on desktop (Sandbox stays at 121 fps, display-limited).
- Engine render targets other than the UI rely on subpass dependencies too; if MoltenVK ordering problems appear
  elsewhere (e.g. the stale-drawable symptom in ADR 0006), explicit barriers are the first thing to try.
- Box-shadow textures are limited to the window size (an RmlUi behaviour).
