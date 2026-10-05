# ADR 0111 — A Godot-compatible 2D canvas renderer in gamma space

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM1 / E2–E3 (the game's `docs/porting.md`); design doc
  [canvas.md](../../docs/design/canvas.md)

## Context

The engine had no 2D renderer: `Node2D` held a transform, `ZIndex` was unused and `Camera2D` drew 3D visuals through the
HDR target and ACES. A Godot 2D game being ported must look the same, and Godot (Forward+, `hdr_2d` off) blends 2D in
sRGB space in an RGBA8 target, with its own draw order rules (z lists, y-sort, show behind parent, top level) and
tessellation.

## Decisions

1. **`CanvasItem : Node` becomes the base of `Node2D`**, mirroring Godot's API (Visible, Modulate, SelfModulate,
   ZIndex/ZAsRelative, YSortEnabled, ShowBehindParent, TopLevel, Material/UseParentMaterial, texture filter/repeat,
   `QueueRedraw` + `OnDraw` with Godot's `draw_*` calls). Colours are `Vector4` (floats; modulate may exceed 1) since the
   engine's `System.Drawing.Color` is 8-bit.
2. **Separate canvas pass in gamma space, composited after the tonemap below the game UI** — the same `IOverlayRenderer`
   hook and layer/composite design as the RmlUi renderer (ADR 0050). The canvas server is registered before the UI
   server so its overlay draws first. A canvas clear colour makes the layer opaque for 2D-only games.
3. **Godot's semantics are ported, not approximated**: `RendererCanvasCull` ordering, Godot's blend states, the
   `canvas_item_add_*` tessellation, `Triangulate`, `Sprite2D::_get_rects`. The culler is engine-free and unit-tested.
4. **CPU-transformed batching**: vertices are transformed to target pixels and modulated on the CPU and batched across
   items; only items whose shader has `vertex()` keep local vertices with MODEL/CANVAS matrices as push constants (one
   draw each), matching Godot's built-ins.
5. **Textures upload in the frame step**, never mid-frame (uploads are recorded at frame start).

## Consequences

- One more swapchain-sized RGBA8 target and composite when anything is on the canvas.
- Future 2D work (canvas shaders, lights, sub-viewports, SVG, camera/stretch, text, clip children) builds on this
  (tracked in the game's porting doc and canvas.md's known issues).
