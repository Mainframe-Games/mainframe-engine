# Proposal: G18 — Slang surface shaders

**Status:** draft (2026-11-03) · **Milestone:** [Gameplay toolkit](../../milestones.md#gameplay-toolkit-) (G18) ·
**Builds on:** [Shaders](../shaders.md) ([ADR 0144](../../../memory/decisions/0144-slang-shader-language.md)),
[Spatial shaders](../shaders.md#spatial-shaders) ([ADR 0205](../../../memory/decisions/0205-spatial-shaders.md)),
[Canvas shaders](../canvas.md#canvas-shaders) ([ADR 0113](../../../memory/decisions/0113-canvas-shaders-godot-language.md)),
[ADR 0236](../../../memory/decisions/0236-mfshader-extension.md) (`.mfshader`, in review as PR #156)

## Problem

Game shaders are `.mfshader` files in Godot's shading language (`shader_type canvas_item` / `spatial`).
`CanvasShaderCompiler` and `SpatialShaderCompiler` translate them by hand. They split the file into statements with
regexes, parse `uniform` hints, `varying`s and `render_mode`, and paste the user's GLSL into a Slang template per pass,
compiled with `-allow-glsl`. That costs four things:

- **The files read as Godot's.** The owner does not want engine files to look like Godot's. ADR 0236 changed the
  extension, but the language is still Godot's.
- **Two languages.** Engine shaders are Slang, with row-major `mul(v, M)` and the C# matrices. Game shaders are GLSL
  through `-allow-glsl`, with `M * v` and Godot's column-major built-ins. Anyone writing both switches conventions, and
  code can't move between them.
- **A hand-written front end.** About 1 500 lines of C# (the two translators) re-implement what slangc already knows:
  uniform types, std140 layout, varyings and their locations. Every Godot feature not yet supported (the preprocessor,
  includes, `instance uniform`, `light()`) is more translator. Errors in user code depend on `#line` directives, and
  slangc 2026.1 crashes in `-obfuscate` while handling those source maps on Linux
  ([log](../../../memory/logs/2026-11-03-mfshader-extension.md)). The build works around it with a glibc tunable.
- **No tooling.** No editor understands the Godot subset as the engine reads it. Godot's plugins flag the engine's
  differences, and nothing can complete the engine's built-ins.

Raw Slang passes are not the answer either. A spatial shader is ten SPIR-V files: colour, depth prepass, two shadow
casters and object IDs, a vertex and a fragment stage each. They share descriptor sets, vertex inputs, the
invariant-position rule and the "write only what the fragment reads" rule. Asking game code to write them would
hand every engine change to every game.

## Goals

- Game shaders written in **Slang syntax**: entry functions such as `void fragment(inout SurfaceFragment s, …)` and
  `void vertex(inout SurfaceVertex v, …)` written against an engine module (`import mainframe.spatial;` /
  `import mainframe.canvas;`). The built-ins are struct fields (`s.ALBEDO`, `v.VERTEX`, `s.UV`, `v.TIME`,
  `v.INSTANCE_CUSTOM`).
- **The engine still owns every pass**: colour, depth, shadow, shadow-point and object-ID for spatial shaders, and
  vertex and fragment for canvas shaders. Game code never writes a full pass, a descriptor layout, a binding or a
  location.
- **Uniform metadata read from slangc reflection** (`-reflection-json`) through Slang user attributes (`[Color]`,
  `[Range]`, `[Filter]`, `[Repeat]`, …), instead of parsed by a hand-written translator.
- **Render modes as typed attributes** that slangc checks, so a typo is a compile error at the right line.
- Errors name the user's file and line with **no `#line` directives**.
- Editor support from the **shader-slang VS Code extension and the slangd LSP**: diagnostics, completion and hover on
  the engine's built-ins.
- **Godot-syntax `.mfshader` keeps working** through a deprecation period. All 19 in-repo shaders are migrated, and the
  render goldens and the 0-B allocation gate stay as they are.

## Non-goals

- Raw passes, custom descriptor sets or custom vertex layouts from game code.
- New shader features: `light()`, `instance uniform`, `global uniform`, POSITION/DEPTH writes, TANGENT, more than 4
  textures. The Slang surface makes several cheaper later (see [Later](#later)), but this proposal is about parity.
- An automatic Godot → Slang converter. Nineteen shaders are migrated by hand (see [Migration](#coexistence-and-migration)).
  A converter is another translator, which is what this removes.
- Runtime compilation. SPIR-V stays prebuilt and committed. Slang stays a build tool, and nothing of it ships.
- Removing the Godot front end. That is a later ADR, after the deprecation period ([Open questions](#open-questions)).
- How game projects outside the engine checkout build shaders. It is unchanged: today `mf-shaders` runs from the
  checkout.

## Design

### What a game shader looks like

```slang
// Assets/Shaders/sway.mfshader
import mainframe.spatial;

[Cull(Cull.Disabled), Specular(Specular.BlinnPhong)]
struct Material
{
    [Color, Default("0.85, 0.80, 0.70")] float3 stalk_color;
    [Default("0.45"), Range(0, 2, 0.01)] float sway;
    [Default("1.3")] float speed;
};

struct Varyings
{
    float3 tint;
};

void vertex(inout SurfaceVertex v, Material m, out Varyings o)
{
    float3 world = mul(float4(v.VERTEX, 1.0), v.MODEL_MATRIX).xyz; // the engine's row-major convention
    float bend = (1.0 - v.UV.y) * (1.0 - v.UV.y);
    float phase = v.INSTANCE_CUSTOM.x * TAU + world.x * 0.4 + world.z * 0.3;
    v.VERTEX.x += sin(v.TIME * m.speed + phase) * m.sway * bend;
    v.VERTEX.z += cos(v.TIME * m.speed * 0.7 + phase) * m.sway * 0.5 * bend;
    o.tint = v.INSTANCE_CUSTOM.yzw;
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    float cap = smoothstep(0.25, 0.15, s.UV.y);
    s.ALBEDO = lerp(m.stalk_color, i.tint, cap);
    s.ROUGHNESS = 0.6;
}
```

That is the render-test `spatial_sway.mfshader` after migration. The file is plain Slang and type-checks on its own:
slangd, given the engine's include folder, finds `mainframe.spatial` and needs nothing generated.

**The contract**, all declared in the engine's `mainframe` modules:

| Piece | Rule |
|---|---|
| `import mainframe.spatial;` / `import mainframe.canvas;` | The first statement. It says the file is Slang and which kind of shader it is. |
| `struct Material` | Optional. Its fields are the material parameters (uniforms and textures). Its attributes are the render modes. Without it, the material is empty with default render modes. |
| `struct Varyings` | Optional. The values passed from `vertex` to `fragment`. Integer fields carry `nointerpolation`, as in any Slang shader. |
| `void vertex(inout SurfaceVertex v[, Material m][, out Varyings o])` | Optional. Without it, the engine's own vertex transform runs. |
| `void fragment(inout SurfaceFragment s[, Material m][, Varyings i])` | Optional. Without it, the surface is white (canvas: the texture × the vertex colour, as today). |
| Canvas | `CanvasVertex` / `CanvasFragment`, the same optional parameters. |
| Anything else | Helper functions, `static const`s, structs and `import`s of the project's own `.slang` modules are free. Global shader parameters are an error ([Uniforms](#uniform-metadata)). |

The optional parameters are found by one signature match per entry function (`void vertex(` / `void fragment(` and
their parameter types). slangc checks everything else. This is the only text the build reads apart from the first
statement.

**Built-ins** keep Godot's names and meanings ([Spatial shaders](../shaders.md#spatial-shaders), [Canvas
shaders](../canvas.md#canvas-shaders)): the same inputs, outputs and spaces (VERTEX is view space in `fragment`, and so
on). Two things change:

- **Matrices are the C# values.** Built-in matrices follow the engine's row-major `mul(v, M)` convention
  ([Writing shaders](../shaders.md#writing-shaders)), like every other Slang shader in the engine. Godot's
  `MODEL_MATRIX * vec4(VERTEX, 1.0)` becomes `mul(float4(v.VERTEX, 1.0), v.MODEL_MATRIX)`.
- **Built-in textures are fields too.** Canvas `s.TEXTURE`, for example, is a `Sampler2D` field; Slang legalizes
  resource fields of structs.

The `mainframe` modules also export PI, TAU and E, and the helpers the templates already use (`srgbToLinear`,
`glsl_mod` as `mf_mod`, the normal-map helper). Their `///` comments are what slangd shows on hover.

Calling the user's code as functions also simplifies the templates. The depth prepass calls `vertex()` twice (this
frame and last frame, with last frame's model matrix and `TIME`) by passing two `SurfaceVertex` values. Today it pastes
the body twice and juggles globals.

### File format: still `.mfshader`

Both syntaxes stay `.mfshader` (ADR 0236). The **first statement** says which one a file uses:

| First statement (after comments) | Syntax | Front end |
|---|---|---|
| `shader_type canvas_item;` / `shader_type spatial;` | Godot (deprecated) | `CanvasShaderCompiler` / `SpatialShaderCompiler`, unchanged |
| `import mainframe.canvas;` / `import mainframe.spatial;` | Slang | `SlangSurfaceShader` (new) |
| anything else | error: ``x.mfshader(1): a .mfshader starts with `shader_type …;` (Godot syntax) or `import mainframe.spatial;` / `import mainframe.canvas;` (Slang)`` | |

Both statements are already required by their languages, so detection adds nothing for the user to write. The UIDs, the
`.meta` files and the sidecar names (`x.mfshader.*.spv`, `x.mfshader.spvlock`) are unchanged, so migrating a shader
means changing its contents, never renaming it.

A new extension (`.mfslang`) was considered. The one thing it buys is editor association by extension. While Godot and
Slang `.mfshader` files coexist, one `files.associations` entry can't highlight both. A per-folder glob covers that
period, and once Godot syntax goes, `.mfshader` is simply Slang. A second extension would mean two importers for one
resource type and a second rename right after ADR 0236. See [Open questions](#open-questions).

The user file is compiled by **textual `#include`**, not `import`. Slang's `import "x"` appends `.slang` to the path,
while `#include "x.mfshader"` takes any extension. The preprocessor tracks the included file itself, so slangc's errors
name it with no `#line` directives (checked with slangc 2026.1):

```
sway.mfshader(21): error 30027: 'ALBEDOX' is not a member of 'SurfaceFragment'.
```

`#include` means the user's globals share a namespace with the generated entry. The generated code keeps its own names
under the reserved `mf_` prefix, as `gd_` is reserved today.

### How a pass is built

For each pass (spatial: 5; canvas: 1), `mf-shaders` writes a small entry file and compiles it with the engine's
flags, `-obfuscate` and `-reflection-json`. This is the fragment stage of the colour pass:

```slang
import mainframe.spatial;
import mainframe.passes.color;         // engine: set 0/1 declarations, lighting, fog (static Slang, not generated)
#include "sway.mfshader"
[[vk::binding(0, 2)]] ConstantBuffer<Material> mf_material;   // set 1 in the shadow casters
struct mf_In { float4 pos : SV_Position; [[vk::location(0)]] float3 viewPos; /* … */ [[vk::location(12)]] Varyings user; };
[shader("fragment")]
float4 main(mf_In i, bool front : SV_IsFrontFace) : SV_Target0
{
    SurfaceFragment s = mf_surface_fragment(i.viewPos, /* … */ front);
    fragment(s, mf_material, i.user);      // only the parameters the user declared
    return mf_shade(s);                    // lights, shadows, fog: engine code
}
```

- **What is generated** is only this glue: which parameters the entry functions take, the `Varyings` member at
  location 12 (`LocFirstVarying`), and the vertex stage's outputs.
- **What is static Slang** is everything else, moved out of the C# string builders: the per-pass declarations,
  lighting and the built-in setup. It goes into `Assets/Shaders/include/mainframe/` as modules. The Godot front end
  keeps its own templates until it goes.
- **The fragment stage compiles first.** Its kept inputs (`SpirvInputs`) and its reflection (each kept `Varyings`
  field's name, type and location) then generate a vertex stage that writes only those locations, as today.

The material layout is Slang's. A spike with slangc 2026.1 checked that `ConstantBuffer<Material>` with
`Sampler2D` fields at `[[vk::binding(0, 2)]]` lays out the way the engine already binds a material. The ordinary
fields form one std140 block at binding 0, in declaration order, and the textures follow at bindings 1…n in the same set.
`nointerpolation` fields become `Flat`, and `spirv-val` passes. The pipeline layouts in C# (`MeshRenderer`, the canvas
renderer) therefore don't change.

### Uniform metadata

Material parameters are fields of `struct Material`, with attributes the `mainframe` module declares. slangc lists them
under `userAttribs` in `-reflection-json`, with each field's offset, size, stride and binding, so nothing parses
declarations:

| Godot hint | Slang attribute on the field | `ShaderUniform` |
|---|---|---|
| `source_color` (vector) | `[Color]` | `SourceColor`: linearized for spatial, raw for canvas, as today |
| `source_color` (sampler) | `[Color]` | sampled as sRGB |
| `hint_range(a, b, s)` | `[Range(a, b, s)]` | new `Range` (stored for the inspector; ignored today) |
| `= value` | `[Default("0.4, 0.8, 0.3, 1")]` | `Default` (the existing number-list parser; count checked against the type) |
| `filter_nearest*` / `filter_linear*` | `[Filter(Filter.Nearest)]` / `Filter.Linear` | `Filter` |
| `repeat_enable` / `repeat_disable` | `[Repeat(Repeat.Enabled)]` / `Repeat.Disabled` | `Repeat` |
| `hint_default_black` / `_transparent` / `hint_normal` | `[DefaultTexture(DefaultTexture.Black)]` / `.Transparent` / `.Normal` | `TextureHint` |
| `group_uniforms` | `[Group("Wind")]` | new `Group` (inspector only) |

- **Types.** Reflection types map to `ShaderUniformType`: `bool`, `int`, `uint`, `float`, `floatN`, `intN`,
  `float2x2`/`3x3`/`4x4`, `Sampler2D`, and 1-D arrays of the non-texture ones. Anything else is a build error naming
  the file and `Material.field`: nested structs, `half`, `float3x2`, separate `Texture2D`, texture arrays, or more than
  4 spatial textures (the MoltenVK sampler budget).
- **Global parameters are an error.** A `uniform` or a `Sampler2D` declared outside `Material` shows up in reflection
  as a parameter that isn't `mf_material` or engine-owned: "material parameters go in `struct Material`". Slang would
  otherwise auto-bind it into set 0, which belongs to the frame.
- **Defaults are a string.** Slang attributes have a fixed number of arguments (a 4-float attribute rejects
  `[Default(0.5)]`), and field initializers (`float sway = 0.45;`) are not in reflection. A string covers every type,
  and the build checks it against the field's type. The alternative is reading initializers through Slang's C API
  ([Open questions](#open-questions)). A field initializer inside `Material` is a build error (a one-struct scan), so
  it can't be silently ignored.

**Reflection is not read at run time.** `mf-shaders` reduces it to the engine's own `x.mfshader.layout.json`. That file
holds the uniforms (as `ShaderUniform` serializes them), the block size, the render state and whether `vertex` exists.
It is committed with the SPIR-V and covered by the lock. `Shader.Load` for a Slang file reads the layout file and the
SPIR-V and translates nothing. The Godot path translates at load as today. `AssetDatabase.IsBuildOutput`,
`SpirvInvariance` and game export learn the new sidecar. Slang's JSON format is read in one place, at build time; if it
changes, one class and its fixtures change.

### Render modes

Render modes are **attributes on `struct Material`**, typed by the module's enums (`Cull.Disabled`, `Blend.Add`), so
slangc rejects a typo at its line (`'Disabld' is not a member of 'typeof(Cull)'`). They sit on the material because
reflection reports a struct's attributes (as integers) but not a function's, and because render modes are material
state: they are part of the pipeline key.

| Godot `render_mode` | Slang |
|---|---|
| `blend_mix` / `blend_add` / `blend_premul_alpha` (canvas: also `sub`, `mul`, `disabled`) | `[Blend(Blend.Mix)]` … |
| `cull_back` / `cull_front` / `cull_disabled` | `[Cull(Cull.Back)]` … |
| `depth_draw_opaque` / `_always` / `_never`; `depth_test_disabled` | `[DepthDraw(DepthDraw.Opaque)]` …; `[NoDepthTest]` |
| `unshaded`; canvas `light_only` | `[Unshaded]`; `[LightOnly]` |
| `world_vertex_coords`; `fog_disabled` | `[WorldVertexCoords]`; `[NoFog]` |
| `specular_blinn` / `specular_phong` | `[Specular(Specular.BlinnPhong)]` (PBR is the default; the Lambert/Burley/GGX names were no-ops and go) |
| Godot's alpha rule (writing `ALPHA` → blend; `ALPHA_SCISSOR_THRESHOLD` → cutout) | `[Alpha(Alpha.Blend)]` / `[Alpha(Alpha.Cutout)]`; a non-mix `Blend` implies `Alpha.Blend`, as today |

Alpha becomes explicit because reflection can't see which fields the code writes. Today the alpha mode comes from a
regex over the fragment code. The build warns when the file mentions `ALPHA` but the material is opaque, which is the
one way the explicit form can surprise someone.

### Coexistence and migration

**Godot syntax keeps working, unchanged**, through a deprecation period. Its translators, templates, tests and render
coverage stay. The two front ends produce the same runtime types (`ShaderUniform`, `MaterialRenderState`, the SPIR-V
file set), so `ShaderMaterial`, `MeshRenderer`, the canvas renderer, `.mres` files and saved scenes can't tell them
apart. Deprecation comes in steps:

1. Once all in-repo shaders are migrated (P3), `mf-shaders build` prints one notice per Godot-syntax file. There is no
   runtime warning: players' logs shouldn't carry it.
2. A release later, `Shader.Load` warns once per file, like `.gdshader` today.
3. A later ADR removes the Godot front end, `-allow-glsl` and `glsl_compat.slang`, when no known game project has
   Godot-syntax files.

**The 19 in-repo shaders** are migrated by hand, in the same files (same UIDs):

| Where | Shaders | Lines |
|---|---|---|
| `Tests/MainframeEngine.RenderTests.Host/Assets/Shaders` | `spatial_dissolve`, `spatial_ground`, `spatial_rim`, `spatial_screen`, `spatial_sway`, `wave` (canvas) | 149 |
| `Examples/Demo/Assets/Shaders` | `dissolve`, `ground`, `sway` | 98 |
| `Examples/Neon/Assets/Shaders` | `beam`, `crowd`, `facade`, `hologram`, `rack`, `screen`, `sign`, `skyline`, `spill`, `street` | 1 074 |

The rules for each file:

- `uniform` → a `Material` field with attributes, `varying` → a `Varyings` field, `render_mode` → `Material`
  attributes.
- `vecN`/`ivecN`/`matN` → `floatN`/`intN`/`floatNxN`; `mix` → `lerp`, `fract` → `frac`, `mod` → `mf_mod`;
  `texture(s, uv)` → `s.Sample(uv)`; `textureLod` → `SampleLevel`.
- Swap the operands of every matrix product, `M * v` → `mul(v, M)`. This is the one change that can be silently
  wrong. The render goldens catch it.

The render-test shaders go first, and **keep their Godot versions** as fixtures under
`Assets/Shaders/godot/`. Both versions render the same scene against the **same goldens**. That proves the Slang path
matches the Godot path pixel for pixel (within the golden tolerance), and it keeps the Godot path covered while it is
supported. The Demo and the Neon then migrate with nothing kept. Their tests and screenshots check them.

### Tooling

- **VS Code.** The shader-slang extension (`shader-slang.slang-language-extension`, slangd underneath) gives
  diagnostics, completion, hover and go-to-definition, given two settings: `"files.associations": { "*.mfshader":
  "slang" }` and a search path to the engine's `include` folder (`slang.additionalSearchPaths`; P0 confirms the exact
  key and whether slangd also reads a `slangdconfig.json`). shaders.md documents both, replacing the godot-tools line
  from ADR 0236. While both syntaxes coexist, a folder glob keeps Godot files on the Godot mode.
- **Games.** The engine package ships `include/mainframe/*.slang` as content. The `mfgame` template writes
  `.vscode/settings.json` with the association and the path. The path to a package's content moves with each version,
  so the build copies the modules to a stable `<project>/.mainframe/shaders/` (git-ignored) folder
  ([Open questions](#open-questions)).
- **Other editors.** Any editor with an LSP client can run slangd with the same search path. Rider and CLion
  highlight `.slang`.
- **`mf-shaders translate x.mfshader [pass]`** prints a Slang file's generated entry, as it prints a Godot file's
  template today.

### The slangc `-obfuscate` crash

The gdb trace from ADR 0236's work (`wyhash::hash` ← `SourceMap::getSourceFileIndex` ← `obfuscateModuleLocs`) is a
use-after-free in the source maps that `#line` directives create. The Slang path writes no `#line`. Its entry files
`#include` the user file, and the preprocessor's own source locations name it. So the Slang path no longer has the
input that crashed.

That is not proof. The crash depends on heap layout, and the spike reproduced neither path: 60 runs each, with and
without `#line`, under random directories, all passed. So:

- **P0** runs the migrated real shaders (the Neon's `facade` and `street` are the big ones) 200 times each, under
  varied temp names and working directories and without `GLIBC_TUNABLES`. It does the same for today's Godot path as
  the control, on the Linux machine that crashed.
- The `GLIBC_TUNABLES=glibc.malloc.mmap_threshold=0` workaround stays until the Godot front end goes, since that path
  still writes `#line`.
- The crash is reported upstream either way, with the Godot-path reproducer.

### Code layout

| File | Contents |
|---|---|
| `Assets/Shaders/include/mainframe/spatial.slang`, `canvas.slang` | The public modules: `SurfaceVertex`/`SurfaceFragment`, `CanvasVertex`/`CanvasFragment`, the attributes and their enums, PI/TAU/E, helpers. Documented with `///`. |
| `Assets/Shaders/include/mainframe/passes/*.slang` | The per-pass engine code the Godot templates build as strings today (set declarations, built-in setup, lighting, fog, casters, IDs). Internal: not imported by game code. |
| `Src/Rendering/Shaders/SlangSurfaceShader.cs` | The Slang front end: first-statement detection, signature match, per-pass entry generation, the vertex stage from the fragment's reflection. |
| `Src/Rendering/Shaders/SlangReflection.cs` | `-reflection-json` → `ShaderUniform`s, render state, the errors; the only reader of Slang's JSON |
| `Src/Rendering/Shaders/ShaderLayoutFile.cs` | `x.mfshader.layout.json`: written by the build, read by `Shader.Load` |
| `Src/Rendering/Canvas/Shader.cs`, `CanvasShaderBuild` | Dispatch on the syntax. The lock for a Slang file covers the user file, its `-depfile` dependencies (project modules it imports) and `SlangSurfaceRevision`. |

The lock's first line is the hash of the user file and the engine revision, which is what `Shader.Load` can check
without slangc. The following lines hold each dependency's path and hash, which `mf-shaders check` (and CI) verify too.
`Shader.Load` therefore warns about a stale shader when its own file changed. `mf-shaders check` also catches a
changed imported module.

### Risks

- **Slang churn.**
  - User attributes need `[__AttributeUsage(_AttributeTargets.Var)]`, a double-underscore, nominally internal Slang
    API. It appears only in the engine's `mainframe` module; game code writes `[Color]`. If Slang renames it, the
    module changes and games don't.
  - Slang language changes (generics, modules, `import` rules) reach game code directly, where today they reach only
    the templates. Mitigated by the existing pin: `MinimumSlangcVersion` 2026.1, CI's SHA-256-pinned slangc and
    `shaders.lock` recording the slangc version. A slangc upgrade is a deliberate change that rebuilds every shader
    and runs the goldens.
- **Reflection stability.** `-reflection-json` is a tool output, not a documented schema. `SlangReflection` is its only
  reader. Its tests run over JSON captured from the pinned slangc and committed as fixtures. A schema change breaks
  those tests on the slangc upgrade, not a game's build.
- **Goldens.** The migrated shaders' SPIR-V bytes change (different code, the same maths). The goldens must not: P2
  and P3 pass with `just test-render` and `just render-tests-linux` and no `golden-update`. Any pixel difference beyond
  tolerance is a migration bug, most likely a matrix order.
- **0-B allocation gate.** Nothing per frame changes. `ShaderMaterial`, `Shader.WriteUniformBlock` and the pipelines
  see the same `ShaderUniform`s. Loading reads one small JSON file per shader at import, off the frame. The existing
  `SpatialShadersSteadyStateAllocatesNothing` and canvas gates run on the migrated shaders.
- **Validation.** "Write only what the fragment reads" now relies on reflection naming the kept `Varyings` fields.
  The render tests' validation gate catches a leftover output.
- **Two front ends for a while.** Pass setup lives once, in Slang for the new path, and still as strings for the old.
  A pass change (a new set 0 binding) is made twice until the Godot path goes. This is the cost of a deprecation
  period instead of a hard cut.
- **Metal names.** User functions are named `vertex` and `fragment`, which are Metal keywords, and that rule exists
  because Slang keeps names in the SPIR-V. `-obfuscate` (already used for user shaders) strips them; the spike's
  obfuscated SPIR-V keeps only `_std140`. P0 checks a MoltenVK pipeline build on macOS.

### Later

These are made cheaper by Slang syntax, but none of them is in this proposal:

- `light()` as a third entry function.
- `instance uniform`, as a second struct (`struct Instance`) read from the instance buffer.
- A shader library of project `.slang` modules (noise, palettes), which works from P1 through `import`.
- DXIL and WGSL targets for M11: native Slang has no `-allow-glsl` dependency.

## Scenarios

### P1 — the Slang front end

- **Kind detection.**
  - Given a `.mfshader` whose first statement is `import mainframe.spatial;`, when `mf-shaders build` runs, then it
    writes the ten SPIR-V files, `x.mfshader.layout.json` and `x.mfshader.spvlock`, and `Shader.Load` returns a shader
    with `Spatial` set and the reflected uniforms. Test: `SlangSpatialShaderBuildsEveryPass`.
  - Given `import mainframe.canvas;`, then two SPIR-V files and a canvas `Program`. Test:
    `SlangCanvasShaderBuildsBothStages`.
  - Given a file that starts with `shader_type spatial;`, then the Godot translator runs and its output is
    byte-identical to before this change. Test: the existing `SpatialShaderCompilerTests` and `CanvasShaderCompilerTests`,
    unchanged.
  - Given a file that starts with neither, then the build fails with ``x.mfshader(1): a .mfshader starts with `shader_type …;` …``.
    Test: `UnknownShaderSyntaxNamesBothForms`.
- **Uniforms.**
  - Given `struct Material { [Color, Default("0.4, 0.8, 0.3, 1")] float4 tint; float sway; float2 offsets[3]; float4x4 m; }`,
    when it is built, then its `ShaderUniform`s (offsets, sizes, strides, defaults, `SourceColor`) equal those of the
    Godot declarations `uniform vec4 tint : source_color = vec4(0.4, 0.8, 0.3, 1.0); uniform float sway; uniform vec2
    offsets[3]; uniform mat4 m;`. Test: `SlangMaterialLayoutMatchesGodotUniforms` (a theory over every supported type).
  - Given `[Filter(Filter.Nearest), Repeat(Repeat.Disabled), DefaultTexture(DefaultTexture.Black)] Sampler2D mask;`
    after two scalars, then `mask` has `Binding` 1, `Filter` Nearest, `Repeat` Disabled, `TextureHint` Black. Test:
    `SlangTextureAttributesMapToHints`.
  - Given a fifth `Sampler2D` in a spatial `Material`, then the build fails naming the file and `Material.<field>`.
    Test: `SlangSpatialMaterialHasAtMostFourTextures`.
  - Given a `float3x2`, a nested struct or a `Texture2D` field, then the build fails naming the field and the
    supported types. Test: `SlangUnsupportedFieldTypeIsAnError`.
  - Given `uniform float x;` or a global `Sampler2D` outside `Material`, then the build fails with "material
    parameters go in `struct Material`". Test: `SlangGlobalParameterIsAnError`.
  - Given `[Default("1, 2")]` on a `float3`, then the build fails naming the field and "3 values". Test:
    `SlangDefaultCountMustMatchTheType`.
  - Given `float sway = 0.45;` inside `Material`, then the build fails, pointing at `[Default("0.45")]`. Test:
    `SlangMaterialFieldInitializerIsAnError`.
- **Render modes.**
  - Given `[Cull(Cull.Disabled), Blend(Blend.Add), Unshaded, NoFog]` on `Material`, then the shader's `RenderState`,
    `BlendMode`, `Unshaded` and `FogDisabled` equal those of `render_mode cull_disabled, blend_add, unshaded,
    fog_disabled;`. Test: `SlangRenderModesMatchGodot` (a theory over every mode in both tables).
  - Given `[Cull(Cull.Disabld)]`, then slangc's error names the user file and that line. Test:
    `SlangRenderModeTypoIsACompileError`.
  - Given `[Alpha(Alpha.Cutout)]`, then `Alpha` is Cutout and the shadow casters are built cut out. Given no `Alpha`
    and `Blend.Add`, then `Alpha` is Blend. Test: `SlangAlphaModes`.
  - Given a fragment that writes `s.ALPHA` with no `Alpha` attribute, then the build warns that the material is
    opaque. Test: `SlangAlphaWriteOnAnOpaqueMaterialWarns`.
- **Entry functions and varyings.**
  - Given only `void fragment(inout SurfaceFragment s)` (no `Material`, no `Varyings`, no `vertex`), then it builds,
    `HasVertexFunction` is false and the uniform block is empty. Test: `SlangMinimalFragmentOnly`.
  - Given `struct Varyings { float3 tint; nointerpolation int id; float unused; }` where `fragment` reads `tint` and
    `id`, then `tint` and `id` sit at locations 12 and 13, `id` is `Flat`, and the vertex stage writes exactly those
    two locations. Test: `SlangVaryingsWriteOnlyWhatTheFragmentReads`.
  - Given more `Varyings` than locations 12–31 hold, then the build fails naming `Varyings`. Test:
    `SlangTooManyVaryingsIsAnError`.
- **Errors, locks and loading.**
  - Given `s.ALBEDOX = …` at line 21 of `sway.mfshader`, then the build's error starts with
    `sway.mfshader(21): error 30027:`, and no generated file contains `#line`. Test: `SlangErrorsNameTheUserFileAndLine`.
  - Given a Slang shader that imports the project's `noise.slang`, when `noise.slang` changes, then
    `mf-shaders check` reports the shader as stale, and `build` rebuilds it. Test: `SlangLockCoversImportedModules`.
  - Given a built Slang shader whose `.layout.json` was deleted, when it loads, then `Shader.Load` warns (the stale or
    missing SPIR-V warning) and the mesh draws with the default material. Test: `SlangMissingLayoutDrawsDefaultMaterial`.
  - Given a `ShaderMaterial` on a Slang shader with `SetShaderParameter("tint", new Vector4(…))`, then the material
    block bytes equal those of the same values on the Godot twin. Test: `SlangAndGodotTwinsWriteTheSameBlock`.

### P2 — the render-test shaders

- Given the render-test host's `SpatialShaderScene` with the Slang versions of its five shaders, when it renders, then
  it matches the `spatial-shaders` golden (and `spatial-shaders-taa`) with no `golden-update`. With the Godot fixtures it still matches. Test:
  `SpatialShadersDrawLitShadowedAndPickableAndMatchGolden` as a theory over `Syntax.Godot`/`Syntax.Slang`, and
  `SpatialShadersUnderTaaAreValidationCleanPickableAndMatchGolden` the same way.
- Given the `canvas` scene with the Slang `wave`, then it matches the `canvas` golden and its exact pixel checks.
  Test: `CanvasMatchesGoldenWithExactGammaSpaceColours`, as the same theory.
- Given the Slang spatial scene running steadily, then frames allocate 0 B. Test:
  `SpatialShadersSteadyStateAllocatesNothing` over both syntaxes.

### P3 — the Demo and the Neon

- Given the Demo after migration, when its tests load every scene, then each shader loads with no warning and the
  same uniforms as before. Test: the Demo's scene-load tests, plus `DemoShadersAreSlang` (every `.mfshader` under
  `Examples/` starts with `import mainframe.`).
- Given the Neon after migration, then its 214 tests pass and its screenshot tests match. Test: `just neon-test`.

### P4 — tooling and deprecation

- Given a Godot-syntax file, when `mf-shaders build` runs, then it prints one notice naming the file and this doc's
  migration section, and `Shader.Load` stays silent. Test: `GodotSyntaxBuildNotice`.
- Given `dotnet new mfgame`, then the project has `.vscode/settings.json` with the `*.mfshader` → `slang` association
  and the include path, and the template's sample shader is Slang. Test: `just template-smoke` (asserts the file and
  builds the shader).

## Testing

- **Goldens.** P2 and P3 must pass `just test-render` (moltenvk) and `just render-tests-linux` (lavapipe) with no
  `golden-update`. The Godot/Slang theory keeps both syntaxes drawing the same golden for as long as both exist.
- **Validation gate.** Every migrated pass, through the existing render tests (zero validation warnings), especially
  the vertex stage built from reflection.
- **Allocation gate.** `SpatialShadersSteadyStateAllocatesNothing` and the canvas gate over both syntaxes.
- **`just shaders-check`.** It covers `.layout.json` and the dependency lines of the lock. CI's shaders job runs it.
- **Reflection fixtures.** `SlangReflectionTests` run over `-reflection-json` captured from the pinned slangc,
  committed under `Tests/Content/Shaders/reflection/`, so they don't need slangc.
- **Crash stress (P0, by hand).** 200 builds of each big Neon shader, Slang and Godot paths, varied temp names and
  working directories, no `GLIBC_TUNABLES`, on Linux. The result goes in the P0 log.
- **SPIR-V size.** P0 compares the colour-pass SPIR-V size and instruction count, Godot vs Slang, for `facade`: the
  struct copies should optimize away.

## Human checklist

- [ ] VS Code with the shader-slang extension on a Slang `.mfshader`: diagnostics, completion on `s.`, hover docs on a
  built-in, go-to-definition into `mainframe/spatial.slang`.
- [ ] The Demo's and the Neon's shader scenes look the same before and after migration (the dissolve, the sway, the
  Neon's facade, signs, hologram, rain on the street), on macOS (MoltenVK) and Linux.
- [ ] The migrated Neon shaders read better than the Godot versions (the point of this work). If they don't, say so
  before P3 finishes.

## Task list

- [ ] **P0 — Spike** (throwaway, outside the engine; a log, no code merged).
  - [x] slangc 2026.1: `ConstantBuffer<Material>` layout (block at binding 0, textures at 1…, set 2), user attributes
    and struct attributes in `-reflection-json`, enum arguments as integers, `nointerpolation` → `Flat`, `#include` of
    a `.mfshader` with real-file diagnostics, `-obfuscate` + `spirv-val`, a typed-attribute typo as a compile error,
    `-depfile` listing the included file and modules. (Done while writing this proposal.)
  - [ ] Hand-write the five passes of `spatial_sway` in the proposed shape. Draw them in the render-test host
    against its golden.
  - [ ] The crash stress, and an upstream report.
  - [ ] slangd/VS Code settings keys; a MoltenVK pipeline build on macOS.
- [ ] **P1 — Front end.** The `mainframe` modules and pass modules, `SlangSurfaceShader`, `SlangReflection`,
  `.layout.json`, the lock's dependency lines, `Shader.Load`/`ShaderImporter`/`IsBuildOutput`/`SpirvInvariance`/export
  sidecars, `mf-shaders build|check|translate`, the P1 scenarios. An ADR (the Slang surface contract; it supersedes the
  language part of ADRs 0113 and 0205).
- [ ] **P2 — Render-test shaders.** Migrate six, keep their Godot fixtures, the syntax theory, goldens unchanged.
- [ ] **P3 — Demo and Neon.** Migrate thirteen. The Demo's and the Neon's tests, screenshots, the human check.
- [ ] **P4 — Tooling and docs.** shaders.md, canvas.md and materials-and-meshes.md rewritten around Slang (the Godot
  syntax becomes a "deprecated" section), the docs site, the `mfgame` template (sample shader, `.vscode/settings.json`,
  `.mainframe/shaders/`), the `mf-shaders` notice. AGENTS.md (project structure, the shaders paragraph).
- [ ] **Later (own ADR).** The runtime deprecation warning; then removing the Godot front end, `-allow-glsl`,
  `glsl_compat.slang` and the glibc workaround.

## Open questions

All for the owner:

1. **Extension.** Keep `.mfshader` for both syntaxes, chosen by the first statement (recommended), or give Slang its own
   extension (`.mfslang`) for association by extension?
2. **Built-in names.** Keep Godot's upper-case names as struct fields (`s.ALBEDO`, `v.INSTANCE_CUSTOM`; recommended:
   documented meanings and a mechanical migration), or engine-style names (`s.albedo` or `s.Albedo`)? The upper-case
   names are the most Godot-looking thing left.
3. **Defaults.** `[Default("…")]` strings (recommended: slangc CLI only), or field initializers read through Slang's
   C API? The C API means P/Invoking the `slang` library from `mf-shaders`, a new native dependency of the build tool.
4. **Deprecation period.** A build notice after P3, a runtime warning one release later, then removal by ADR
   (recommended), or a different schedule?
5. **Include path for games.** Copy the `mainframe` modules to `<project>/.mainframe/shaders/` on build, so editor
   settings have a stable path (recommended), or point at the NuGet package's versioned content folder?

## Related

- [Shaders](../shaders.md) · [Canvas](../canvas.md#canvas-shaders) ·
  [Materials & meshes](../materials-and-meshes.md#shadermaterial-spatial-shaders-adr-0205)
- [ADR 0113](../../../memory/decisions/0113-canvas-shaders-godot-language.md) (canvas shaders in Godot's language) ·
  [ADR 0144](../../../memory/decisions/0144-slang-shader-language.md) (Slang) ·
  [ADR 0205](../../../memory/decisions/0205-spatial-shaders.md) (spatial shaders) ·
  [ADR 0236](../../../memory/decisions/0236-mfshader-extension.md) (`.mfshader`)
- [The `.mfshader` log](../../../memory/logs/2026-11-03-mfshader-extension.md) (the slangc `-obfuscate` crash)
- [Rendering backend abstraction](rendering-backend-abstraction.md) (M11: DXIL/WGSL targets)
