# Plan: Slang surface shaders (G18)

**Status:** done, see [log](../logs/2026-11-03-slang-surface-shaders.md)
**Docs:** the G18 proposal (folded into [Shaders → Surface shaders](../../docs/design/shaders.md#surface-shaders) when it shipped) ·
[ADR 0237](../decisions/0237-slang-surface-shaders.md) (the owner's decisions; they change the proposal's extension,
names, defaults and deprecation) · [Shaders](../../docs/design/shaders.md) · [Canvas](../../docs/design/canvas.md) ·
[Materials & meshes](../../docs/design/materials-and-meshes.md)
**Started:** 2026-11-03

## Goal

Game shaders are `.slang` surface shaders (ADR 0237): the 19 in-repo shaders are ported, `mf-shaders` builds them from
reflection, `Shader.Load` reads the layout sidecar, and the Godot translators, `-allow-glsl` and `glsl_compat.slang` are
gone. The `lavapipe` goldens pass unchanged, with no validation warnings and 0-B frames.

## Not in scope

- New shader features (`light()`, instance uniforms, POSITION/DEPTH, more textures).
- Moving the per-pass engine code from C# string templates into static Slang modules (the proposal's idea). The
  generated entries keep today's template code, which keeps the SPIR-V maths the same. If this is dropped for good, it
  goes in "Changes from the proposal".
- `moltenvk` golden runs and the macOS MoltenVK pipeline check (no Mac here): a human check.
- How game projects outside the checkout build shaders.

## Steps

- [x] **Baseline.** Record `just render-tests-linux` (lavapipe) passing before any change, and capture the Demo's and
  the Neon's shader screenshots on this host (NVIDIA) for a before/after diff. Verified by: the run's summary and PNGs
  in the scratchpad.
- [x] **Engine modules.** `Assets/Shaders/include/mainframe/{material,spatial,canvas}.slang`: attributes and enums,
  `SurfaceVertex`/`SurfaceFragment`/`CanvasVertex`/`CanvasFragment` (camelCase), PI/TAU/E, helpers, `///` docs.
  Verified by: a test shader importing each compiles with slangc.
- [x] **Front end.** `SurfaceShaderCompiler`: first-statement detection, the signature match, the literal-default
  reader, per-pass entry generation (spatial ×5, canvas), the vertex stage from the fragment's kept inputs and
  reflection. `SlangReflection`: JSON → `ShaderUniform`s, render state, errors. `ShaderLayoutFile`: write and read.
  Verified by: unit tests over committed reflection fixtures (no slangc), plus slangc tests (skipped without it).
- [x] **Build and load.** `CanvasShaderBuild` builds `.slang` shaders (reflection, layout, lock with the depfile
  lines), `check` verifies them, `Shader.Load`/`ShaderImporter` read the layout, `.mfshader`/`.gdshader` fail with the
  porting error, `AssetDatabase.IsBuildOutput` and `SpirvInvariance` know the new sidecars, `mf-shaders` and the
  justfile use `.slang`. Verified by: `just build`, `just test`.
- [x] **Port the render-test shaders** (5 spatial + `wave`), the `.meta` renamed with them (UIDs kept), the scene and
  `.mres` references updated. Verified by: `just render-tests-linux` with no golden changes, the validation gate and
  `SpatialShadersSteadyStateAllocatesNothing`.
- [x] **Port the Demo (3) and the Neon (10).** References in code, scenes, Blender tools, README/NOTICE. Verified by:
  the Demo's and the Neon's tests, and their screenshots diffed against the baseline (no visible change).
- [x] **Remove the Godot front end.** Delete the translators, `glsl_compat.slang`, `-allow-glsl`, the legacy extension
  code, and their tests. Rewrite the tests that used them. Verified by: `git grep -i -E 'gdshader|mfshader|allow-glsl|
  glsl_compat|shader_type'` lists only history and the porting error and its test.
- [x] **Docs.** shaders.md (a "Surface shaders" section replaces "Spatial shaders" and the `.mfshader` section, with a
  "Porting from Godot" list), canvas.md, materials-and-meshes.md, demo.md, neon.md, testing.md, AGENTS.md, the
  proposal moved into the design docs with "Changes from the proposal", the milestones row ticked, ~~the `mfgame`
  template's VS Code settings~~ (dropped: the package does not ship the modules, so there is no path to point at yet). Verified by: `check.sh` (links), `just docs-build`.
- [x] **Gates.** `just build`, the Release `-warnaserror` build, `just test`, `just render-tests-linux`,
  `just format-check`, `just shaders-check`, `check.sh`, the Demo and Neon tests, `just template-smoke`. Verified by:
  each passing, noted in the log.

## Done when

- Every `.slang` under the three asset folders starts with `import mainframe.spatial;` or `import mainframe.canvas;`,
  and no `.mfshader` or `.gdshader` file is left in the repository.
- `just render-tests-linux` passes with no golden file changed in the diff.
- `just shaders-check` passes, and `git grep allow-glsl` finds nothing outside history.
- The gates above pass, and the log lists what was not verified (moltenvk, macOS).

## Open questions

- None open: ADR 0237 records the owner's answers.
