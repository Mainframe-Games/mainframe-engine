# ADR 0113 — Canvas shaders in Godot's shading language, translated to GLSL at build time

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM1 / E5 (the game's `docs/porting.md`)

## Context

The game has seven `canvas_item` shaders (ground, map fog, silhouette, four intro/menu effects) using Godot's built-ins,
uniform hints and defaults, varyings and `vertex()`. The engine compiled only its own GLSL at build time; materials were
3D-only.

## Decisions

1. **Keep the Godot language.** `CanvasShaderCompiler` translates a `.gdshader` (shader_type canvas_item) to Vulkan GLSL:
   uniforms → one std140 block (set 1, binding 0, declaration order) + samplers (bindings 1…n, filter/repeat hints),
   defaults parsed, `render_mode` (blend_*, unshaded, light_only), varyings, helper functions, `vertex()`/`fragment()`
   dropped from the other stage, Godot's built-ins as globals/macros set by a template mirroring canvas.glsl (COLOR =
   vertex colour × texture before `fragment()`, the canvas modulation after it unless unshaded). The game's shaders stay
   byte-identical to the Godot ones. `light()` waits for 2D lights (E6).
2. **SPIR-V is built ahead of time and committed**, like the engine's shaders: `mf-shaders build <folders>` (and
   `CanvasShaderBuild.Build`) writes `x.gdshader.vert.spv`/`.frag.spv` and `x.gdshader.spvlock` (sha256 of the translated
   GLSL) next to the source; `mf-shaders check` fails on stale ones; loading warns. No runtime shader compiler dependency.
3. **`source_color` uniforms are not converted** (Godot's canvas uses the sRGB uniform set when 2D is not HDR).
4. **Materials:** `ShaderMaterial.SetShaderParameter` by name (float/int/bool/vectors, arrays, `Texture2D`); per frame,
   each material's block is written once into a host-visible ring (dynamic UBO offset), sets per material are rebuilt
   when its textures change; pipelines per (shader, blend, primitive).

## Consequences

- All seven game shaders translate and compile with glslc unchanged.
- Shader authors need glslc (Vulkan SDK) only to change a shader; everyone else uses the committed SPIR-V.

## Amended by ADR 0144 (2026-10-08)

The translator wraps the Godot code in a Slang template compiled by `slangc` with `-allow-glsl` (the user code keeps
GLSL syntax and meaning); `CanvasShaderProgram` exposes `VertexSource`/`FragmentSource`. The fragment stage compiles
first and the vertex stage writes only the inputs it kept (`SpirvInputs`), because Slang drops unread fragment inputs.
