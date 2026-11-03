# ADR 0237 — Game shaders are Slang surface shaders (`.slang`)

- **Date:** 2026-11-03
- **Status:** accepted
- **Supersedes:** the shading language of [ADR 0113](0113-canvas-shaders-godot-language.md) and
  [ADR 0205](0205-spatial-shaders.md) (their passes, bindings and built-in meanings stay), and
  [ADR 0236](0236-mfshader-extension.md) (the `.mfshader` extension)
- **Docs:** docs/design/shaders.md#surface-shaders (the G18 proposal, folded in when it shipped, with its changes),
  docs/design/canvas.md#canvas-shaders

## Context

Game shaders were Godot's shading language (`shader_type canvas_item` / `spatial`) in `.mfshader` files, translated to
Slang by two hand-written C# translators and compiled with `-allow-glsl`. The owner does not want engine files to
look like Godot's. The translators re-implemented what slangc knows, game and engine shaders used two languages and two
matrix conventions, and no editor understood the files. Raw Slang passes were ruled out: a spatial shader is ten
SPIR-V files sharing the engine's sets, inputs and invariance rules. The proposal (G18) recommended a Slang surface
contract, and the owner settled its open questions.

## Decision

- **Game shaders are `.slang` files** in Slang syntax. A file is a surface shader when its first statement is
  `import mainframe.spatial;` or `import mainframe.canvas;`. Any other `.slang` file under `Assets/` is a helper module
  shaders can `import`, never a `Shader` resource. Sidecars keep their pattern: `x.slang.vert.spv` (and each other pass),
  `x.slang.spvlock`, `x.slang.meta`, plus the new `x.slang.layout.json`.
- **The engine owns every pass.** The user writes optional `vertex(inout SurfaceVertex v[, Material m][, out Varyings
  o])` and `fragment(inout SurfaceFragment s[, Material m][, Varyings i])` (canvas: `CanvasVertex` / `CanvasFragment`)
  against the engine modules in `Assets/Shaders/include/mainframe/`. `mf-shaders` generates each pass's entry, which
  `import`s the user file as a module (so errors name the user file and line with no `#line` directives).
- **Built-ins are camelCase struct fields** (`s.albedo`, `v.vertex`, `v.instanceCustom`, `v.modelMatrix`), with the
  meanings and spaces the Godot built-ins had. Matrices are the C# values with the engine's `mul(v, M)` convention.
  Unsupported Godot built-ins are not declared, so using one is a compile error.
- **Material parameters are fields of `struct Material`.** Their attributes (`[color]`, `[range(a, b)]`,
  `[filter(Filter.Nearest)]`, `[repeat(Repeat.Disabled)]`, `[defaultTexture(DefaultTexture.Black)]`) and the struct's
  render-mode attributes (`[cull(Cull.Disabled)]`, `[blend(Blend.Add)]`, `[alpha(Alpha.Cutout)]`, `[unshaded]`, …) come
  from `slangc -reflection-json`, as do offsets, sizes and bindings. Attributes are lowercase, like Slang's own.
  Alpha is explicit: there is no inference from writes.
- **Defaults are literal field initializers** (`float sway = 0.45;`, `float4 tint = float4(0.4, 0.8, 0.3, 1);`,
  `{…}` arrays, `true`/`false`). `mf-shaders` reads only those literal forms from the `Material` struct and checks them
  against the reflected types. Any other expression is a build error.
- **Reflection is not read at run time.** The build writes `x.slang.layout.json` (uniforms, block size, render state);
  `Shader.Load` reads it with the SPIR-V. The lock's first line hashes the user file with the generated entries (what the
  runtime can check); further lines hash every file slangc's `-depfile` lists (imported modules, engine includes), which
  `mf-shaders check` verifies.
- **The Godot front end is removed in the same change**: both translators, `glsl_compat.slang`, `-allow-glsl`, and
  `.mfshader`/`.gdshader` loading. Such a file now fails to load with an error that points at porting it to Slang.
  The 19 in-repo shaders are ported by hand, with the render goldens unchanged.

## Ruled out

- Keeping `.mfshader` for Slang syntax (the proposal's recommendation): the owner chose `.slang`, in line with Slang,
  which also gives editors and slangd the language with no association.
- Godot's UPPER_CASE names, or PascalCase: the owner chose camelCase.
- `[Default("…")]` string attributes: the owner wanted field initializers.
- Initializers through Slang's C API: it reports only scalar defaults (2026.1: integers; 2026.8 adds floats), not
  vectors or arrays.
- Initializers evaluated by `slangi`: it is experimental and evaluated a vector initializer wrongly in a test.
- Initializers evaluated by an extra SPIR-V compile: a `Material` with `Sampler2D` fields can't be default-constructed
  (`use of uninitialized opaque handle`), so textures would have to leave `Material`.
- A deprecation period with both front ends: the owner chose a clean cut. Game projects outside the repository
  with Godot-syntax shaders must port them.
- `#include` of the user file (the proposal's mechanism): `import` gives the user's code its own module namespace
  now that the file is `.slang`, and diagnostics stay on the user file.

## Consequences

- One shading language and one matrix convention for engine and game shaders, and no translator to extend. Game
  shaders can `import` their own modules (the Godot path had no includes).
- `vscode` with the shader-slang extension (slangd) checks game shaders, given the engine include path.
- Literal-only defaults: `float x = 0.5 * 2;` is a build error, not a computed default.
- Godot-syntax shaders outside this repository break on the next engine version (with a clear error).
- Game code now sees Slang's language changes directly. The pinned slangc (`MinimumSlangcVersion`, CI's SHA-256 pin,
  the lock's recorded version) keeps those deliberate.
- The `.slang` extension is shared with the engine's own shader sources, which live under the engine's
  `Assets/Shaders/` and are told apart by their `*.vk.{vert,frag,comp}.slang` names, never imported as resources.

## Revisit when

- Slang's reflection API reports vector and array initializers: defaults could come from it.
- Slang changes `__AttributeUsage` (the user-attribute mechanism the engine modules use).
