# Gizmos

## Purpose

How the editor draws its viewport gizmos: the transform gizmo, the scene icons of lights, cameras and audio nodes, and
their shapes. All of these are drawn by the engine's **gizmo draw list**: lines with a real width in pixels and
anti-aliased edges, solid shaded shapes, and icons rasterised from SVG. Collision shapes, selection boxes, paths and
navigation stay [debug lines](#debug-lines-on-surfaces) (1 pixel). The editor grid is the analytic style of
[`Grid3D`](scene-grid.md#analytic-style). The dev overlay's screen gizmos (light and axis markers in a game window) are
something else: [Developer overlay → Screen gizmos](dev-overlay.md#screen-gizmos).

## Key types

| Type | File | Notes |
|---|---|---|
| `GizmoDrawList` | [Rendering/Gizmos/GizmoDrawList.cs](../../MainframeEngine/Src/Rendering/Gizmos/GizmoDrawList.cs) | Immediate-mode list: `Line`/`Polyline`/`Circle` (width in pixels), `Triangle`, `Disc`, `Cone`, `Box`, `Icon`; `SetCamera` for shading and culling; grows, then 0 B |
| `GizmoIconAtlas` | [Rendering/Gizmos/GizmoIconAtlas.cs](../../MainframeEngine/Src/Rendering/Gizmos/GizmoIconAtlas.cs) | RGBA8 straight-alpha sRGB atlas: a white block, then icons in rows (`Add` → index, `UvRect`, `Version`) |
| `GizmoRenderer` (internal) | [Rendering/Gizmos/GizmoRenderer.cs](../../MainframeEngine/Src/Rendering/Gizmos/GizmoRenderer.cs) | One triangle-list pipeline per target (scene pass / after post × depth-tested / on top); atlas at set 1 |
| `SceneViewport.Gizmos` / `OverlayGizmos` | [Scene/SceneViewport.cs](../../MainframeEngine/Src/Scene/SceneViewport.cs) | Depth-tested / always on top; drawn after `DebugLines` / `OverlayLines` and cleared with them |
| `TransformGizmo`, `TransformGizmo2D` | [Editor/Viewport/](../../MainframeEngine.Editor/Src/Viewport/) | The editor's handles: math, picking and drawing |
| `ViewportController` (icons) | [Editor/Viewport/ViewportController.Icons.cs](../../MainframeEngine.Editor/Src/Viewport/ViewportController.Icons.cs) | Scene icons from `Assets/gizmos/*.svg`, light/camera/audio shapes |

## The draw list

- **Lines** are one quad each (6 vertices) carrying both ends, a side (±1) and the width in framebuffer pixels. The
  vertex shader (`Shaders/Gizmos/Gizmo.vk.vert`) projects both ends (clipped to the near plane), works in the frame of
  the segment from its lexicographically first end, so both ends agree on the sides, and pushes the corners out by half
  the width plus one pixel (`frame.viewport` gives the pixel size). The fragment shader computes the distance to the
  segment with round caps and fades the outer pixel: anti-aliased at any width, no MSAA. A polyline's joints overlap,
  which only shows on translucent lines.
- **Solids** (`Cone`, `Box`, `Disc`, `Triangle`) are tessellated on the CPU. `Cone` and `Box` are shaded on the CPU for
  the camera given to `SetCamera`: a key light from the viewer's upper left (`0.45 + 0.55·n·l` plus a small highlight),
  faces turned away culled, and the silhouette edged with a 1-pixel anti-aliased line (the faces themselves are not
  anti-aliased). Without a depth buffer of their own only convex shapes draw correctly, and shapes overlap in
  submission order: draw far shapes first.
- **Icons** are quads that sample the list's `Atlas`; lines and solids sample its white block, so one pipeline and one
  draw serve everything. Without an atlas the renderer binds a 1×1 white texture.
- **Colours** are sRGB-authored with straight alpha, like debug lines: linearised in the HDR scene pass, written as
  display values after post.
- **2D views** (a canvas): `CanvasFrame.AppendGizmos` turns the list into canvas triangles in target pixels, expanding
  lines on the CPU (1-pixel faded edge, square caps). Icons are not drawn in 2D.

## Renderer

`RenderServer` draws a 3D view's lists in `DrawLines` (scene pass) or `DrawAfterPost` (a post-processed view, ADR 0169):
`DebugLines`, `Gizmos`, `OverlayLines`, `OverlayGizmos`, in that order, then clears all four
(`SceneViewport.ClearDebugVisuals`, also when the view did not render). Vertex buffers are dynamic `GpuBuffer`s per
frame slot, appended to by every draw of the frame and doubled when a draw does not fit. An atlas is uploaded the first
time a list using it is drawn and again when its `Version` changes; the old texture and descriptor pool go to the
deletion queue. Depth-tested lists test `LessOrEqual` without writing; after post they write `afterPostLineDepth`.

## Editor transform gizmo

Sizes are in points × `PixelScale` (= the view's `ViewScale`), so they keep their size on HiDPI views.

| | |
|---|---|
| Shafts and rings | `LineWidth` 3 pt; hovered or dragged `HoverLineWidth` 4.5 pt |
| Translate | shaft from `CenterGap` (0.14 of the length) to a solid cone (`ConeHeight` 0.24, `ConeRadius` 0.075) |
| Scale | shaft to a solid cube (`CubeHalf` 0.065) in the node's local axes; the centre is a cube too (uniform scale) |
| Centre (translate) | a screen-facing white disc with a dark rim |
| Rotate | three rings (96 segments): the far half of each at 28 % alpha, drawn before the near halves; a faint silhouette circle; while dragging, a translucent yellow sector from the grab direction through the applied angle |
| Hover | the axis colour lightened 45 % toward white, wider shaft, heads 15 % larger |
| Drag | the dragged handle yellow, the others at 30 % alpha |
| Axes pointing at the camera | fade out from 0.985 to ~0.945 alignment (a shaft seen end-on cannot be dragged well) and cannot be picked |
| Order | the three handles far to near by their direction's depth, the centre between those pointing away and those pointing at the camera |

**Picking** (`HitTest`) uses the drawn shapes: a shaft is grabbed within half the hovered width plus `GrabMargin` (5 pt)
of its centre line, a head within its on-screen radius plus `GrabMargin`, the centre within its radius; a ring within
half its hovered width plus `GrabMargin`, its far half counting `GrabMargin` farther so the near half wins where two
rings cross. `TransformGizmo2D` draws through the same list: 3 pt shafts with solid arrow heads or squares, a solid
centre square with a dark rim, a 3 pt rotation ring with a spoke and a dot at the current angle.

## Scene icons

Lights, cameras and audio nodes show an icon: a dark disc badge with a white glyph (`MainframeEngine.Editor/Assets/
gizmos/*.svg`: `light-directional`, `light-omni`, `light-spot`, `camera`, `audio`, `audio-listener`; glyphs redrawn from
Tabler Icons, MIT). `ViewportController.EnsureIconAtlas` rasterises them with ThorVG (`Svg.Rasterize`) at `IconPoints`
(28) × `ViewScale` pixels into a `GizmoIconAtlas`, again only when the view scale changes, so they are crisp at any
display scale. Each icon is a camera-facing quad in `OverlayGizmos` (on top, like Godot's), ringed in an accent colour:
the light's own colour at full brightness, violet for cameras, cyan for audio, the selection colour (and a wider ring)
when selected. Clicking within the icon's radius selects the node.

Their shapes are depth-tested gizmo lines 1.5 pt wide in the accent colour: a directional light's arrow, a spot light's
cone (to its range when selected), an omni light's or audio player's range when selected (three faint great circles
and the sphere's outline as the camera sees it), a camera's frustum with a solid "up" triangle. `Marker3D` keeps its
axis cross (debug lines).

## Debug lines on surfaces

Debug lines often lie exactly on a surface: a box collider on its mesh, a floor's outline on the floor. Depth-tested as
they are, they z-fight it, and after post the scene depth is jittered by TAA. `DebugLines.vk.frag` pulls every
depth-tested line towards the camera by 8 pixels' footprint (`kPullPixels`, about 0.9 % of the view depth at 1080p) in
both the scene pass and after post: enough at grazing angles down to about 4°, little enough that a line well behind a
surface stays hidden. The grid and gizmo lists keep `afterPostLineDepth` (4 pixels) after post; the analytic grid pulls
itself 2 pixels in both paths.

## Tests

- `GizmoDrawListTests` (unit): a line's vertices, culling and shading of solids, icon UVs, the vertex cap, 0 B refills,
  the atlas packing.
- `TransformGizmoTests` (editor): hit areas match the drawn heads and widths and scale with `PixelScale`; rings grabbed
  on both halves; drawing adds shapes.
- `GizmoRenderTests` (render, `gizmos` / `gizmos-taa` scenes): lines are their width in pixels with blended edges, the
  cone and box draw shaded, debug lines on a crate and a floor seen at a grazing angle stay visible under TAA, and a frame
  that re-adds everything allocates nothing.
- `just qa-editor Tests/QA/editor-gizmos.qa`: captures of each mode idle and mid-drag, the icons, a selected spot light.

## Related docs

[Editor](editor.md) · [Scene grid](scene-grid.md) · [Post-processing → Sub-viewports](post-processing.md#sub-viewports) ·
[Canvas](canvas.md) · [Shaders](shaders.md)
