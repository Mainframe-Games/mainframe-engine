# Scene Grid

## Purpose

Editor-style reference grid with colored axis lines. In a scene tree it is the `Grid3D` node (no shadows;
`RenderPriority` -100 so it draws before other visuals, as the grid always did); it is a debug visual, added at runtime
rather than saved (the render-test scenes add one under `/root`). `Grid3D.Style` picks how it draws, when its GPU objects
are created:

- **`Lines`** (the default): a fixed line list that fades with distance, `GridSize` lines each way (below). The
  render-test scenes use it.
- **`Analytic`** (the editor's): drawn per pixel on an endless plane, anti-aliased lines with a width in pixels, levels
  that follow the zoom ([Analytic style](#analytic-style)).

## Key types

| Type | File | Notes |
|---|---|---|
| `SceneGrid` (abstract) | [SceneGrid/SceneGrid.cs](../../MainframeEngine/Src/Rendering/SceneGrid/SceneGrid.cs) | `(IRenderer, uint vertexCount)`, `Draw(ICamera)` |
| `SceneGrid2d` | [SceneGrid/SceneGrid2d.cs](../../MainframeEngine/Src/Rendering/SceneGrid/SceneGrid2d.cs) | XY plane at z = 0. X axis red, Y axis yellow. `(2·⌊gridSize/2⌋+1)·4` verts. |
| `SceneGrid3d` | [SceneGrid/SceneGrid3d.cs](../../MainframeEngine/Src/Rendering/SceneGrid/SceneGrid3d.cs) | XZ plane. X red, Z blue, plus a vertical yellow Y line. Default `gridSize = 200`. `(2·⌊gridSize/2⌋+1)·4 + 2` verts. |

Vertex: `vec3 Position` + `vec4 Color` + `vec3 Other` (stride 40); `Other` is the other end of the vertex's
line, filled in by `BuildVertexArray` (consecutive pairs are lines). Grid lines are white at α 0.1; axis lines
are opaque. Vertices are built in a load-time array.

### Clipping in the vertex shader

`SceneGrid.vk.vert` clips each line to the view volume itself (Liang–Barsky in clip space against
`|x|, |y| ≤ 1.25·w` — a guard band, so the rasterizer still trims the last bit to the viewport edges — and the
near and far planes); both ends order the segment the same way, so they agree on the clipped span or on
rejecting it (both ends then go behind the near plane). Without it, the lines that pass beside and behind the
camera reach the rasterizer with endpoints ~10⁵ pixels off-screen (where they cross the near plane). MoltenVK
draws those correctly; lavapipe drops some of them and smears others into stray fragments — which showed as a
grey haze over the ground between the lines in every lavapipe render test. The `sky-grid` render test
([Testing](testing.md#render-tests)) checks that every line over the ground is drawn and nothing is drawn
away from the lines.

## Analytic style

`SceneGridAnalytic` (internal, [SceneGrid/SceneGridAnalytic.cs](../../MainframeEngine/Src/Rendering/SceneGrid/SceneGridAnalytic.cs);
shaders `SceneGrid/GridAnalytic.vk.{vert,frag}`) draws one quad: the vertex shader makes 6 vertices (`SV_VulkanVertexID`,
no vertex buffer) on the plane `y = Grid3D.GlobalPosition.Y`, centred under the camera and as wide as the far plane.
The fragment shader draws the lines from the interpolated world position:

- **Width in pixels.** The distance to the nearest line in pixels is `|frac(p/cell + ½) − ½| / fwidth(p/cell)` per axis;
  coverage is `saturate(width/2 + ½ − distance)`: a line `width` pixels wide whose outer pixel fades, at any distance.
  Widths are in points × `Grid3D.LineScale` (the editor sets its view scale): minor 1, major 1.75, axes 2.5.
- **Levels.** Minor lines every cell (1 m), major every 10. When minor lines would come closer than 10 pixels
  (`log10` of the plane's pixel footprint), the level steps ×10 (10 m minor, 100 m major, …) with a cross-fade: the
  minor lines fade out over the level, the majors turn into minors and the next level's majors take over, so nothing
  pops while zooming.
- **Crowding.** Lines of one direction whose spacing falls under 3 pixels (the plane seen edge-on) fade out by 1.5
  pixels, each direction on its own, so there is no moiré near the horizon.
- **Axes.** The X axis (red, along z = 0) and the Z axis (blue, along x = 0); no Y line.
- **Fade.** With distance (to `min(0.8·far, max(60 × the camera's height above the plane, 40 cells))`, smoothly from 35 %
  of it) and below about 5° of elevation (to 25 %).
- **Depth.** Tested `LessOrEqual` against the scene, not written; the grid is pulled towards the camera by 2 pixels'
  footprint so it shows on a floor at its height instead of z-fighting it. Like the line grid it draws after the effects
  in a post-processed view (`kDisplayOutput`).

Line colours are sRGB with straight alpha: minor grey at 0.30, major at 0.55, axes at 0.95. Push constants (the
frame's 128-byte range): `params` = pixel scale, plane height, base cell size.

## GPU resources

| Resource | Detail |
|---|---|
| Vertex buffer | device-local `GpuBuffer.CreateStatic`, filled by the upload queue |
| Set 0 | the per-frame shared set (`FrameContext`): `viewProjection`, near/far |
| Pipeline | `LineList`, width 1, no cull, alpha blend (`SrcAlpha/OneMinusSrcAlpha` for color, `One/Zero` for alpha), depth test + write `Less` |
| Shaders | `SceneGrid.vk.vert` (`viewProjection · position` of both line ends, clipped as above; no depth remap since M1), `SceneGrid.vk.frag` (linearizes depth with the camera's near/far from set 0, decodes the sRGB line colour, fades colour and alpha by `1 − linearDepth/far`) |

`Draw(camera)` calls `Frame.EnsureCamera(camera)` (writes set 0 unless already written this frame), sets a Y-flipped viewport, binds set 0 and draws.

## Known issues

- The grid writes depth even though it alpha-blends.
- Lines on a coplanar surface (the test scenes' floor quad) z-fight: line and triangle depths are interpolated
  differently, so whether a line shows depends on the driver and on rounding (since the shader clipping, the
  receding lines over the floor lose on MoltenVK).
- `Lines`: distant lines are 1-pixel, aliased lines; near the horizon they merge into bright streaks (the `Analytic`
  style does not).

## Related docs

[Cameras & input](cameras-and-input.md) · [Coordinate conventions](coordinate-conventions.md) · [Shaders](shaders.md) ·
[Gizmos](gizmos.md)
