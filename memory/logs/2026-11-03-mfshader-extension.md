---
summary: >-
  Shader files are .mfshader instead of .gdshader (ADR 0236): every file and sidecar renamed, code and docs updated,
  old .gdshader still loads with a warning. slangc 2026.1 crashes in -obfuscate on Linux; worked around with a glibc tunable.
---

# 2026-11-03: Shader files are `.mfshader`

Branch `feature/mfshader-extension`. Plan: [Shader files are `.mfshader`](../plans/2026-11-03-mfshader-extension.md).

## What changed

- **Renames.** The 19 shaders in the render-test host, the Demo and the Neon were renamed with `git mv`, along with
  every sidecar: the `.meta` files (UIDs unchanged), the `.spvlock` files and all the `.spv`. That's 230 files.
- **Engine.**
  - `Shader.Extension` is `.mfshader` and `Shader.LegacyExtension` is `.gdshader`. `ShaderImporter` takes both, and
    `Shader.Load` warns on the old extension.
  - `AssetDatabase.IsBuildOutput` and `SpirvInvariance.UserShaderSuffixes` know both sidecar names.
  - The temp file name is now `mf-shader-<guid>`.
- **mf-shaders** builds and checks both extensions and warns on each old file. The justfile variable is now
  `mfshader_folders`.
- **References.** `shaders.mscene`, `spatial_dissolve.mres`, the Demo, Neon and render-test code, the Neon Blender
  tools, and the Neon README and NOTICE.
- **Docs.**
  - canvas.md, shaders.md, materials-and-meshes.md, demo.md, neon.md and testing.md.
  - shaders.md has a new section on the `.mfshader` extension: the rename steps and a VS Code `files.associations`
    line for highlighting.
- **ADRs.** The new ADR 0236 records the change; the status lines of ADRs 0113 and 0205 point at it.

## Decisions

- **The extension is `.mfshader`.** The brief said `.mshader`; the owner changed it to `.mfshader` mid-task. See
  [ADR 0236](../decisions/0236-mfshader-extension.md).
- **Old `.gdshader` files load, with a deprecation warning.** The owner didn't answer the fallback question, so this
  is the cheap default. Dropping it is a few lines.
- **The committed SPIR-V bytes are unchanged.**
  - A shader built here under both names gives identical files, so the name does not reach the SPIR-V. Only the
    `.spvlock` hashes change, because they cover the `#line` directives.
  - Rebuilt on Linux, some colour-pass `.frag.spv` came out different from the committed ones (probably built on
    macOS). I kept the committed bytes so the goldens stay put.

## Dead ends

- **`just canvas-shaders` segfaulted on every spatial shader** (exit 139, sometimes SIGBUS).
  - gdb shows a use-after-free in slangc 2026.1's `-obfuscate` handling of `#line` source maps:
    `ankerl::unordered_dense::wyhash::hash` ← `SourceMap::getSourceFileIndex` ← `obfuscateModuleLocs`.
  - It depends on heap layout: the same input crashes for one temp-file name or working directory and passes for
    another. The old `mf-gdshader-…` names crash too, so the rename didn't cause it. It is deterministic, so
    `Compile`'s retry can't help.
  - Workarounds: `GLIBC_TUNABLES=glibc.malloc.mmap_threshold=0` (used here), or `-g0`.
- **Fresh worktrees have no `Plugins/Spine` submodule**, so the build fails on Spine types. Run
  `git submodule update --init Plugins/Spine` first.

## Verified

- `just build` and the Release `-warnaserror` build: 0 errors.
- `just test`: engine 3194 passed (3 skipped), editor 1805 (5 skipped), video 460.
- `just test-render`: 222 passed.
- `format-check` and `shaders-check` (151 engine shaders locked, 19 user shaders up to date): ok.
- Demo tests: 113 passed. Neon tests: 214 passed.
- `git grep -i gdshader` lists only history (ADRs, logs, progress log, old plans), the fallback code and its test,
  ADR 0236, and the shaders.md note.

## Not verified

- The Demo and Neon weren't run by hand. Their tests load the scenes, and the render tests draw the renamed shaders.
- The VS Code `"gdshader"` language id comes from godot-tools and hasn't been checked in an editor.
- macOS: not built there.

## Next

- Work around or report the slangc `-obfuscate` crash. Options: retry once with `GLIBC_TUNABLES`, pass `-g0`, or
  report it upstream.
- Drop `Shader.LegacyExtension` once no game project has `.gdshader` files.
