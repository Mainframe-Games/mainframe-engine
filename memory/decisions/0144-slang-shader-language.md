# ADR 0144 — Slang is the engine's shader language

- **Date:** 2026-10-08
- **Status:** accepted
- **Milestone:** M11 (shader language), consoles readiness (ADR 0143)
- **Spec:** docs/design/future/consoles.md#rendering, docs/design/shaders.md

## Context

41 GLSL shaders and 9 includes compile to SPIR-V with `glslc` (ADR 0007). The console plan (ADR 0143) needs one source
for SPIR-V and DXIL (and later a console compiler's input); M11's WebGPU backend needs WGSL. glslang's HLSL front end
is deprecated. Slang (Khronos-hosted, Apache-2.0) compiles one HLSL-like language to SPIR-V, DXIL, MSL and WGSL and
ships in the LunarG Vulkan SDK next to `glslc`.

## Decision

- Slang is the only shader language for engine shaders. `glslc` is removed from the build, CI and tools.
- Sources are `Content/Shaders/<Dir>/<Name>.vk.<stage>.slang`, one entry point `main`, compiled to the unchanged
  `<Name>.vk.<stage>.spv`. Includes are `include/*.slang` headers (`#include`), not modules, while they are
  parameterised by macros.
- Flags: `-target spirv -capability spirv_1_5 -matrix-layout-row-major`. Row-major layout makes a Slang `float4x4`
  equal the C# `System.Numerics.Matrix4x4` uploaded to it: `mul(v, M)` is `Vector4.Transform(v, M)`.
- Canvas shaders (`.gdshader`, ADR 0113) keep Godot's language; the translator wraps it in a Slang template compiled
  with `-allow-glsl`.
- CI pins Slang v2026.1 (GitHub release, SHA-256). Build-only: never linked or shipped.
- DXIL/WGSL targets are added with the backends that consume them (M11), not now.

## Consequences

- Every committed `.spv` and `shaders.lock` changes once; interfaces and goldens do not.
- Shader authors write `mul(v, M)`, `SV_VulkanVertexID`, `glsl_mod`; docs/design/shaders.md lists the rules.
- Slang drops unread stage inputs (no option keeps them), and the validation layer warns about the vertex outputs and
  attributes left unread. So each stage writes only what the next reads: `Post/Fullscreen.vk.vert` outputs only the
  position, the object-ID pass got its own `Mesh/MeshId.vk.vert`, each mesh pipeline passes only the attributes its
  vertex shader reads, and the canvas build compiles the fragment stage first and has the vertex stage write only the
  inputs it kept (`SpirvInputs`).
- Slang keeps local names in the SPIR-V and MoltenVK passes them into Metal source: no Metal keywords (`vertex`, …) as
  names.
- `shaders.lock` records the `slangc` version that built the committed `.spv`; `slangc` older than 2026.1 is treated like
  a missing one. Canvas shaders also compile with `-obfuscate`, so user names never reach Metal source.
- Game projects' canvas SPIR-V built with `glslc` is stale (the translation changed): rebuild once with
  `just canvas-shaders <folder>`.
- ADR 0007's build-time pipeline, committed-`.spv` fallback and lock are unchanged in shape.
