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
| `Light` (abstract) | [Light.cs](../../MainframeEngine/Src/Lighting/Light.cs) | `Position`, `Color = (1,1,1)` (sRGB; `LinearColor` is converted when set), `Intensity = 1`; shadows: `CastsShadows = true`, `ShadowResolution` (per type), `ShadowBias = 0.5`, `ShadowNormalBias = 1.5` (texels), `ShadowOpacity = 1` (Godot's `shadow_opacity`, ADR 0123) |
| `DirectionalLight` | [DirectionalLight.cs](../../MainframeEngine/Src/Lighting/DirectionalLight.cs) | `Direction = normalize(-0.5,-1,-0.3)`. `Position` is used only by the gizmo. `ShadowResolution = 2048` (per cascade), `CascadeCount = 4`, `CascadeSplitLambda = 0.75`, `MaxShadowDistance = 100`, `CascadeBlend = 0.1` |
| `PointLight` | [PointLight.cs](../../MainframeEngine/Src/Lighting/PointLight.cs) | `Range = 10`, `ShadowResolution = 512` (cube face) |
| `SpotLight` | [SpotLight.cs](../../MainframeEngine/Src/Lighting/SpotLight.cs) | `Direction = -Y`, `Range = 20`, `InnerConeAngle = 15°`, `OuterConeAngle = 30°` (half-angles), `ShadowResolution = 1024` (atlas tile) |
| `LightEnvironment` | [LightEnvironment.cs](../../MainframeEngine/Src/Lighting/LightEnvironment.cs) | `AmbientColor = DefaultAmbientColor = (0.22,0.22,0.25)` (sRGB; ≈ 0.04 linear) |

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
- Light gizmos are `LightGizmos.Draw(ScreenGizmoBatch, camera, viewport, lights, scale)`
  (`Rendering/Gizmos/`), queued by `RenderServer.RenderMain` for the root viewport while
  `RenderServer.ShowLightGizmos` is on: point lights as a dot with range rings, spot lights as cones, and
  directional lights as an arrow (projected along the light direction) plus sun icon. Points behind the camera are
  skipped.

`LightEnvironment` owns **no GPU resources**. The lights UBO is written through the shared
`internal LightEnvironment.WriteUbo(Span<byte>, in Vector3 cameraPosition)` (size `LightEnvironment.UboSize`):
once per frame (and view) into the shared set 0 by `FrameContext`, read by every lit pipeline (meshes, Spine).
Unit tests pin the layout.

## Lights UBO

std140, **1200 bytes** = `48 + 4×32 + 16×32 + 8×64`. Colours are written **linear** (`Light.LinearColor`,
ambient converted when set). Spine and future scene pipelines read it from the per-frame shared set 0,
binding 1 (`FrameContext`, written once per frame); shapes still bind their own copy as set 1, binding 0.
Shaders get the struct and the shading loop from `include/lights.glsl`.

![Lights UBO layout](../images/lights-ubo-layout.svg)

| Offset | Field | Packing |
|---|---|---|
| 0 | `vec4 ambientColor` | rgb |
| 16 | `vec4 cameraPosition` | xyz |
| 32 | `ivec4 counts` | x = dir, y = point, z = spot |
| 48 | `DirLight dir[4]` | `{dir.xyz, intensity}`, `{color.xyz, shadowOpacity}` |
| 176 | `PointLight point[16]` | `{pos.xyz, range}`, `{color.xyz, intensity}` |
| 688 | `SpotLight spot[8]` | `{pos.xyz, range}`, `{dir.xyz, intensity}`, `{color.xyz, cos(inner)}`, `{cos(outer), shadowOpacity, pad×2}` |

## Shading model

`lights.glsl` (`shadeLightsBlinnPhong(base, N, Ngeo, worldPos, specular, shininess)`; `Ngeo` is the geometric normal the shadow lookups offset along), used by `Mesh.vk.frag` with the material's specular strength and
shininess and by `SpineLit.vk.frag` through `shadeLights` (strength 0.3, exponent 32):

```
result = ambient · base
       + Σ_lights  color · intensity · (diffuse + 0.3 · pow(max(N·H, 0), 32)) · base · atten · spot · shadow
```

| Term | Formula |
|---|---|
| Attenuation (point/spot) | `clamp(1 − d / range, 0, 1)²` |
| Spot cone | `clamp((cosθ − cosOuter) / max(cosInner − cosOuter, 1e-4), 0, 1)` |
| Shadow | the light's shadow code picks its map; see [Shadow system](shadow-system.md#sampling-and-filtering) |

All lighting runs in linear space into the HDR scene target; the tonemap pass (exposure + ACES) maps it
to the display, so overlapping lights no longer clip (see [Color pipeline](color-pipeline.md)).

## Shadows

Every light casts shadows unless `CastsShadows` is false. The shadow system decides which map each light gets:

- the first shadowed directional light: cascades (a light that is hidden and shown again is appended to the end of its
  list, so re-showing a directional light can change which one gets the cascades);
- other directional and spot lights: atlas tiles;
- the first four shadowed point lights: cubes.

The lights UBO is unchanged; the shadow set's codes map each light index to its map. The light nodes export the
settings (`CastsShadows`, `ShadowResolution`, `ShadowBias`, `ShadowNormalBias`, `ShadowOpacity`; on `DirectionalLight3D` also
`ShadowCascades`, `ShadowSplitLambda`, `ShadowMaxDistance`, `ShadowCascadeBlend`). Scenes save them when they differ
from the defaults. `ShadowOpacity` (Godot's `shadow_opacity`, [ADR 0123](../../memory/decisions/0123-godot-shadow-opacity.md))
lightens the shadow: `lights.glsl` uses `mix(1, shadow, opacity)` for directional and spot lights. See
[Shadow system](shadow-system.md).

## Usage

```csharp
// Scene tree: a light node drives a DirectionalLight from its transform (direction = global -Z).
var sun = new DirectionalLight3D { Position = new(0, 5, 0), Energy = 0.9f };
sun.LookAt(sun.Position + Vector3.Normalize(new(0, -0.5f, -1)));
Root.AddChild(sun);   // registered in Root.World3D.Lights while visible; synced after process when it moves

// Tree-less: a LightEnvironment by hand.
var lights = new LightEnvironment();
lights.AddLight(new DirectionalLight { Direction = Vector3.Normalize(new(0, -0.5f, -1)), Intensity = 0.9f });
node.Draw(camera, lights);                                   // main pass
shadowSystem.RenderShadows(lights, draw2D, drawPoint);       // shadow pass (no camera fit, no culling)
```

## Invariants

- The light limits come from `Content/Shaders/limits.json` (generated `ShaderLimits` / `include/limits.glsl`).
- The UBO layout must match the shader `LightsUBO` struct byte for byte.

## Known issues

- Lights over the limit are dropped silently, with no warning.
- Point lights ignore `ShadowOpacity` (their UBO entry has no free slot; ADR 0123).
- Directional gizmo arrows are not projected through the camera.

## Related docs

[Shadow system](shadow-system.md) · [Shaders](shaders.md) ·
[Materials & meshes](materials-and-meshes.md) · [Color pipeline](color-pipeline.md)
