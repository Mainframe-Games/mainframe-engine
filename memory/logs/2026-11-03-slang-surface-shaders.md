---
summary: >-
  G18: game shaders are Slang surface shaders (.slang, import mainframe.spatial|canvas; ADR 0237). The Godot translators,
  -allow-glsl and glsl_compat are gone; all 19 in-repo shaders ported with every lavapipe golden unchanged.
---

# 2026-11-03: Slang surface shaders (G18)

Branch `feature/g18-slang-shaders`. Plan: [Slang surface shaders](../plans/2026-11-03-slang-surface-shaders.md).
Proposal: #159 (now folded into `docs/design/shaders.md#surface-shaders`).

## What changed

- **Engine modules** in `Assets/Shaders/include/mainframe/`:
  - `spatial.slang` and `canvas.slang`: the render-mode attributes and their enums; they re-export the three below.
  - `material.slang`: the parameter attributes.
  - `math.slang`: PI, TAU, E and `mod`.
  - `surface/{spatial,canvas}.slang`: the camelCase built-in structs.
- **Front end** in `Src/Rendering/Shaders/`:
  - `SurfaceShaderSource` reads the text: the first statement, the entry functions' optional parameters, the literal
    defaults, and the `normalMap`/`lightVertex`/`alpha` scans.
  - `SurfaceShaderCompiler` writes the probe and every pass's entry. It is the old Godot templates ported, so they
    import the user module instead of pasting its code.
  - `SlangReflection` turns slangc's `-reflection-json` into uniforms, render modes and varyings, with the errors.
  - `SurfaceShaderLayout` is `x.slang.layout.json`, written by the build and read at load.
  - `SurfaceShaderBuild` runs the probe, then each pass's fragment stage, then its vertex stage. It writes the lock:
    the first line hashes the source, the layout and the entries; the other lines hash each `-depfile` dependency.
- **Programs are metadata only.** `CanvasShaderProgram` and `SpatialShaderProgram` (now a record) hold no sources.
  `Shader.Load` parses the source, reads the layout and warns when the SPIR-V is stale. Without a layout it loads with
  no parameters and draws the default material.
- **Godot syntax is rejected.** `.mfshader`/`.gdshader` files fail to load with an error that points at porting
  (`ShaderImporter` claims those extensions only to report it). mf-shaders reports them as errors.
- **Removed:** `CanvasShaderCompiler`, `SpatialShaderCompiler`, `glsl_compat.slang`, `-allow-glsl`, and #158's
  `#line` stripping and diagnostic compile.
- **Ported:** the 19 shaders (render-test host 6, Demo 3, Neon 10). Each was `git mv`'d, `.meta` included, and its
  references updated: scenes, the `.mres`, C# paths, the Neon's Blender tools, README and NOTICE.
- **Tests:**
  - New `SurfaceShaderTests` (no slangc) run over reflection fixtures captured from slangc 2026.1 in
    `Tests/MainframeEngine.Tests/Rendering/Fixtures`.
  - New `SurfaceShaderBuildTests` run with slangc.
  - The Godot tests were replaced. The Neon `ShaderTests` now check that varyings fed by `instanceCustom` are
    `nointerpolation`.
- **Docs:**
  - `shaders.md` gained a "Surface shaders" section (contract, built-ins, parameters, render modes, passes, building,
    editor support, porting from Godot, changes from the proposal).
  - canvas.md, materials-and-meshes.md, demo.md, neon.md, testing.md, AGENTS.md, the README and the site's landing
    page were updated.
  - ADRs 0113, 0205 and 0236 are marked superseded. The milestones row is ticked.
- **Engine `shaders.lock`:** the include hashes are refreshed. `canvas.slang` and `spatial.slang` changed only in
  comments and no longer include `glsl_compat.slang`. The engine's canvas SPIR-V compiles byte-identical from the old
  and new includes.

## Decisions

- The owner's answers are in [ADR 0237](../decisions/0237-slang-surface-shaders.md): `.slang`, camelCase built-ins,
  literal defaults, and removing the Godot front end now.
- Defaults are literals that mf-shaders reads itself. The owner first chose "field initializers through Slang's C
  API", but the API reports scalars only (and only integers in 2026.1). The owner then picked literals.
- Lowercase attributes (`[color]`, `[cull(Cull.Disabled)]`), like Slang's own.
- The per-pass code stays as C# string templates, ported from the Godot ones, so the maths match. Moving it into
  static Slang modules would be a separate change.

