# 2D canvas

## Purpose

Godot-compatible 2D drawing: canvas items (`Node2D`, `Sprite2D`, custom `OnDraw`), canvases and layers, draw order,
modulation and blending exactly as Godot 4.7's `RendererCanvasCull` + `RendererCanvasRenderRD` with `hdr_2d` off, so a
game ported from Godot draws the same pixels. Built for the Crash Site Defense port (its `docs/porting.md`, rows E2/E3);
ADRs [0110 Y-down](../../memory/decisions/0110-y-down-2d.md), [0111 canvas renderer](../../memory/decisions/0111-canvas-renderer.md).

| Piece | Where | What |
|---|---|---|
| Nodes | [Scene/Canvas/](../../MainframeEngine/Src/Scene/Canvas/), [Scene/Nodes2D/](../../MainframeEngine/Src/Scene/Nodes2D/) | `CanvasItem` (base of `Node2D`), `Sprite2D`, `CanvasLayer`, `CanvasModulate`; `Canvas` (a viewport's root canvas or a layer's) |
| Commands | `CanvasDrawList`, `CanvasPrimitives` | per-item vertices/indices recorded by `OnDraw`; Godot's tessellation (64-segment circles, polyline strips with clamped joints, feathered antialiasing, ear-clipping triangulation) |
| Ordering | [Rendering/Canvas/CanvasCuller.cs](../../MainframeEngine/Src/Rendering/Canvas/CanvasCuller.cs) | port of `_cull_canvas_item`: z lists −4096…4096, tree order, show-behind-parent, y-sort, top level, viewport culling |
| Server | `CanvasServer` (`IFrameServer`) | runs queued draws, culls every canvas of the root viewport (root canvas + visible layers, by layer) into one `CanvasFrame` |
| Renderer | `VulkanCanvasRenderer` (`IOverlayRenderer`) | draws the frame into an RGBA8 UNORM layer (gamma space), composites it after the tonemap, below the game UI |
| Materials | `CanvasItemMaterial`, `ShaderMaterial` + `Shader` | blend mode (mix, add, sub, mul, premultiplied, disabled) and light mode; canvas shaders (E5, in progress) |
| Shaders | [Content/Shaders/Canvas/](../../MainframeEngine/Content/Shaders/Canvas/), `include/canvas.glsl` | default vertex/fragment stages; push constants mirror Godot's world/canvas/screen transforms and canvas modulation |

## Model

- **2D is Y-down** (ADR 0110). `Transform2D` maths is Godot's.
- **Canvas items** keep a draw list. `QueueRedraw()` queues `RunDraw` for the end of the frame (`SceneTree.FlushCanvasRedraws`,
  called by the canvas server): the list is cleared, then the type's built-in draw (`Sprite2D`), the `Draw` signal and
  `OnDraw()` record into it. `Draw*` outside a draw throws. Items redraw on entering the tree and on becoming visible.
- **Canvases.** A canvas item whose parent is not a canvas item (or that is `TopLevel`) is a *root* of a canvas: the
  nearest `CanvasLayer` ancestor's, else its viewport's `RootCanvas` (layer 0). Roots draw in the order of their index
  among their parent's children (Godot's draw index), ties by registration. The root canvas transform is
  `SceneViewport.CanvasTransform` (the camera), a layer's is its own transform. Layers draw by `Layer`, the root canvas
  first among layer 0; hidden layers are skipped.
- **Order** within a canvas (CanvasCuller): an item is attached to the list of its absolute z (relative to its parent
  unless `ZAsRelative` is false); children follow their parent unless `ShowBehindParent`; a `YSortEnabled` item flattens
  its subtree (the item itself first, nested y-sort subtrees included) and orders it by the Y of each item's origin
  relative to the sort root, ties (is_equal_approx) by collection order. Lists are concatenated from the lowest z.
- **Modulate** multiplies down the tree; `SelfModulate` applies to the item only; an item with final alpha < 0.007 is
  skipped with its subtree. `CanvasModulate` (the last visible one on a canvas) multiplies every fragment of the canvas
  after the item's colour and before lights, except for unshaded materials.
- **Culling:** an item whose drawn bounds (transformed) miss the viewport rect is not attached; its children still are.
- **Textures** are sampled raw (UNORM, gamma space), straight alpha, with the item's resolved `TextureFilter`/
  `TextureRepeat` (default linear, no repeat); `draw_texture_rect(tile: true)` forces repeat for that rect.

## Camera and stretch

- **`Camera2D`** is Godot 4.7's, ported: while current it writes `SceneViewport.CanvasTransform` (anchor centre or top
  left, Godot zoom where 2 = twice as close, offset, limits, drag margins, position/rotation smoothing). The scroll updates
  when Godot updates it: in `OnProcess` (Godot's internal process; subclasses call `base.OnProcess` first), on transform
  changes without smoothing, and on every `Zoom`/`Offset` set, which keeps the smoothed position (so a script that sets
  the zoom every frame sees Godot's double smoothing step). `GetScreenCenterPosition()`; a camera entering a viewport
  without a current camera makes itself current (when `Enabled`).
- **Content scale** (Godot's window stretch) is computed by `ContentScale.Compute` (a port of
  `Window::_update_viewport_size`) on the root viewport each frame (`SceneViewport.SetSize`): `canvas_items` mode draws
  the canvas at the window's resolution from a base size, `Expand`/`Keep`/`KeepWidth`/`KeepHeight`/`Ignore`, fractional
  or integer scale, letterbox margins. `SceneViewport.GetVisibleRect()` is the visible area in canvas units;
  `StretchTransform` multiplies every canvas (layers included). Project file: `window.stretchMode` (`canvas_items`),
  `window.stretchAspect`, `window.stretchScale`, `window.stretchScaleMode`; base size = `window.width/height`.
  `rendering.canvasClearColor` sets `CanvasServer.ClearColor`.

## Frame

1. `Tree.Tick` (process), then frame servers: `CanvasServer.Process` flushes redraws, builds `CanvasFrame` (vertices in
   target pixels, pre-transformed and pre-modulated; one batch per run of equal texture, sampler, material, blend,
   primitive and canvas modulate) and uploads new textures (`VulkanCanvasRenderer.PrepareTextures`, so the uploads are
   recorded at the next frame's start).
2. `BeginOverlayPass`: `RecordOffscreen` writes the frame's geometry into per-frame-slot dynamic buffers and draws the
   batches into the canvas layer, cleared to `CanvasServer.ClearColor` (opaque: a 2D game; null: transparent over the
   3D scene). After the tonemap `RecordOverlay` composites the layer premultiplied (Godot's mix blend into a
   transparent target is premultiplied by construction), then the game UI and ImGui draw.

## Known issues

- Not yet: canvas shaders (E5), 2D lights (E6), 2D `SubViewport`/`ViewportTexture` (E7), text (E4),
  Camera2D physics interpolation, the `viewport` stretch mode (treated as `canvas_items`), `ClipChildren`
  (canvas groups), nine-patch, meshes/multimeshes, physics interpolation of canvas items, pixel snapping.
- The editor's 2D view does not draw canvas items yet (E18).
- 1 px lines (width −1) on exact integer coordinates rasterise on whichever side the GPU picks, as in Godot.
