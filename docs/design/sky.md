# Sky

## Purpose

Draws the background as a fullscreen triangle before any geometry. There are three types: procedural
gradient + sun, equirectangular panorama, or cubemap. In scenes the sky is a `WorldEnvironment` node holding
a `Sky` resource (`Mode`, `Panorama`, `CubemapFaces` and the procedural/sun parameters below); the render
server builds the matching `SkyEnvironment` on first draw, rebuilds it when the mode or images change, and
draws it first in the main pass. See [Scene graph & nodes](scene-graph-and-nodes.md#servers-and-render-nodes).

## Key types

| Type | File | Constructor |
|---|---|---|
| `SkyEnvironment` | [Sky/SkyEnvironment.cs](../../MainframeEngine/Src/Rendering/Sky/SkyEnvironment.cs) | `protected`; `public class : IDisposable` |
| `SkyProcedural` | [Sky/SkyProcedural.cs](../../MainframeEngine/Src/Rendering/Sky/SkyProcedural.cs) | `(IRenderer)` |
| `SkyPanoramic` | [Sky/SkyPanoramic.cs](../../MainframeEngine/Src/Rendering/Sky/SkyPanoramic.cs) | `(IRenderer, string? panoramicPath)` |
| `SkyCubemap` | [Sky/SkyCubemap.cs](../../MainframeEngine/Src/Rendering/Sky/SkyCubemap.cs) | `(IRenderer, string[] facePaths)`, in +X −X +Y −Y +Z −Z order |
| `SkyEnvironmentType` | [Sky/SkyEnvironmentType.cs](../../MainframeEngine/Src/Rendering/Sky/SkyEnvironmentType.cs) | `Procedural`, `Panoramic`, `Cubemap` |

### Procedural parameters

| Property | Default |
|---|---|
| `SkyColor`, `HorizonColor`, `GroundColor` | gradient stops, sRGB: (0.18, 0.48, 0.87), (0.70, 0.85, 1.00), `SkyEnvironment.DefaultGroundColor` (0.42, 0.39, 0.35) |
| `SunDirection`, `SunColor` | normalize(0.3, 1, 0.5); (1.00, 0.95, 0.85) |
| `SunIntensity` | 20 |
| `SunAngularRadius` | 0.53° |
| `HorizonSharpness` | 6 |

## GPU resources

| Resource | Detail |
|---|---|
| Set 0 | The per-frame shared set (`FrameContext`): camera (`invProjection`, `invViewRotation`); `Draw(camera)` calls `EnsureCamera` |
| Set 1, binding 0 | `CombinedImageSampler` (Panoramic/Cubemap only), one shared set |
| Push constants | `SkyParams` (96 B, below) |
| Texture | `GpuTexture`, `R8G8B8A8Srgb` (decoded to linear by the sampler), 1 mip. Panoramic: 2D image. Cubemap: 6 layers, `CubeCompatible`, Cube view. Uploaded by the upload queue at the start of the next frame (no queue wait). |
| Sampler | Linear, ClampToEdge |
| Pipeline | No vertex input, no cull, **depth test/write off**, no blend, dynamic viewport and scissor, HDR scene pass; layout from `FrameContext.CreatePipelineLayout` |

### `SkyParams` (push constants, 96 B)

| Offset | Field |
|---|---|
| 0 | `vec4 skyColor` |
| 16 | `vec4 horizonColor` |
| 32 | `vec4 groundColor` |
| 48 | `vec4 sunDirection` |
| 64 | `vec4 sunColorIntensity` (rgb colour, a intensity) |
| 80 | `vec4 sun` (x = cos of angular radius, y = horizon sharpness) |

Colours are authored in sRGB and decoded to linear in the shader; the sun (`SunColor × SunIntensity`,
20 by default) is an HDR value that the tonemap rolls off instead of clipping
([Color pipeline](color-pipeline.md)).

The ground default is calibrated for exposure + ACES (like the ambient default): it displays as a muted earth
tone of about (95, 86, 74) at the default exposure. The pre-M3 default (0.15, 0.14, 0.13) fell into the
tonemap's toe and displayed as (15, 13, 11), a black void below the horizon. A unit test pins the displayed
range and that `Sky` (the scene resource) uses the same default.

## How it draws

```mermaid
sequenceDiagram
    participant G as Game.OnRenderMainPass
    participant S as SkyEnvironment
    participant GPU
    G->>S: Draw(camera)  (first, before geometry)
    S->>S: Frame.EnsureCamera(camera) (set 0, once per frame)
    S->>GPU: Y-flipped viewport, bind set 0 (+ set 1), push SkyParams, CmdDraw(3)
    GPU->>GPU: Sky.vk.vert emits fullscreen triangle (-1,-1) (3,-1) (-1,3), z = 0
    GPU->>GPU: frag: skyRay(ndc) = normalize(mat3(invViewRotation) · unproject(ndc)) (include/sky.glsl)
```

| Fragment shader | Technique |
|---|---|
| `Sky.Procedural.vk.frag` | Linear colours. Above horizon: `mix(horizon, sky, clamp(y·sharpness))`. Below: `mix(horizon, ground, clamp(−y·sharpness))` (inverted before M3: the ground colour sat at the horizon). Sun disc: `smoothstep` on `dot(dir, sunDir)` × color × intensity. |
| `Sky.Panoramic.vk.frag` | `u = atan(z, x)/2π + 0.5`, `v = 0.5 − asin(y)/π` |
| `Sky.Cubemap.vk.frag` | `texture(samplerCube, dir)` |

## Invariants

- Draw the sky **first** in the main pass. It does not write depth.
- The sky needs an `IVulkanContext`. With any other renderer, the constructor and `Dispose` silently
  do nothing.

## Testing

The `sky-grid` render scene draws only the procedural sky and the grid; the test recomputes every pixel's sky
colour on the CPU (`SkyGridReference`: `skyRay`, gradient, sun, exposure, ACES, sRGB encode) and requires all
pixels more than 2 px from a projected grid line to match within ±3 — above the horizon, in the
horizon-to-ground gradient and in the plain ground — on every driver ([Testing](testing.md#render-tests)).
This would catch a wrong ray, NaN/Inf, a uniform/push-constant layout mismatch or an encoding difference.

## Known issues

- Cubemap faces must be square and equal-sized (checked); a Panoramic or Cubemap sky without paths throws.
- The panorama is sampled without mips (the longitude seam would select the smallest mip).

## Related docs

[Cameras & input](cameras-and-input.md) · [Coordinate conventions](coordinate-conventions.md) ·
[Color pipeline](color-pipeline.md) · [GPU resources](gpu-resources.md)
