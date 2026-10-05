# Scene Grid

## Purpose

Editor-style reference grid with colored axis lines, drawn as a line list that fades with distance. In a
scene tree it is the `Grid3D` node (`GridSize`; no shadows; `RenderPriority` -100 so it draws before other
visuals, as the grid always did); it is a debug visual, added at runtime rather than saved (the render-test
scenes add one under `/root`).

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
- Distant lines are 1-pixel, aliased lines; near the horizon they merge into bright streaks (no analytic,
  filtered grid yet).

## Related docs

[Cameras & input](cameras-and-input.md) · [Coordinate conventions](coordinate-conventions.md) · [Shaders](shaders.md)
