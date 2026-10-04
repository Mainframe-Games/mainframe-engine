# Scene Grid

## Purpose

Editor-style reference grid with colored axis lines, drawn as a line list that fades with distance. In a
scene tree it is the `Grid3D` node (`GridSize`; no shadows; `RenderPriority` -100 so it draws before other
visuals, as the grid always did); it is a debug visual, added at runtime rather than saved (the Sandbox adds
one under `/root`).

## Key types

| Type | File | Notes |
|---|---|---|
| `SceneGrid` (abstract) | [SceneGrid/SceneGrid.cs](../../MainframeEngine/Src/Rendering/SceneGrid/SceneGrid.cs) | `(IRenderer, uint vertexCount)`, `Draw(ICamera)` |
| `SceneGrid2d` | [SceneGrid/SceneGrid2d.cs](../../MainframeEngine/Src/Rendering/SceneGrid/SceneGrid2d.cs) | XY plane at z = 0. X axis red, Y axis yellow. `(gridSize+1)·4` verts. |
| `SceneGrid3d` | [SceneGrid/SceneGrid3d.cs](../../MainframeEngine/Src/Rendering/SceneGrid/SceneGrid3d.cs) | XZ plane. X red, Z blue, plus a vertical yellow Y line. Default `gridSize = 200`. |

Vertex: `vec3 Position` + `vec4 Color` (stride 28). Grid lines are white at α 0.1; axis lines are
opaque.

## GPU resources

| Resource | Detail |
|---|---|
| Vertex buffer | device-local `GpuBuffer.CreateStatic`, filled by the upload queue |
| Set 0 | the per-frame shared set (`FrameContext`): `viewProjection`, near/far |
| Pipeline | `LineList`, width 1, no cull, alpha blend (`SrcAlpha/OneMinusSrcAlpha` for color, `One/Zero` for alpha), depth test + write `Less` |
| Shaders | `SceneGrid.vk.vert` (plain `projection · view · position`; no depth remap since M1), `SceneGrid.vk.frag` (linearizes depth with the camera's near/far from set 0, decodes the sRGB line colour, fades colour and alpha by `1 − linearDepth/far`) |

`Draw(camera)` calls `Frame.EnsureCamera(camera)` (writes set 0 unless already written this frame), sets a Y-flipped viewport, binds set 0 and draws.

## Known issues

- `SceneGrid3d` allocates and draws `(gridSize+1)·6` vertices but fills only `(gridSize+1)·4 + 2`.
  The rest are degenerate, transparent lines at the origin.
- Vertices are built with `stackalloc` ([SceneGrid3d.cs:9](../../MainframeEngine/Src/Rendering/SceneGrid/SceneGrid3d.cs)),
  about 33 KB at the default size. A large `gridSize` can overflow the stack.
- The grid writes depth even though it alpha-blends.

## Related docs

[Cameras & input](cameras-and-input.md) · [Coordinate conventions](coordinate-conventions.md) · [Shaders](shaders.md)
