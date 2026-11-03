# Plan: Shader files are `.mfshader`

**Status:** in progress
**Docs:** [ADR 0236](../decisions/0236-mfshader-extension.md), docs/design/shaders.md, docs/design/canvas.md
**Started:** 2026-11-03

## Goal

The engine's shader files (Godot's shading language) use `.mfshader`; nothing outside history and the legacy fallback
says `gdshader`; old `.gdshader` files still load with a warning.

## Not in scope

- The shading language itself (Godot's canvas_item/spatial subset stays).
- Editor syntax highlighting (documented as a `files.associations` line, no extension shipped).

## Steps

- [x] `git mv` every `.gdshader` file and sidecar (`.meta`, `.spvlock`, `.spv`) in the render-test host, Demo and Neon.
  Verified by: `git ls-files | grep -c gdshader` is 0, UIDs in the `.meta` files unchanged (renames only).
- [x] Code, scenes, `.mres`, justfile (`mfshader_folders`), Neon tools and docs say `.mfshader`. Verified by: `git grep -i
  gdshader` lists only ADRs, logs, the progress log, old plans and the fallback.
- [x] Fallback: `Shader.Extension`/`LegacyExtension`, `ShaderImporter` takes both, `Shader.Load` warns, `IsBuildOutput`,
  `SpirvInvariance`, `mf-shaders` handle both. Verified by: `LegacyGdshaderFilesStillLoadWithADeprecationWarning`.
- [x] ADR 0236; ADR 0113/0205 status lines point at it.
- [x] Rebuild the SPIR-V with slangc 2026.1 (`just canvas-shaders`; slangc 2026.1 crashes in `-obfuscate` on Linux, built with `GLIBC_TUNABLES=glibc.malloc.mmap_threshold=0`). The SPIR-V bytes do not depend on the name, so the committed `.spv` stay as they were and only the 19 `.spvlock` change. Verified by: `just shaders-check`.
- [x] Gates: `just build`, Release build, `just test`, `just test-render`, `just format-check`, `just shaders-check`.

## Done when

- `git grep -il gdshader` lists only history files and the fallback code, docs and test.
- All gates in `.agents/workflow.md` pass on `feature/mfshader-extension`.

## Open questions

- Keep the `.gdshader` fallback or hard-cut? Default taken: keep it, with a warning (the owner can say otherwise).
