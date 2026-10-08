# ADR 0007 — Build-time shader compilation, committed SPIR-V fallback, generated limits

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W2 lane m3a)

## Context

`.spv` files were compiled by hand; content paths were relative to the working directory; light and
shadow limits were `#define`d in two shaders and declared again in C#. Open questions: keep committing
`.spv` or require the Vulkan SDK; how to keep C# and GLSL limits in sync.

## Decisions

1. **Compile in `dotnet build`** (`build/Shaders.targets`, imported by the engine): incremental
   `CompileShaders` target (`glslc --target-env=vulkan1.2 -I Content/Shaders/include`, inputs: sources,
   includes, `limits.json`), output to `obj/<config>/Shaders`, copied to `Content/Shaders` in every
   output. glslc errors are build errors.
2. **Keep committing `.spv`** as the fallback: without glslc the build warns (`MFSHADER001`) and ships the
   committed files, so contributors without the SDK can build. `just shaders` regenerates them and
   `shaders.lock` (now also hashing `include/*.glsl`); `just shaders-check`/CI verify them. CI builds with
   `-p:CompileShaders=false` (no glslc on the runners; the warning would fail `-warnaserror`).
3. **Single source of truth for limits:** `Content/Shaders/limits.json` → an inline MSBuild task generates
   `Src/Rendering/Generated/ShaderLimits.g.cs` and `include/limits.glsl` (both committed, rewritten only
   on change). Unit tests fail on drift or hand edits. A source generator was not used: the
   `MainframeEngine.Generators` project belongs to another lane.
4. **`ContentPaths.Resolve`** is the one path API: rooted paths unchanged, `"Content/…"` relative to
   `AppContext.BaseDirectory`, anything else relative to `Content/`.

## Amended by ADR 0144 (2026-10-08)

Shaders are Slang, compiled by `slangc` (`$(Slangc)`, `$(VULKAN_SDK)/bin/slangc` or `PATH`) instead of `glslc`; the
generated header is `include/limits.slang` (the `limits.json` key is `shader`). The build-time pipeline, the
committed-`.spv` fallback (`MFSHADER001`) and `shaders.lock` are unchanged.
