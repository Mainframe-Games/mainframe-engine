# Scene Grid

## Purpose

Editor-style reference grid with colored axis lines, drawn as a line list that fades with distance.

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
| Vertex buffer | device-local, uploaded once via staging |
| Set 0, binding 0 | VP UBO per swapchain image (vertex stage) |
| Pipeline | `LineList`, width 1, no cull, alpha blend (`SrcAlpha/OneMinusSrcAlpha` for color, `One/Zero` for alpha), depth test + write `Less` |
| Shaders | `SceneGrid.vk.vert` (applies the GL→VK depth remap), `SceneGrid.vk.frag` (linearizes depth with near 0.1 / far 1000 and fades color and alpha by `1 − linearDepth/far`) |

`Draw(camera)` writes the VP UBO for the current image, sets a Y-flipped viewport, and issues
`CmdDraw(vertexCount)`. Draw it after the sky and before scene geometry.

## Known issues

- `SceneGrid3d` allocates and draws `(gridSize+1)·6` vertices but fills only `(gridSize+1)·4 + 2`.
  The rest are degenerate, transparent lines at the origin.
- Vertices are built with `stackalloc` ([SceneGrid3d.cs:9](../../MainframeEngine/Src/Rendering/SceneGrid/SceneGrid3d.cs)),
  about 33 KB at the default size. A large `gridSize` can overflow the stack.
- The grid writes depth even though it alpha-blends.
- The shader's hard-coded near/far (0.1/1000) duplicates the `Camera3D` constants.

## Related docs

[Cameras & input](cameras-and-input.md) · [Coordinate conventions](coordinate-conventions.md) · [Shaders](shaders.md)
