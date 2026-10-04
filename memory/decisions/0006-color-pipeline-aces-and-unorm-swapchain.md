# ADR 0006 — Colour pipeline: ACES fitted, UNORM swapchain, UI after tonemap

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W2 lane m3a)

## Context

The proposal (now [docs/design/color-pipeline.md](../../docs/design/color-pipeline.md)) left open: ACES
or AgX, bloom or not, and how ImGui (authored in sRGB) is composited once the swapchain is sRGB. The
user asked for ImGui to look identical to before.

## Decisions

1. **Tonemap: ACES fitted** (Stephen Hill's RRT + ODT fit, in linear Rec.709), not AgX: the de facto
   standard look, cheap, and with a C# mirror (`ColorSpace.AcesFitted`) the tests compare pixels against.
   AgX can be added as an option later. **No bloom** in M3.
2. **Exposure default 1.3** (`IVulkanContext.DefaultExposure`), calibrated so a white surface lit at
   ~0.75 shows about as bright as before; **default ambient** raised to sRGB (0.22, 0.22, 0.25) so unlit
   surfaces do too (the old 0.08 was a display-space value; linear 0.007 vanishes in ACES' toe).
3. **Authored colours are sRGB everywhere** (lights, ambient, shapes, grid, sky, Spine tint, clear
   colour) and are converted once on the way in; lighting and blending of scene content are linear.
4. **Swapchain: UNORM + shader encode by default**, sRGB swapchains as fallback/opt-in
   (`MAINFRAME_SWAPCHAIN_ENCODING`). Deviation from the proposal's `B8G8R8A8_SRGB` preference, because:
   ImGui must stay byte-identical (gamma-space blending), which an sRGB target only allows through a
   second pass on a mutable-format UNORM view; that extra pass costs bandwidth on tilers; and on MoltenVK
   the UNORM view of a mutable-format swapchain image intermittently resolves to a stale drawable
   (observed: the overlay vanished on some frames). Scene pixels are byte-identical across all paths.
5. **ImGui draws after the tonemap** into the swapchain (overlay pass, `BeginOverlayPass`), never into
   the HDR target.
6. **Spine PMA handled once, in the shader**; PMA atlases stay UNORM and are un-premultiplied, decoded and
   re-premultiplied (the exporter multiplied in sRGB space), straight atlases are sRGB images.
7. **Procedural sky ground gradient fixed** (it was inverted); accepted golden change.

## Consequences

- `moltenvk` goldens re-recorded; `lavapipe` goldens are still to be recorded from CI.
- The `color-pipeline` render test pins the numeric behaviour (texture decode, exposure, ACES, encode,
  overlay colour and blending).
