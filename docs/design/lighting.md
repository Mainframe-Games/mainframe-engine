# Lighting

## Purpose

CPU-side light descriptions plus a fixed-size uniform layout consumed by lit shaders (meshes and
Spine). The lighting model is forward Blinn-Phong with shadow attenuation, or PBR (Cook-Torrance GGX + Lambert,
[ADR 0150](../../memory/decisions/0150-pbr-shading-and-sky-ibl.md)) for `ShadingMode.Pbr` materials, with ambient light
and reflections from the sky's image-based lighting, and distance + height fog. In scenes, lights are nodes
(`DirectionalLight3D`, `OmniLight3D`, `SpotLight3D`; see [Scene graph & nodes](scene-graph-and-nodes.md#servers-and-render-nodes))
that wrap these light objects and register them with their world's `LightEnvironment`
(`SceneViewport.World3D.Lights`); `WorldEnvironment.AmbientColor` sets the ambient term, `AmbientEnergy` scales it and
`AmbientSource` (`Color` by default, or `Sky`) picks where it comes from.

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
Shaders get the struct from `include/lights_data.slang` (no shadow set needed: the fog and the sky shaders read it) and
the shading loops from `include/lights.slang`. The render server sets `LightEnvironment.AmbientEnergy` and
`EnvironmentFlags` from the world's `WorldEnvironment` before each view writes the block.

![Lights UBO layout](../images/lights-ubo-layout.svg)

| Offset | Field | Packing |
|---|---|---|
| 0 | `vec4 ambientColor` | rgb = ambient colour × `AmbientEnergy`, w = `AmbientEnergy` (scales the sky's irradiance) |
| 16 | `vec4 cameraPosition` | xyz, w = environment flags as a float (`kEnv*` in `lights_data.slang`: 1 sky diffuse, 2 sky reflections, 4 no reflections) |
| 32 | `ivec4 counts` | x = dir, y = point, z = spot |
| 48 | `DirLight dir[4]` | `{dir.xyz, intensity}`, `{color.xyz, shadowOpacity}` |
| 176 | `PointLight point[16]` | `{pos.xyz, range}`, `{color.xyz, intensity}` |
| 688 | `SpotLight spot[8]` | `{pos.xyz, range}`, `{dir.xyz, intensity}`, `{color.xyz, cos(inner)}`, `{cos(outer), shadowOpacity, pad×2}` |

## Shading model

`lights.slang` (`shadeLightsBlinnPhong(base, N, Ngeo, worldPos, specular, shininess)`; `Ngeo` is the geometric normal the shadow lookups offset along), used by `Mesh.vk.frag` with the material's specular strength and
shininess and by `SpineLit.vk.frag` through `shadeLights` (strength 0.3, exponent 32):

```
result = ambient · base
       + Σ_lights  color · intensity · (diffuse + 0.3 · pow(max(N·H, 0), 32)) · base · atten · spot · shadow
```

`ambient` is `iblDiffuse(N)` (`include/environment.slang`): the ambient colour, or the sky's irradiance when the world's
`AmbientSource` is `Sky` and the sky has been captured. With screen-space ambient occlusion (below) `Mesh.vk.frag` uses
the overload with the AO: `ambient · base · ssao`, and every light × `ssaoDirect(ssao)`.

| Term | Formula |
|---|---|
| Attenuation (point/spot) | `clamp(1 − d / range, 0, 1)²` |
| Spot cone | `clamp((cosθ − cosOuter) / max(cosInner − cosOuter, 1e-4), 0, 1)` |
| Shadow | the light's shadow code picks its map; see [Shadow system](shadow-system.md#sampling-and-filtering) |

### PBR

`shadeLightsPbr(PbrSurface s, float3 Ngeo, float3 worldPos)` (`lights.slang`), with `PbrSurface { albedo, metallic,
roughness, ao, N }`. Every lit 3D shader of the later G8 lanes (foliage, terrain, water) calls it.

| Term | Formula |
|---|---|
| Roughness | perceptual `r` clamped to ≥ 0.045; α = r², a2 = α² |
| D (GGX) | `a2 / (π · ((N·H)² (a2 − 1) + 1)²)` |
| Visibility (Smith height-correlated) | `0.5 / (N·L √((N·V)² (1 − a2) + a2) + N·V √((N·L)² (1 − a2) + a2))` |
| F (Schlick) | `F0 + (1 − F0)(1 − V·H)⁵`, `F0 = lerp(0.04, albedo, metallic)` |
| Direct light | `(albedo (1 − metallic)(1 − F) + π · D · Vis · F) · radiance · N·L` |
| Ambient | `(iblDiffuse(N) · albedo (1 − metallic) + iblSpecular(R, r) · (F0 · A + B) · so) · ao`, (A, B) = `iblBrdf(N·V, r)`; without SSAO `ao` = `s.ao`, `so` = 1 |

Light units are the Blinn-Phong ones: Lambert's 1/π is folded into the light (a light of energy 1 lights a white diffuse
surface at normal incidence to 1), so the specular lobe carries the π. `radiance` is colour × intensity × attenuation ×
spot cone × shadow, with the same shadow lookups, shadow opacity and cascade tint as Blinn-Phong.

### Screen-space ambient occlusion

`PostProcessProfile.SsaoEnabled` (on `WorldEnvironment.PostProcess`; [ADR 0165](../../memory/decisions/0165-ssao-gtao.md); GTAO, see
[Post-processing → SSAO](post-processing.md#ssao)) puts a per-pixel AO image at set 0 binding 5; without it the binding
is a white 1×1 image. The lit shaders read it at their fragment (`ambientOcclusionAt(SV_Position)`,
`include/ambient_occlusion.slang`) and pass it to the overloads that take `ssao`:
`shadeLightsBlinnPhong(…, ssao)`, `shadeLightsPbr(s, Ngeo, worldPos, ssao)`.

| Term | Formula | Setting (Godot name) |
|---|---|---|
| Ambient (`ao` above) | `ssaoCombine(m, ssao)` = `lerp(min(m, ssao), m · ssao, t)`, `m` the material's AO (ORM red, foliage vertex AO) | `SsaoAoChannelAffect` = t (0: the darker of the two) |
| Reflections (`so` above) | `min(1, Lagarde(N·V, ssao, α) / ssao)`, Lagarde and de Rousiers 2014: smooth surfaces seen face-on lose more | — |
| Direct light | × `ssaoDirect(ssao)` = `1 + (ssao − 1) · k` on every light's shadow term | `SsaoLightAffect` = k (default 0) |

`FrameData.AmbientOcclusion` carries k and t for the main view (0 otherwise). Without SSAO the image is 1 and k, t are 0,
so every factor is exactly 1 (or `m`) and the shaders produce the frames they produced before (bit-identical goldens).
Which shaders use it: `Mesh.vk.frag` (opaque and cutout; blended surfaces are not in the depth prepass, so the AO under
them is the background's and they skip it), `Foliage.vk.frag` (also its translucency, × `ssaoDirect`),
`TerrainSplat.vk.frag`, and `Water.vk.frag` for the light scattered in the water body only (the AO under water is the
bed's). Spine and the overloads without `ssao` are unchanged.

### Image-based lighting fallbacks

The ambient helpers (`include/environment.slang`) fall back without a captured sky: `iblDiffuse` returns the ambient
colour, `iblSpecular` a uniform environment of the ambient colour (or nothing when `ReflectedLightSource` is
`Disabled`). See [Sky → Image-based lighting](sky.md#image-based-lighting) and `BrdfLut` for the split-sum table.

### Fog

`include/fog.slang`: `applyFog(color, worldPos)` for surfaces and `applySkyFog(color, dir)` for the sky, from
`FrameData.fogColor` / `fogParams` (`WorldEnvironment.Fog*`). Both return the colour unchanged while fog is off
(`fogColor.a == 0`, the default).

- Density at height y is `FogDensity · exp(−FogHeightDensity · max(y − FogHeight, 0))`: uniform below the fog height,
  thinning above it. The optical depth along the camera ray is integrated exactly (the antiderivative of the density),
  so the fog is right with the camera inside or above it. `amount = 1 − exp(−depth)`.
- Sun scatter (Godot's `fog_sun_scatter`) adds `dir[0].color · intensity · max(V · toSun, 0)⁸ · FogSunScatter` to the fog
  colour.
- **The sky** gets only height fog: the ray to infinite height through the thinning fog, so the horizon fades into the
  fog and the zenith keeps most of its colour. Uniform fog (height density 0) leaves the sky clear (it would otherwise
  turn the whole sky to fog). The sky lighting capture is unfogged.
- `Mesh.vk.frag` fogs opaque and blended surfaces after emission.

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
lightens the shadow: `lights.slang` uses `lerp(1, shadow, opacity)` for directional and spot lights. See
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

- The light limits come from `Content/Shaders/limits.json` (generated `ShaderLimits` / `include/limits.slang`).
- The UBO layout must match the shader `LightsUBO` struct byte for byte.

## Known issues

- Lights over the limit are dropped silently, with no warning.
- Point lights ignore `ShadowOpacity` (their UBO entry has no free slot; ADR 0123).
- Directional gizmo arrows are not projected through the camera.
- PBR has no `MetallicSpecular`, per-material `AoLightAffect` or multi-scattering energy compensation yet; specular
  occlusion comes only from SSAO (not from the material's AO), and there are no bent normals yet (G8e.1).
- Fog has no aerial perspective or volumetric part (froxels wait for compute, ADR 0149).

## Related docs

[Shadow system](shadow-system.md) · [Shaders](shaders.md) ·
[Materials & meshes](materials-and-meshes.md) · [Color pipeline](color-pipeline.md)
