# Proposal: Asset & Shader Pipeline

**Milestone:** M3 · **Status:** ⬜ planned · **Touches:** csproj files, every `"Content/..."` path, shader loading

## Problem

- `.spv` files are compiled by hand with `glslc` and committed, so they can drift from their source.
- Content paths are relative to the working directory, so launching from elsewhere fails.
- Legacy GL shaders and unused `.vk` shaders sit next to the live ones.
- The Sandbox csproj has stale content entries.

## Goals

- Shaders are compiled as part of `dotnet build` (incremental), with `#include` support and errors
  shown in build output.
- One `ContentPaths.Resolve("Shaders/Shapes/Shapes.vk.vert.spv")` rooted at `AppContext.BaseDirectory`.
- The shader folder contains only live shaders.

## Proposed design

```mermaid
flowchart LR
    G["*.vk.vert / *.vk.frag<br/>+ include/*.glsl"] -->|MSBuild target CompileShaders<br/>Inputs/Outputs incremental| S["obj/Shaders/*.spv"]
    S -->|Content item, CopyToOutput| O["bin/.../Content/Shaders/*.spv"]
    O --> R["ContentPaths.Resolve → File.ReadAllBytes"]
```

- MSBuild target in `MainframeEngine.csproj`: `<ShaderSource Include="Content/Shaders/**/*.vk.*" />` →
  `Exec Command="glslc -I Content/Shaders/include %(Identity) -o $(IntermediateOutputPath)..."`.
- If `glslc` is missing, fall back to the committed `.spv` with a build **warning**, so contributors
  without the Vulkan SDK can still build.
- Shared includes: `lights.glsl`, `shadows.glsl`. These remove the duplicated
  `MAX_*` defines and lighting code in `Shapes.vk.frag` and `SpineLit.vk.frag`.
- Optional: generate a C# constants file from a single `limits.json`, so C# and GLSL limits can never
  drift.

## Task list

- [ ] `ContentPaths` helper; replace every literal `"Content/..."` path
- [ ] MSBuild `CompileShaders` target with incremental inputs and outputs
- [ ] Shared `include/` for lights and shadows
- [ ] Delete legacy GL shaders and unused `Quad.vk.*`/`Spine.vk.*` (or keep the unlit Spine path)
- [ ] Remove stale Sandbox csproj entries; unify Silk.NET versions; remove Assimp until used

## Open questions

- Keep committing `.spv` as a fallback, or require the SDK?
- Use `Directory.Build.props` / central package management?

## Related

[Milestones](../../milestones.md) · [Shaders](../shaders.md) · [Build & platforms](../build-and-platforms.md)
