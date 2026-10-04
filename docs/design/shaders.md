# Shaders

## Purpose

Inventory of every GLSL file in [`MainframeEngine/Content/Shaders`](../../MainframeEngine/Content/Shaders),
which C# class loads it, its interface (inputs, sets, bindings, push constants), and the constants
that must stay in sync with C#.

## Workflow

- Vulkan shaders use the `*.vk.vert` / `*.vk.frag` naming. Compile each one by hand:
  `glslc X.vk.frag -o X.vk.frag.spv`. The `.spv` files are committed and copied to the output via
  `Content/**`.
- C# loads the `.spv` with `File.ReadAllBytes("Content/Shaders/...")`, relative to the working directory.
- Entry point is always `main`.

```mermaid
flowchart LR
    A["edit *.vk.frag"] --> B["glslc → *.vk.frag.spv"] --> C["commit both"] --> D["dotnet build copies Content/**"] --> E["File.ReadAllBytes at pipeline creation"]
```

## In use

| Shader | Loaded by | Inputs | Sets / bindings | Push constants |
|---|---|---|---|---|
| `Shapes/Shapes.vk.vert` | `ShapeBase` (`Box3d`, `Quad`) | 0 `vec3 pos`, 1 `vec2 uv` (unused), 2 `vec3 normal` | s0 b0 `ViewProjection{view, projection}` | `mat4 model; vec4 color` (80 B) |
| `Shapes/Shapes.vk.frag` | same | world pos, normal | s1 b0 lights · s2 b0 shadow matrices, b1 `sampler2DShadow[4]`, b2 `sampler2DShadow[7]`, b3 `samplerCube[4]` | same block |
| `Spine/SpineLit.vk.vert` | `SpineRenderer` | 0 `vec3`, 1 `vec2`, 2 `vec4` | s0 b0 `ViewProjection` | `mat4 model; vec4 worldNormal` |
| `Spine/SpineLit.vk.frag` | same | uv, color, world pos, normal | s1 lights · s2 shadows (as Shapes) · s3 b0 `sampler2D uTexture` | — |
| `Shadows/Shadow2D.vk.vert/.frag` | `ShadowSystem` | 0 `vec3` | s0 b0 `LightVP{mat4}` | `mat4 model` (64 B). The frag shader is empty (depth only). |
| `Shadows/ShadowPoint.vk.vert/.frag` | `ShadowSystem` | 0 `vec3` | s0 b0 `LightVP{mat4}` | `mat4 model; vec4 lightPosRange` (80 B). The frag shader writes linear `gl_FragDepth`. |
| `Sky/Sky.vk.vert` | `SkyEnvironment` | `gl_VertexIndex` | — | — |
| `Sky/Sky.Procedural.vk.frag` | same | NDC xy | s0 b0 `SkyUbo` | — |
| `Sky/Sky.Panoramic.vk.frag` | same | NDC xy | s0 b0 `SkyUbo`, s1 b0 `sampler2D` | — |
| `Sky/Sky.Cubemap.vk.frag` | same | NDC xy | s0 b0 `SkyUbo`, s1 b0 `samplerCube` | — |
| `SceneGrid/SceneGrid.vk.vert/.frag` | `SceneGrid` | 0 `vec3`, 1 `vec4` | s0 b0 `ViewProjection` | — |
| `ImGui/ImGui.vk.vert/.frag` | `VulkanImGuiController` | 0 `vec2`, 1 `vec2`, 2 `vec4` | s0 b0 `sampler2D fontSampler` | `vec2 scale; vec2 translate` |

## Compiled but not loaded

| Shader | Note |
|---|---|
| `Shapes/Quad.vk.*` | Flat-color quad. `Quad` now uses `Shapes.vk.*`. |
| `Spine/Spine.vk.*` | Unlit Spine (s0 VP, s1 `sampler2D`, push `model`) |

## Legacy OpenGL (`#version 330 core`, never loaded)

`Shapes/Shapes.{vert,frag}`, `Shapes/Quad.{vert,frag}`, `SceneGrid/SceneGrid.{vert,frag}`,
`Spine/Spine.{vert,frag}` (the multi-texture blend the README describes).

## Constants that must match C#

| Define (in `Shapes.vk.frag` and `SpineLit.vk.frag`) | Value | C# source |
|---|---|---|
| `MAX_DIR_LIGHTS` | 4 | `LightEnvironment.MaxDirectional` |
| `MAX_POINT_LIGHTS` | 16 | `LightEnvironment.MaxPoint` |
| `MAX_SPOT_LIGHTS` | 8 | `LightEnvironment.MaxSpot` |
| `MAX_SHADOW_DIR` | 4 | `ShadowSystem.MaxShadowDir` |
| `MAX_SHADOW_SPOT` | 7 | `ShadowSystem.MaxShadowSpot` |
| `MAX_SHADOW_POINT` | 4 | `ShadowSystem.MaxShadowPoint` |

The UBO layouts (`LightsUBO` 1200 B, `SkyUbo` 224 B, `ShadowMatricesUBO` 704 B) must match the C#
writers byte for byte. See [Lighting](lighting.md#lights-ubo), [Sky](sky.md#skyubo-std140-224-b) and
[Shadow system](shadow-system.md#main-pass-descriptor-set-set-2).

## Known issues

- **Double depth remap:** `Shapes.vk.vert:26`, `SpineLit.vk.vert:25` and `SceneGrid.vk.vert:15` apply
  `z = z·0.5 + w·0.5` on top of System.Numerics projections that already output [0,1]. See
  [Coordinate conventions](coordinate-conventions.md#depth).
- Dynamic indexing of sampler arrays without enabling `shaderSampledImageArrayDynamicIndexing`.
- No build-time compilation: `.spv` files can drift from their source.
- Unused and legacy files add noise: 8 GL files, plus `Quad.vk.*` and `Spine.vk.*`.

## Related docs

[Build & platforms](build-and-platforms.md) · [Future: asset & shader pipeline](future/asset-and-shader-pipeline.md)