## Dead ends

- **Defaults through Slang's C API.** It has no vector or array defaults.
- **Defaults through `slangi`.** It printed `0.2, 1.6, 0.9, 4.0` for `float4(0.4, 0.8, 0.3, 1.0) * 0.5`.
- **Defaults through a SPIR-V constant-extraction compile.** A `Material` with a `Sampler2D` can't be
  default-constructed (`use of uninitialized opaque handle`).
- **`import "x.mfshader"`.** Slang appends `.slang` to the path. Moot once the extension became `.slang`.
- **`PI` was ambiguous** between `common.slang` (included by the entries) and the user-facing module. Fixed by moving
  the structs into `mainframe.surface.*`, which the entries import, and the constants into `mainframe.math`.
- **A textures-only material** (`spatial_screen`) got its texture at binding 0, because slangc drops the empty
  constant buffer. That was a validation error on the engine's set layout. Such a material is now declared at binding
  1, and the layout shifts the reflected bindings.
- **`just render-tests-linux --filter "a|b"`** loses the quotes. Call `build/linux/run.sh render --filter "…"`
  directly.

## Verified

- **Spike (slangc 2026.1, Linux).** Reflection gives the engine's existing layout (block at binding 0, textures from
  1, set 2). User attributes appear on both fields and the struct, enum arguments come through as integers, and an
  unread material is still reflected. `nointerpolation` becomes `Flat`, `-depfile` lists the imported modules, and
  errors name the user file and line.
- **Builds:** `just build` and the Release `-warnaserror -p:CompileShaders=false` build, 0 warnings.
  `just format-check` passes.
- **Unit tests** (`just test`, with slangc on PATH): engine 3235 (3 skipped), editor 1805 (5 skipped), video 460.
  This includes the five slangc build tests: a spatial shader with an imported module, a canvas shader, three error
  lines, and a stale import making the lock stale.
- **Shader gates.** `just shaders-check`: 156 engine shaders locked, 19 surface shaders current. The 52 render-test
  SPIR-V files pass `spirv-val`. `check.sh` passes. `just docs-build` passes (its one warning, about `editor.md`, was
  there before).
- **Render tests on lavapipe:**
  - Shader tests (`SpatialShader*`, `CanvasRenderTests`, `ViewportMaterial*`, 13 tests): all passed, every frame has
    a golden, and no golden changed.
  - Full suite (`build/linux/run.sh render`): 222/222. No golden changed in the diff. `auto-exposure-histogram_frame0005`
    is still without a golden, as before.
  - Baseline before any change: 222/222, with `auto-exposure-histogram_frame0005` already without a golden.
- **Host GPU** (NVIDIA RTX 3070 Ti, which has no goldens): the same 13 shader render tests are validation-clean and
  pass the 0-B allocation gate (`SpatialShadersSteadyStateAllocatesNothing`, `ViewportMaterialFramesAllocateNothing`).
- **Demo** 118 tests pass, **Neon** 215 tests pass. `just template-smoke` passes (with slangc on PATH: its
  `-warnaserror` build turns MFSHADER001 into an error without it, as before this change).
- **Screenshots before and after** (same host, old code in a temporary `origin/main` worktree):
  - The Demo's `shaders` scene is pixel-identical.
  - The Neon's n1, n2, n6 and n7 are identical. n3, n4, n5 and n8 differ only in rain and shower particles: a heat
    map shows only the streaks, and the old code against itself differs by 4.4 % on n3.
  - The Demo's `particles`, `physics_2d` and `physics_3d` differ the same way. They have no surface shaders.

## Not verified

- `moltenvk` goldens and MoltenVK pipeline creation (no Mac here). Run `just test-render` on the Mac before merging.
- slangd in an editor (the human check). The setting key `slang.additionalSearchPaths` is confirmed from the
  installed extension's `package.json`.
- The Windows build of mf-shaders (paths in the lock are written with `/`; the hash normalises line endings).

## Next

- The package does not ship `include/mainframe/*.slang`, so a game project outside a checkout has no modules for
  slangd or mf-shaders. Ship them and wire the `mfgame` template's `.vscode/settings.json` (the proposal's
  `.mainframe/shaders/` idea).
- `[range]` and `[group]` are read but stored nowhere, ready for a `ShaderMaterial` inspector.
