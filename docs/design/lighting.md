# Lighting

## Purpose

CPU-side light descriptions plus a fixed-size uniform layout consumed by lit shaders (shapes and
Spine). The lighting model is forward Blinn-Phong with shadow attenuation. In scenes, lights are nodes
(`DirectionalLight3D`, `OmniLight3D`, `SpotLight3D`; see [Scene graph & nodes](scene-graph-and-nodes.md#servers-and-render-nodes))
that wrap these light objects and register them with their world's `LightEnvironment`
(`SceneViewport.World3D.Lights`); `WorldEnvironment.AmbientColor` sets the ambient term.

## Key types

| Type | File | Fields (defaults) |
|---|---|---|
| `Light` (abstract) | [Light.cs](../../MainframeEngine/Src/Lighting/Light.cs) | `Position`, `Color = (1,1,1)`, `Intensity = 1` |
| `DirectionalLight` | [DirectionalLight.cs](../../MainframeEngine/Src/Lighting/DirectionalLight.cs) | `Direction = normalize(-0.5,-1,-0.3)`. `Position` is used only by the gizmo. |
| `PointLight` | [PointLight.cs](../../MainframeEngine/Src/Lighting/PointLight.cs) | `Range = 10` |
| `SpotLight` | [SpotLight.cs](../../MainframeEngine/Src/Lighting/SpotLight.cs) | `Direction = -Y`, `Range = 20`, `InnerConeAngle = 15°`, `OuterConeAngle = 30°` (half-angles) |
| `LightEnvironment` | [LightEnvironment.cs](../../MainframeEngine/Src/Lighting/LightEnvironment.cs) | `AmbientColor = (0.08,0.08,0.10)` |

### `LightEnvironment`

| Limit | Value |
|---|---|
| `MaxDirectional` | 4 |
| `MaxPoint` | 16 |
| `MaxSpot` | 8 |

- `AddLight(Light)` routes the light by its runtime type. Unknown subclasses are ignored.
  `RemoveLight(Light)` removes it (light nodes do both on enter/exit); `Count` counts every light.
- Lists (`DirectionalLights`, `PointLights`, `SpotLights`) are `internal`.
- Lights beyond the limits are silently dropped when the UBO is written.
- `DrawLightGizmos(ICamera)` is `[Conditional("DEBUG")]`. It draws on the ImGui background draw list:
  point lights as a dot with range rings, spot lights as cones, and directional lights as an arrow plus
  sun icon. The directional arrow is drawn in 2D and is not camera-projected.

`LightEnvironment` owns **no GPU resources**. Each consumer writes its own copy of the lights UBO every
frame through the shared `internal LightEnvironment.WriteUbo(Span<byte>, in Vector3 cameraPosition)`
(size `LightEnvironment.UboSize`), used by `ShapeBase` and `SpineRenderer`. Unit tests pin the layout.

## Lights UBO

std140, **1200 bytes** = `48 + 4×32 + 16×32 + 8×64`. Bound as set 1, binding 0, fragment stage.

![Lights UBO layout](../images/lights-ubo-layout.svg)

| Offset | Field | Packing |
|---|---|---|
| 0 | `vec4 ambientColor` | rgb |
| 16 | `vec4 cameraPosition` | xyz |
| 32 | `ivec4 counts` | x = dir, y = point, z = spot |
| 48 | `DirLight dir[4]` | `{dir.xyz, intensity}`, `{color.xyz, pad}` |
| 176 | `PointLight point[16]` | `{pos.xyz, range}`, `{color.xyz, intensity}` |
| 688 | `SpotLight spot[8]` | `{pos.xyz, range}`, `{dir.xyz, intensity}`, `{color.xyz, cos(inner)}`, `{cos(outer), pad×3}` |

## Shading model

Implemented identically in `Shapes.vk.frag` and `SpineLit.vk.frag`:

```
result = ambient · base
       + Σ_lights  color · intensity · (diffuse + 0.3 · pow(max(N·H, 0), 32)) · base · atten · spot · shadow
```

| Term | Formula |
|---|---|
| Attenuation (point/spot) | `clamp(1 − d / range, 0, 1)²` |
| Spot cone | `clamp((cosθ − cosOuter) / max(cosInner − cosOuter, 1e-4), 0, 1)` |
| Shadow | see [Shadow system](shadow-system.md#sampling-in-the-main-pass) |

All lighting runs in gamma (non-linear) space. There is no tonemapping (see
[Coordinate conventions](coordinate-conventions.md#color-space)).

## Usage

```csharp
// Scene tree: a light node drives a DirectionalLight from its transform (direction = global -Z).
var sun = new DirectionalLight3D { Position = new(0, 5, 0), Energy = 0.9f };
sun.LookAt(sun.Position + Vector3.Normalize(new(0, -0.5f, -1)));
Root.AddChild(sun);   // registered in Root.World3D.Lights; synced after process when it moves

// Tree-less: a LightEnvironment by hand.
var lights = new LightEnvironment();
lights.AddLight(new DirectionalLight { Direction = Vector3.Normalize(new(0, -0.5f, -1)), Intensity = 0.9f });
node.Draw(camera, lights);                                   // main pass
shadowSystem.RenderShadows(lights, draw2D, drawPoint);       // shadow pass
```

## Invariants

- The `MAX_*_LIGHTS` defines in both lit shaders must equal the C# limits.
- The UBO layout must match the shader `LightsUBO` struct byte for byte.

## Known issues

- Each drawable uploads its own 1200 B copy of the lights UBO per frame.
- Lights over the limit are dropped silently, with no warning.
- No linear-space lighting or HDR.
- Directional gizmo arrows are not projected through the camera.

## Related docs

[Shadow system](shadow-system.md) · [Shaders](shaders.md) ·
[Future: materials & meshes](future/materials-and-meshes.md) · [Future: color pipeline](future/color-pipeline.md)
