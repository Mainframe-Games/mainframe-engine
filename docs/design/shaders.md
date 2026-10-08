# Shaders

## Purpose

Inventory of every shader in [`MainframeEngine/Content/Shaders`](../../MainframeEngine/Content/Shaders),
which C# class loads it, its interface (inputs, sets, bindings, push constants), and how shaders are
built, shared and kept in sync with C#. Shaders are written in **Slang** ([ADR 0144](../../memory/decisions/0144-slang-shader-language.md));
the rules for writing them are under [Writing shaders](#writing-shaders).

## Workflow

Shaders compile as part of `dotnet build` ([ADR 0007](../../memory/decisions/0007-build-time-shaders-and-shared-limits.md)).

```mermaid
flowchart LR
    J["limits.json"] -->|GenerateShaderLimits| L["include/limits.slang<br/>Src/Rendering/Generated/ShaderLimits.g.cs"]
    A["*.vk.vert.slang / *.vk.frag.slang<br/>+ include/*.slang"] -->|"CompileShaders (incremental)<br/>slangc -target spirv -capability spirv_1_5<br/>-matrix-layout-row-major -I include"| S["obj/&lt;config&gt;/Shaders/**.spv"]
    L --> S
    S -->|copied| O["bin/…/Content/Shaders/**.spv"]
    C["committed *.spv<br/>(just shaders)"] -.->|"no slangc: warning MFSHADER001"| O
    O --> R["ShaderModuleCache.Get(&quot;Shaders/…spv&quot;)<br/>(ContentPaths)"]
```

- One stage per file: `X.vk.vert.slang` / `X.vk.frag.slang` / `X.vk.comp.slang`, entry point `main`
  (`[shader("vertex")]` etc.), compiled to `X.vk.vert.spv` and so on — the names C# loads. The stage comes from the
  file name. The targets live in [`build/Shaders.targets`](../../build/Shaders.targets), imported by
  `MainframeEngine.csproj`.
- `slangc` is `$(Slangc)`, else `$(VULKAN_SDK)/bin/slangc` (the LunarG Vulkan SDK ships it next to the validation
  layers), else `slangc` on `PATH`. Without it the build warns and copies the committed `.spv` files;
  `-p:CompileShaders=false` forces that (CI does: its build runners have no slangc and build with `-warnaserror`).
  CI's shaders job installs Slang **v2026.1** from its GitHub release (pinned by SHA-256). An older `slangc` (below
  `SlangMinimumVersion`, 2026.1) is treated like a missing one: warning `MFSHADER001` and the committed `.spv`.
  `shaders.lock` records the `slangc` that built the committed `.spv` (`# slangc …`); `just shaders` with a different one
  says so, since the bytes may change (CI checks the lock against the sources, not the bytes). Slang is a build tool
  only: nothing of it ships.
- Shader sources, includes and `shaders.lock` are not copied to the output; only `.spv` files are.
- **After editing a shader** run `just shaders` (recompiles the committed `.spv` with the same flags
  and rewrites `shaders.lock`) and commit the `.spv` files with the lock. `just shaders-check` (and CI)
  fail when a source, an include or a `.spv` no longer matches the lock.
- C# loads modules through `IVulkanContext.Shaders` (`ShaderModuleCache`): one module per file, shared
  by every pipeline. Pipelines go through `IVulkanContext.Pipelines` (the persisted pipeline cache).

## Shared includes

`#include "x.slang"` (resolved through `-I include`; every include has `#pragma once`). The includes are textual
headers, not Slang modules, because they are configured by macros defined before including them (`MATERIAL_SET`,
`LIGHTS_SET`, `SHADOW_SET`, `CANVAS_LIGHT_SET`).

