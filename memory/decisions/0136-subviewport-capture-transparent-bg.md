# ADR 0136 — SubViewport image readback and transparent 3D background

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM5 / E6 (the game's `docs/porting.md`)

## Context

Driving Range's item icons (`tex_<Item>.png`) are made by an editor tool: a `SubViewport` with `transparent_bg` and a
narrow camera renders the item, and `get_texture().get_image().save_png()` writes the icon.

The engine had neither half of this:
- **No readback.** Only the main frame could be captured (`SceneTree.CaptureFrame`).
- **No transparent 3D view.** `TransparentBg` applied to 2D canvases only: a 3D view cleared to its colour, and the
  tonemap wrote alpha 1.

## Decisions

1. **Transparent 3D background.** `SubViewport.TransparentBg` also clears a 3D view's HDR target to transparent black,
   and the sub-viewport tonemap keeps the scene's alpha.
   - The tonemap push constant's `encodeSrgb` field becomes flags: bit 0 encodes sRGB, bit 1 keeps alpha.
   - Opaque surfaces write alpha 1 already. Every other view still outputs alpha 1, so the goldens don't change.
2. **`SubViewport.CaptureImage(Action<FrameCapture>)`.**
   - The request waits until the view next renders.
   - `SubViewportCapture` copies the LDR colour image to a per-frame-slot readback buffer (`SHADER_READ_ONLY →
     TRANSFER_SRC → SHADER_READ_ONLY`, then a host barrier).
   - The render server delivers it with the picks, once the frame's fence has signalled.
   - Each delivery allocates its pixels. It is a tool path, not a per-frame one.
   - The LDR image gains `TRANSFER_SRC` usage.

## Consequences

- The icon tool ports as an editor tool or a dev command on `CaptureImage` (Driving Range G11).
- Test: render test `SubViewportCaptureReadsBackATransparentBackgroundAndALitObject` (scene `subviewport-capture`). The
  scene checks the corner alpha (0), the centre alpha (255) and the centre colour; there is no golden.
- **Known issues:**
  - Blended surfaces over a transparent background composite their alpha with Vulkan's alpha blend factors, not
    Godot's.
  - Post effects (glow) don't run on sub-viewports anyway.
