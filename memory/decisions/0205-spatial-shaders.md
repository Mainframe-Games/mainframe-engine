# ADR 0205 — Spatial shaders: ShaderMaterial with shader_type spatial on 3D meshes

- **Date:** 2026-10-25
- **Status:** accepted
- **Milestone:** — (Crash Site Defense's move to 3D, mainframe-engine#121; game issue Crash-Site-Defense#9)
- **Docs:** docs/design/shaders.md#spatial-shaders, docs/design/materials-and-meshes.md#shadermaterial-spatial-shaders-adr-0205

## Context

Custom shaders existed only for the canvas (ADR 0113, 0144): `CanvasShaderCompiler` rejected anything but
`shader_type canvas_item`, and the mesh renderer drew only its built-in materials. Crash Site Defense moved to 3D and needs
a few Godot spatial shaders: its procedural ground (world-space albedo from a biome map, noise and hashes, lit and
shadowed), later a hit flash, wind sway and a dissolve. Until then it bakes each ground chunk to a texture on the CPU.

## Decision

1. **Same language, same path.** `SpatialShaderCompiler` translates a Godot 4.7 `shader_type spatial` subset to Slang,
   wrapping the user's code (uniforms with hints and defaults, varyings, constants, structs, helpers, `vertex()`,
   `fragment()`) in a template compiled with `-allow-glsl`, like canvas shaders. Godot's built-ins are template globals
   (fragment: ALBEDO, ALPHA, ALPHA_SCISSOR_THRESHOLD, EMISSION, ROUGHNESS, METALLIC, SPECULAR, AO, NORMAL, NORMAL_MAP(_DEPTH),
   LIGHT_VERTEX, UV, UV2, COLOR, VERTEX, VIEW, FRAGCOORD, FRONT_FACING, SCREEN_UV, TIME, the matrices,
   CAMERA_POSITION_WORLD…; vertex: VERTEX, NORMAL, UV, COLOR, CUSTOM0, INSTANCE_ID, INSTANCE_CUSTOM, VERTEX_ID, the
   matrices…), with Godot's spaces: VERTEX and NORMAL in model space in `vertex()` (world with `world_vertex_coords`) and in
   view space in `fragment()`. The rest of Godot's outputs (RIM, CLEARCOAT, …) are declared and ignored.
2. **One SPIR-V pair per pass**, built ahead of time and committed next to the source by `mf-shaders`
   (`just canvas-shaders`), with one `.spvlock` over all of them: the colour pass (`x.gdshader.{vert,frag}.spv`), the
   depth prepass with velocity (`.depth`, `vertex()` run for this frame and the last), the directional/spot and point
   shadow casters (`.shadow`, `.shadow-point`) and the object-ID pass (`.id`). Every pass runs `vertex()` and `fragment()`,
   so displacement, `discard` and the alpha scissor agree in the image, its shadows, TAA/SSAO's prepass and picking. As for
   canvas shaders: no runtime compiler, the fragment stage first and the vertex stage writing only what it kept, `-obfuscate`;
   `#line` directives make slangc's errors name the `.gdshader` and its line, and the translator's own errors do too.
3. **Lighting is the engine's**: PBR (`shadeLightsPbr`: ALBEDO, METALLIC, ROUGHNESS, AO, the mapped normal; Godot's
   default model) or Blinn-Phong with Godot 3's `specular_blinn`/`specular_phong` (Crash Site Defense's other props are
   Blinn-Phong), sky lighting, SSAO, every light with its shadows, then EMISSION and fog. No `light()`.
4. **Godot's alpha rule**: cut out (`AlphaMode.Cutout`) when the fragment code uses `ALPHA_SCISSOR_THRESHOLD`, blended when
   it writes `ALPHA` or the blend mode is not `blend_mix`, else opaque. Render modes: `blend_mix/add/premul_alpha`,
   `cull_back/front/disabled`, `depth_draw_opaque/always/never` (`MaterialRenderState.DepthDraw`), `depth_test_disabled`,
   `unshaded`, `world_vertex_coords`, `fog_disabled`, `specular_blinn/phong`; the default Lambert/Schlick-GGX names are
   accepted. Others are an error.
5. **In the mesh renderer** a spatial shader is a user material (`MeshRenderer.Spatial.cs`, like the terrain splat
   partial): each shader gets its own set-2 layout (b0 the std140 uniform block, b1…n combined image samplers, both stages)
   and a pipeline layout sharing sets 0 and 1 and the push range, plus caster layouts with that set as set 1. Its
   pipelines live in the shared `PipelineStateCache` under the new `PipelineKey.UserShader` (a dense per-shader id) and the
   shader sets `MeshSpatial`, `MeshDepthSpatial`, `MeshObjectIdSpatial`; casters are cached per shader. The vertex
   attributes of each pipeline are those its compiled vertex stage reads (`SpirvInputs`). The colour and prepass
   vertex stages get an invariant position (`SpirvInvariance`).
6. **At most 4 samplers per shader**: the mesh fragment stage already binds 12 samplers (sky lighting, SSAO, probes,
   shadows) of MoltenVK's 16. `source_color` samplers read the sRGB view, others linear; `filter_*`/`repeat_*` hints
   override the texture's sampling; unset samplers read white (`hint_default_black`/`_transparent`, `hint_normal`).
   `source_color` vectors convert from sRGB to linear (Godot's 3D is linear; canvas shaders keep them raw, ADR 0113).
7. **Values** come from `ShaderMaterial.SetShaderParameter` and from `.mres`/`.mscene`: the new exported
   `ShaderMaterial.ShaderParameters` (`ShaderParameter` records: numbers, vectors, arrays, resources).
8. **MultiMesh custom data** (Godot's `use_custom_data`): `UseCustomData`, `CustomData`, `Set/GetInstanceCustomData`. The
   80-byte `MeshInstanceData` keeps its size: INSTANCE_CUSTOM fills 8 of its 12 padding bytes as four halves, INSTANCE_ID
   (the multimesh index) the last 4.
9. **Next passes and overlays** of spatial shaders draw with a small depth bias towards the camera: their vertex module
   reaches the surface's position by other instructions than the module that drew it, a rounding step apart, which
   `LESS_OR_EQUAL` alone rejected.

## Consequences

- `MeshInstance3D`, `MultiMeshInstance3D`, `MaterialOverride`, surface materials, `MaterialOverlay` and `NextPass` take a
  spatial `ShaderMaterial`; a shader without SPIR-V draws with the default material and a warning.
- Render tests `spatial-shaders` and `spatial-shaders-taa` (procedural ground, swaying stalks and their shadows, a dissolve
  cutout from a `.mres`, a rim next pass and overlay; picks; an allocation gate).
- `just shaders-check` also checks committed `.gdshader` SPIR-V (`gdshader_folders` in the justfile).
- Not yet: instance and global uniforms, `light()`, POSITION/DEPTH writes, TANGENT/BINORMAL (no vertex tangents),
  UV2 (meshes have one UV set; UV2 = UV), CUSTOM1–3, a skinning variant (#119's compute pre-skinning feeds the same
  vertex buffer), the preprocessor.