| Include | Contents | Override macros |
|---|---|---|
| `limits.slang` | **Generated** from `limits.json` — `MAX_DIR_LIGHTS`, `MAX_POINT_LIGHTS`, `MAX_SPOT_LIGHTS`, `MAX_SHADOW_DIR/SPOT/POINT`, `MAX_SHADOW_CASCADES`, `MAX_SHADOW_ATLAS_MAPS` | — |
| `common.slang` | `PI`, `srgbToLinear`, `linearToSrgb`, `glsl_mod`, `textureSize2D`; includes `limits.slang` | — |
| `frame.slang` | Set 0 binding 0 `FrameData` (camera, viewport, near/far, time, exposure), `linearizeDepth` | — |
| `shadows.slang` | Shadow set (`ShadowUBO`, cascade array, atlas, point cubes; all comparison samplers), receiver bias, PCF kernels, `dirShadow/spotShadow/pointShadow`, `shadowCascadeTint` (includes `frame.slang`) | `SHADOW_SET` (default 1) |
| `lights.slang` | Lights UBO, Blinn-Phong per light, `shadeLightsBlinnPhong(base, N, Ngeo, worldPos, specular, shininess)` (`Ngeo`: geometric normal for the shadow normal offset) and `shadeLights` (0.3, 32); `counts.w = 1` skips shadow maps (offscreen views) (needs `shadows.slang` first) | `LIGHTS_SET`/`LIGHTS_BINDING` (default 0/1) |
| `material.slang` | `StandardMaterial3D` set 2 (`MATERIAL_SET`; the cutout shadow casters use 1) (parameters UBO, one sampler, albedo/normal/emission images), `materialUv/Albedo/Emission`, `materialNormal` (derivative tangent frame) | — |
| `sky.slang` | Sky push constants (`SkyParams`), `skyRay(ndc)` | — |

## Descriptor frequency model

| Set | Frequency | Contents | Owner |
|---|---|---|---|
| 0 | per frame | b0 `FrameData` (368 B), b1 lights UBO (1200 B) | `FrameContext` (`IVulkanContext.Frame`) |
| 1 | per frame | shadows (`shadows.slang`) | `ShadowSystem` or the renderer's fallback |
| 2 | per material | `StandardMaterial3D` (`material.slang`); other drawers' textures (sky, Spine) | `MaterialGpu`, the drawer |
| binding 1 (vertex input) | per instance | mesh instances: model matrix + object id (`MeshInstanceData`) | `MeshRenderer` instance buffer |
| push | per draw | 128-byte vertex+fragment range: model matrix (+ extras) for non-batched drawers | the drawer |

`FrameContext.Begin(camera, lights)` writes set 0 once per frame; renderer-owned drawers (sky, grid,
Spine) call `EnsureCamera`/`EnsureLights` with the camera they were given, which write only if nothing
has this frame. Pipeline layouts made with `FrameContext.CreatePipelineLayout` share set 0 (and set 1
when they take shadows) and the push range, so they are compatible. Every frame *view* (main view, offscreen
`SubViewport`s) has its own set 0 copy (`FrameContext.SetView`). Meshes follow the model through the
[mesh renderer](materials-and-meshes.md#descriptor-sets).

## In use

Each row's source is the name plus `.slang` (`Mesh/Mesh.vk.vert.slang` → `Mesh/Mesh.vk.vert.spv`).

| Shader | Loaded by | Inputs | Sets / bindings | Push constants |
|---|---|---|---|---|
| `Mesh/Mesh.vk.vert` | `MeshRenderer` (lit pipelines) | 0 `vec3 pos`, 1 `vec3 normal`, 2 `vec2 uv`; instance 3–6 model rows | s0 frame | — |
| `Mesh/MeshId.vk.vert` | `MeshRenderer` (`MeshObjectId`) | 0 pos, 2 uv; instance 3–6 model rows, 7 `uint objectId` (outputs uv + id only) | s0 frame | — |
| `Mesh/Mesh.vk.frag` | `MeshRenderer` (`MeshLit`) | world pos, normal, uv | s0 frame + lights · s1 shadows · s2 material | — ; specialization 0 `kAlphaMode` |
| `Mesh/MeshId.vk.frag` | `MeshRenderer` (`MeshObjectId`) | uv, id | s2 material (cutout) | — ; specialization 0 `kAlphaMode`; writes `uint` |
| `Mesh/MeshOutline.vk.vert` | `MeshRenderer` (`MeshOutline`) | same as `Mesh.vk.vert` | s0 frame · s2 b0 material (`flags.w` = outline width bits; the binding is visible to the vertex stage) | — |
| `Shadows/Shadow2DInstanced.vk.vert`, `ShadowPointInstanced.vk.vert` | `ShadowSystem` (instanced casters) | 0 `vec3`; instance 1–4 model rows | s0 b0 `LightVP` (dynamic offset) | point: `lightPosRange` at offset 64 |
| `Spine/SpineLit.vk.vert` | `SpineRenderer` | 0 `vec3`, 1 `vec2`, 2 `vec4` | s0 frame | `mat4 model; vec4 worldNormal` |
| `Spine/SpineLit.vk.frag` | same | uv, tint, world pos, normal | s0 frame + lights · s1 shadows · s2 b0 `Sampler2D` | — ; specialization 0 `kPremultipliedTexture` |
| `Shadows/Shadow2D.vk.vert/.frag` | `ShadowSystem` | 0 `vec3` | s0 b0 `LightVP{mat4}` (dynamic offset) | `mat4 model` (64 B). The frag shader is empty (depth only). |
| `Shadows/ShadowPoint.vk.vert/.frag` | `ShadowSystem` | 0 `vec3` | s0 b0 `LightVP{mat4}` (dynamic offset) | `mat4 model; vec4 lightPosRange` (80 B). The frag shader writes linear depth (`SV_Depth`). |
| `Sky/Sky.vk.vert` | `SkyEnvironment` | `SV_VulkanVertexID` | — | — |
| `Sky/Sky.Procedural.vk.frag` | same | NDC xy | s0 frame | `SkyParams` (96 B) |
| `Sky/Sky.Panoramic.vk.frag` | same | NDC xy | s0 frame, s1 b0 `Sampler2D` (sRGB) | `SkyParams` |
| `Sky/Sky.Cubemap.vk.frag` | same | NDC xy | s0 frame, s1 b0 `SamplerCube` (sRGB) | `SkyParams` |
| `SceneGrid/SceneGrid.vk.vert/.frag` | `SceneGrid` | 0 `vec3`, 1 `vec4`, 2 `vec3` (other end of the line; the vertex shader clips the line) | s0 frame | — |
| `Post/Fullscreen.vk.vert` | tonemap pass | `SV_VulkanVertexID` | — | — |
| `Post/Tonemap.vk.frag` | `VulkanRenderer` | `SV_Position` | s0 b0 `Sampler2D` HDR scene | `float exposure; uint encodeSrgb` (8 B) |
| `Post/TonemapPost.vk.frag` | `VulkanRenderer` (non-default `PostProcessSettings`, ADR 0124) | `SV_Position` | s0 b0 `Sampler2D` HDR scene, b1 `sampler2D[7]` glow levels | exposure, encodeSrgb, tonemapper, glow mode/enabled/intensity, white, whiteTonemapped, 7 weights (60 B) |
| `Post/GlowBlur.vk.frag` | `GlowEffect` (ADR 0124) | `SV_Position` | s0 b0 `Sampler2D` source (scene, temp or previous level) | dstSize, flags (horizontal, first), strength, exposure, threshold, scale, bloom, luminanceCap (36 B) |
| `Gizmos/ScreenGizmo.vk.vert/.frag` | `ScreenGizmosRenderer` ([Developer overlay](dev-overlay.md#screen-gizmos)) | 0 `vec2` (pixels), 1 `vec4` (sRGB, straight alpha) | — | `vec2 scale; vec2 translate`; specialization 0 `kLinearizeColors` |

The legacy OpenGL shaders and the unused `Quad.vk.*`/`Spine.vk.*` were deleted in M3, and `Shapes/Shapes.vk.*`
with `ShapeBase` (replaced by the mesh shaders).

## Writing shaders

Slang is HLSL-like. These rules keep the shaders matching the C# side (they were checked against the former GLSL
shaders' SPIR-V when the engine moved to Slang):

- **Matrices are the C# values.** Every shader compiles with `-matrix-layout-row-major`, so a `float4x4` in a buffer
  or push constant holds exactly the bytes of the uploaded `System.Numerics.Matrix4x4`, and **`mul(v, M)`** transforms
  like `Vector4.Transform(v, M)`. Chain transforms left to right: world → clip is
  `mul(mul(float4(p, 1.0), model), frame.viewProjection)`. A matrix built in a shader lists the four C# rows:
  `float4x4(inModel0, inModel1, inModel2, inModel3)`; `m[i]` is row `i`; `(float3x3)m` is the upper 3×3.
- **Built-ins:** `SV_VulkanVertexID` / `SV_VulkanInstanceID` (not `SV_VertexID` / `SV_InstanceID`, which subtract the
  base vertex/instance), `SV_Position` (also the fragment coordinate), `SV_Depth`, `SV_IsFrontFace`, `SV_Target0`.
- **Interfaces:** stage inputs and outputs carry `[[vk::location(n)]]`; integer varyings are `nointerpolation`.
  Resources carry `[[vk::binding(binding, set)]]`; push constants are a `[[vk::push_constant]] ConstantBuffer<T>`
  (`[[vk::offset(n)]]` on a member reproduces a range that starts past 0); specialization constants are
  `[vk::constant_id(n)] const T k = default;`. Uniform buffers are std140 and push constants std430 by default.
- **Textures:** combined `Sampler2D`/`SamplerCube`/`Sampler2DShadow`/`Sampler2DArrayShadow`/`SamplerCubeShadow` with
  `Sample`, `SampleLevel`, `SampleGrad`, `SampleCmp` (implicit LOD) and `Load`; separate `Texture2D` + `SamplerState`
  for the material set.
- **Maths:** `lerp`, `frac`, `atan2(y, x)`, `rsqrt`, `ddx`/`ddy`; `glsl_mod` (from `common.slang`) for GLSL's `mod` —
  never `fmod`, which rounds towards zero.
- **A stage writes only what the next one reads.** Slang drops every unread stage input from the SPIR-V (no option
  keeps them), and the validation layer warns about a vertex output no fragment input reads, and about a vertex
  attribute the vertex shader does not read. So a vertex shader shared by several fragment shaders outputs only what
  they all read (`Post/Fullscreen.vk.vert` outputs nothing but the position), the object-ID pass has its own
  `Mesh/MeshId.vk.vert`, and each mesh pipeline passes only the attributes its vertex shader reads
  (`VertexLayouts.MeshAttributes` / `MeshIdAttributes`). Unused *resources* are dropped too; that needs nothing (the
  pipeline layouts come from C#).
- **No Metal keywords as names** (`vertex`, `fragment`, `kernel`, `constant`, `device`, …): Slang keeps local names in
  the SPIR-V, and MoltenVK's SPIRV-Cross passes them into the Metal source, which then fails to compile.
- Canvas shaders (`.gdshader`) are the exception: their Godot code is wrapped in a Slang template compiled with
  `-allow-glsl` ([Canvas](canvas.md#canvas-shaders)).

## Constants that must match C#

Light and shadow limits come from [`limits.json`](../../MainframeEngine/Content/Shaders/limits.json)
(edit only there): `LightEnvironment.MaxDirectional/MaxPoint/MaxSpot` and
`ShadowSystem.MaxShadowDir/MaxShadowSpot/MaxShadowPoint/MaxCascades` are defined from the generated `ShaderLimits`;
`ShaderLimitsTests` fails if the generated files drift or a shader re-`#define`s a limit (`limits.json` names the shader
macro `shader`).

The UBO and push layouts (`LightsUBO` 1200 B, `FrameData` 368 B, `SkyParams` 96 B, `ShadowUBO`
1680 B) must match the C# writers byte for byte. See [Lighting](lighting.md#lights-ubo),
[Sky](sky.md) and [Shadow system](shadow-system.md#shadowubo).

## Known issues

- `.spv` files are committed as the no-SDK fallback, so a shader change touches both the source and
  the binary (enforced by `shaders.lock`).

## Related docs

[Build & platforms](build-and-platforms.md) · [GPU resources](gpu-resources.md) ·
[Color pipeline](color-pipeline.md) · [Vulkan renderer](vulkan-renderer.md)
