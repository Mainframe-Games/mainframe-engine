# Slang Shaders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Slang the engine's only shader language: port all 41 engine shaders and 9 includes from GLSL, compile
them with `slangc` to SPIR-V with identical interfaces and output names, and make the canvas `.gdshader` translator emit
Slang.

**Architecture:** Each `X.vk.<stage>` GLSL file becomes `X.vk.<stage>.slang` (one entry point `main`, the stage from the
file name) and still compiles to `X.vk.<stage>.spv`, so no C# loader changes. Includes become `include/*.slang` headers
pulled in with Slang's `#include` (they are parameterised by macros such as `MATERIAL_SET`, so they are not modules yet).
Every shader compiles with `-matrix-layout-row-major`, which makes a Slang `float4x4` hold exactly the bytes of a C#
`System.Numerics.Matrix4x4` and keeps GLSL's memory layout. Canvas shaders keep Godot's GLSL-like language inside a Slang
template compiled with `-allow-glsl`.

**Tech Stack:** Slang (`slangc` 2026.1, from the Vulkan SDK locally and the pinned GitHub release in CI), SPIRV-Tools
(`spirv-val`), SPIRV-Cross (verification only), MSBuild targets, POSIX sh, C# (.NET 10).

**Spec:** [docs/design/future/consoles.md](../../design/future/consoles.md) (Rendering → Shaders) and ADR 0144 (Task 1).

## Global Constraints

- **Slang only.** After Task 6 no `.glsl`, `.vk.vert`, `.vk.frag` or `.vk.comp` source and no `glslc` invocation remains
  in the engine, the build, CI or the canvas shader tool.
- **Output names unchanged:** `Content/Shaders/<Dir>/<Name>.vk.<stage>.spv`, entry point `main`.
- **Compiler flags (everywhere):** `slangc <src> -target spirv -capability spirv_1_5 -matrix-layout-row-major
  -entry main -stage <vertex|fragment|compute> -I MainframeEngine/Content/Shaders/include -o <out>`; canvas shaders
  add `-allow-glsl`. Validate with `spirv-val --target-env vulkan1.2`.
- **Goldens must not change.** `just test-render` (moltenvk) and `just render-tests-linux` (lavapipe) pass with the
  committed goldens. A failing golden is a port bug until proven otherwise; never run `just golden-update` in this plan
  without showing the user the diff images first.
- **Interfaces must not change.** For every shader the reflection of the new `.spv` equals the reflection of the old one
  (Task 1's `reflect-diff`): sets, bindings, descriptor types, array sizes, block member offsets/types/strides,
  push-constant layout, input/output locations and types, specialization constant ids and defaults.
- **Dependency:** Slang is a build tool only (Apache-2.0), pinned to **v2026.1** in CI by SHA-256; never shipped.
- **Gates before the final commit** (CLAUDE.md): `just build`; `dotnet build MainframeEngine.slnx -c Release -warnaserror
  -p:CompileShaders=false`; `just test`; `just test-render`; `just format-check`; `just shaders-check`;
  `just canvas-shaders-check`.
- **No pushes.** Commit locally only (user rule: one final push).

### Porting rules (GLSL → Slang)

| GLSL | Slang | Note |
|---|---|---|
| `#version 450`, `#extension GL_GOOGLE_include_directive` | drop | |
| `#include "x.glsl"` | `#include "x.slang"` | include guards → `#pragma once` |
| `layout(set=S, binding=B) uniform Block { … } name;` | `struct Block { … }; [[vk::binding(B, S)]] ConstantBuffer<Block> name;` | std140 is Slang's default for uniform buffers |
| anonymous `uniform Block { … };` | `[[vk::binding(B, S)]] cbuffer Block { … };` | members stay global names |
| `layout(push_constant) uniform P { … } pc;` | `struct P { … }; [[vk::push_constant]] ConstantBuffer<P> pc;` | std430, same as GLSL |
| `layout(constant_id=N) const T k = v;` | `[vk::constant_id(N)] const T k = v;` | |
| `sampler2D`, `samplerCube` | `Sampler2D`, `SamplerCube` | combined image samplers |
| `sampler2DShadow`, `sampler2DArrayShadow`, `samplerCubeShadow` | `Sampler2DShadow`, `Sampler2DArrayShadow`, `SamplerCubeShadow` | |
| `uniform sampler s; uniform texture2D t;` | `SamplerState s; Texture2D t;` | separate objects |
| `texture(s, uv)` | `s.Sample(uv)` / `t.Sample(s, uv)` | |
| `textureLod(s, uv, l)` | `s.SampleLevel(uv, l)` | |
| `textureGrad(s, uv, dx, dy)` | `s.SampleGrad(uv, dx, dy)` | |
| `texture(shadowS, vec3(uv, ref))` (implicit LOD) | `shadowS.SampleCmp(uv, ref)` | array/cube: coordinate + ref split the same way |
| `textureLod(shadowS, …, 0)` | `shadowS.SampleCmpLevelZero(…)` | |
| `textureSize(s, l)` | `s.GetDimensions(l, w, h, levels)` (write a helper) | |
| `vecN/ivecN/uvecN/bvecN/matN` | `floatN/intN/uintN/boolN/floatNxN` | |
| `M * v` | `mul(v, M)` | **row-major convention: Slang matrix = C# matrix** |
| `v * M` | `mul(M, v)` | |
| `A * B` (both matrices) | `mul(B, A)` | |
| `matN(c0, c1, …)` (columns) | `floatNxN(c0, c1, …)` (same arguments, same order) | under `-matrix-layout-row-major` the GLSL columns are Slang rows |
| `m[i]` (column i) | `m[i]` | unchanged under the same rule |
| `mix`, `fract`, `inversesqrt`, `atan(y, x)` | `lerp`, `frac`, `rsqrt`, `atan2(y, x)` | |
| `mod(x, y)` | `glsl_mod(x, y)` from `common.slang` (`x - y * floor(x / y)`) | **never `fmod`**: different sign rule |
| `dFdx`, `dFdy`, `fwidth` | `ddx`, `ddy`, `fwidth` | |
| `gl_VertexIndex`, `gl_InstanceIndex` | `SV_VulkanVertexID`, `SV_VulkanInstanceID` | **not** `SV_VertexID`/`SV_InstanceID` (those subtract the base) |
| `gl_FragCoord` | `float4 fragCoord : SV_Position` input | |
| `gl_FrontFacing`, `gl_FragDepth`, `gl_Position` | `SV_IsFrontFace`, `SV_Depth`, `SV_Position` | |
| `layout(location=N) in/out T x;` | struct fields `[[vk::location(N)]] T x;` | |
| `flat` | `nointerpolation` | |
| `const vec2 a[3] = vec2[](…)` at file scope | `static const float2 a[3] = { … };` | |
| file-scope mutable globals | `static T x;` | |

---

## Review Focus

1. **Shaders no render test draws** (likely: `MeshOutline`, `GlowBlur`, `UiBlur`, `UiDropShadow`, `UiColorMatrix`,
   `ShadowPointCutoutInstanced`, `Shadow2DCutoutInstanced`): a math bug passes the goldens. Expected: identical
   behaviour. Pinned by Task 1's `reflect-diff` on every shader **plus** a side-by-side SPIRV-Cross decompile review of
   each shader's `main` (Tasks 3–5, step "decompile review").
2. **Matrices built or indexed in shaders** (`canvas_*_matrix`, cotangent frames, shadow cascades, cube faces): a
   transposed constructor renders plausible but wrong output. Pinned by the porting rule table and the decompile review:
   the decompiled `mat4(...)` arguments and multiply order must read the same as the original GLSL.
3. **`mod` with negative operands and `atan` argument order** (grid lines, sky, gradients): pinned by a grep gate in
   Task 6 (`fmod` and single-argument misuse) and by the decompile review.
4. **A developer machine without `slangc`, or with an older one:** expected: the build warns `MFSHADER001` and ships the
   committed `.spv`, exactly as today without `glslc`. Pinned by Task 6 step "fallback check".
5. **User `.gdshader` with `mat4` uniforms, `flat` varyings, arrays and helper functions:** expected: same uniform
   offsets and behaviour as the GLSL translation. Pinned by Task 7's fixture shader compiled with `slangc` and
   reflect-checked against the old translation.

---

### Task 1: Decision record, baseline and verification harness

**Files:**
- Create: `memory/decisions/0144-slang-shader-language.md`
- Create (scratch, never committed): `$SCRATCH/slang-port/baseline/**.spv`, `$SCRATCH/slang-port/reflect-diff.py`,
  `$SCRATCH/slang-port/port-check.sh` where `SCRATCH` is this session's scratchpad directory

**Interfaces:**
- Produces: `port-check.sh <Dir>/<Name>.vk.<stage>` — compiles `MainframeEngine/Content/Shaders/<Dir>/<Name>.vk.<stage>.slang`
  with the Global Constraints flags into `$SCRATCH/slang-port/new/`, runs `spirv-val`, then `reflect-diff.py` against the
  baseline; exit 0 only when both pass. `port-check.sh --all` runs every shader that has a `.slang` source.

- [ ] **Step 1: Write ADR 0144**

```markdown
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
- ADR 0007's build-time pipeline, committed-`.spv` fallback and lock are unchanged in shape.
```

- [ ] **Step 2: Snapshot the baseline SPIR-V**

```bash
SCRATCH=<scratchpad>/slang-port; mkdir -p "$SCRATCH/baseline"
cd MainframeEngine/Content/Shaders && find . -name '*.vk.*.spv' | while read f; do mkdir -p "$SCRATCH/baseline/$(dirname "$f")"; cp "$f" "$SCRATCH/baseline/$f"; done
find "$SCRATCH/baseline" -name '*.spv' | wc -l   # expect 41
```

- [ ] **Step 3: Write `reflect-diff.py`**

```python
#!/usr/bin/env python3
"""reflect-diff.py OLD.spv NEW.spv — exit 1 when the shader interfaces differ (names ignored)."""
import json, subprocess, sys

def reflect(path):
    return json.loads(subprocess.check_output(["spirv-cross", path, "--reflect"]))

def resolve(types, t):
    if t in types:
        return [norm_member(types, m) for m in types[t]["members"]]
    return t

def norm_member(types, m):
    out = {k: v for k, v in m.items() if k not in ("name",)}
    out["type"] = resolve(types, m["type"])
    return out

KINDS = ["inputs", "outputs", "ubos", "push_constants", "textures", "separate_images", "separate_samplers",
         "ssbos", "images", "specialization_constants"]

def normalise(d):
    types = d.get("types", {})
    out = {"mode": [e["mode"] for e in d["entryPoints"]]}
    for kind in KINDS:
        items = []
        for r in d.get(kind, []):
            item = {k: v for k, v in r.items() if k not in ("name", "id")}
            if "type" in item:
                item["type"] = resolve(types, item["type"])
            items.append(item)
        key = lambda i: (i.get("set", -1), i.get("binding", -1), i.get("location", -1), i.get("constant_id", -1))
        out[kind] = sorted(items, key=key)
    return out

old, new = normalise(reflect(sys.argv[1])), normalise(reflect(sys.argv[2]))
if old != new:
    for kind in old:
        if old[kind] != new.get(kind):
            print(f"{kind}:\n  old {json.dumps(old[kind])}\n  new {json.dumps(new.get(kind))}")
    sys.exit(1)
print(f"interface ok: {sys.argv[2]}")
```

- [ ] **Step 4: Write `port-check.sh`**

```sh
#!/bin/sh
# port-check.sh <Dir>/<Name>.vk.<stage> | --all
set -eu
SCRATCH=$(cd "$(dirname "$0")" && pwd); ROOT=$(git rev-parse --show-toplevel); S="$ROOT/MainframeEngine/Content/Shaders"
check() {
    rel=$1; stage_ext=${rel##*.}
    case $stage_ext in vert) stage=vertex;; frag) stage=fragment;; comp) stage=compute;; esac
    mkdir -p "$SCRATCH/new/$(dirname "$rel")"
    slangc "$S/$rel.slang" -target spirv -capability spirv_1_5 -matrix-layout-row-major \
        -entry main -stage "$stage" -I "$S/include" -o "$SCRATCH/new/$rel.spv"
    spirv-val --target-env vulkan1.2 "$SCRATCH/new/$rel.spv"
    python3 "$SCRATCH/reflect-diff.py" "$SCRATCH/baseline/$rel.spv" "$SCRATCH/new/$rel.spv"
}
if [ "$1" = "--all" ]; then
    cd "$S"; find . -name '*.vk.*.slang' | sed 's|^\./||; s|\.slang$||' | sort | while read r; do check "$r"; done
else check "$1"; fi
```

- [ ] **Step 5: Self-test the harness on the spike file pair**

Run: `python3 $SCRATCH/reflect-diff.py $SCRATCH/baseline/Post/Fullscreen.vk.vert.spv $SCRATCH/baseline/Post/Fullscreen.vk.vert.spv`
Expected: `interface ok: …`. Then compare two different shaders (`Post/Fullscreen.vk.vert.spv` vs `Sky/Sky.vk.vert.spv`)
and expect exit 1 with a printed difference, proving the diff detects changes.

- [ ] **Step 6: Commit the ADR**

```bash
git add memory/decisions/0144-slang-shader-language.md
git commit -m "ADR 0144: Slang is the engine's shader language"
```

---

### Task 2: Limits generator writes `limits.slang`

**Files:**
- Modify: `build/Shaders.targets` (GenerateShaderLimits task + target)
- Create (generated, committed): `MainframeEngine/Content/Shaders/include/limits.slang`
- Modify: `Tests/MainframeEngine.Tests/Rendering/ShaderLimitsTests.cs`
- Modify: `MainframeEngine/Src/Lighting/LightEnvironment.cs:8`, `MainframeEngine/Src/Rendering/Generated/ShaderLimits.g.cs`
  (generated summary text)

**Interfaces:**
- Produces: `include/limits.slang` with `#pragma once` and the same `#define NAME value // summary` lines as
  `limits.glsl`. `limits.glsl` is still generated until Task 6 deletes the GLSL (the old shaders still compile until then).

- [ ] **Step 1: Make the test expect `limits.slang`**

In `ShaderLimitsTests.GeneratedGlslHeaderMatchesTheJson`, rename to `GeneratedSlangHeaderMatchesTheJson` and read
`Path.Combine(ShaderDir, "include", "limits.slang")`; keep the define-parsing assertions. Rename the tuple field
`Glsl` → `Shader` in `ReadJson` (the JSON key stays `"glsl"` until Step 3 renames it to `"shader"`).

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Tests/MainframeEngine.Tests --filter ShaderLimitsTests`
Expected: FAIL, `limits.slang` not found.

- [ ] **Step 3: Generate `limits.slang`**

In the `GenerateShaderLimits` task add a `SlangFile` parameter and emit:

```csharp
var slang = new StringBuilder();
slang.Append("// Generated from Content/Shaders/limits.json by build/Shaders.targets. Do not edit.\n#pragma once\n\n");
foreach (Match m in matches)
    slang.Append("#define " + m.Groups["glsl"].Value + " " + m.Groups["value"].Value + " // " + m.Groups["summary"].Value + "\n");
```

Write it with the same "only when changed" loop. Rename the JSON key `"glsl"` → `"shader"` in `limits.json`, the regex
and the test. Add `$(ShaderLimitsSlang)` (`$(ShaderIncludeDir)/limits.slang`) to the target's `Outputs` and `Touch`.
Update the C# summary text to "`include/limits.slang`".

- [ ] **Step 4: Build and test**

Run: `just build && dotnet test Tests/MainframeEngine.Tests --filter ShaderLimitsTests`
Expected: build clean (0 warnings), test PASS, `include/limits.slang` created with every limit.

- [ ] **Step 5: Commit**

```bash
git add build/Shaders.targets MainframeEngine/Content/Shaders/limits.json MainframeEngine/Content/Shaders/include/limits.slang \
  MainframeEngine/Src Tests/MainframeEngine.Tests/Rendering/ShaderLimitsTests.cs
git commit -m "Shader limits: generate include/limits.slang"
```

---

### Task 3: Port the frame, post, debug, gizmo, grid and sky shaders

**Files (create `.slang` next to each GLSL file; the GLSL stays until Task 6):**
- `include/common.slang`, `include/frame.slang`, `include/sky.slang`
- `Post/Fullscreen.vk.vert`, `Post/Tonemap.vk.frag`, `Post/TonemapPost.vk.frag`, `Post/GlowBlur.vk.frag`
- `Debug/DebugLines.vk.vert|frag`, `Gizmos/ScreenGizmo.vk.vert|frag`, `SceneGrid/SceneGrid.vk.vert|frag`
- `Sky/Sky.vk.vert`, `Sky/Sky.Procedural.vk.frag`, `Sky/Sky.Panoramic.vk.frag`, `Sky/Sky.Cubemap.vk.frag`

**Interfaces:**
- Produces in `common.slang`: every helper of `common.glsl` with the same names (sRGB conversions etc.), plus
  `float glsl_mod(float x, float y)` and `float2/3/4` overloads (`x - y * floor(x / y)`).
- Produces in `frame.slang`: `struct FrameData` with the members of `frame.glsl` in the same order and
  `[[vk::binding(0, 0)]] ConstantBuffer<FrameData> frame;` (keep the GLSL instance name so includes read the same).

- [ ] **Step 1: Port `common`, `frame` and `Post/Fullscreen.vk.vert`** following the porting rules. Worked example for
  the fullscreen triangle:

```slang
// Post/Fullscreen.vk.vert.slang — fullscreen triangle (no vertex buffer).
static const float2 positions[3] = { float2(-1.0, -1.0), float2(3.0, -1.0), float2(-1.0, 3.0) };

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv;
};

[shader("vertex")]
VsOut main(uint vertexIndex : SV_VulkanVertexID)
{
    VsOut o;
    float2 pos = positions[vertexIndex];
    o.position = float4(pos, 0.0, 1.0);
    o.uv = pos * 0.5 + 0.5;   // copy the original expression exactly
    return o;
}
```

  (Copy each original's expressions; the example only shows the shape.)

- [ ] **Step 2: Interface check**

Run: `sh $SCRATCH/port-check.sh Post/Fullscreen.vk.vert`
Expected: `spirv-val` silent, `interface ok`. Fix and repeat until it passes.

- [ ] **Step 3: Port the remaining files of this task**, running `port-check.sh` after each until `interface ok`.

- [ ] **Step 4: Decompile review**

For each shader of this task:
`diff <(spirv-cross $SCRATCH/baseline/<rel>.spv) <(spirv-cross $SCRATCH/new/<rel>.spv)`. Names differ; the arithmetic must
not. Check line by line that every matrix product, constructor, `mod`, `atan`, `mix`, comparison and constant matches.
Record "reviewed: <rel>" in the task notes for each shader.

- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Content/Shaders
git commit -m "Shaders: port post, debug, gizmo, grid and sky shaders to Slang"
```

---

### Task 4: Port the mesh, material, lighting and shadow shaders

**Files:**
- `include/material.slang`, `include/lights.slang`, `include/shadows.slang`
- `Mesh/Mesh.vk.vert|frag`, `Mesh/MeshId.vk.frag`, `Mesh/MeshOutline.vk.vert`
- `Shadows/*` (10 files: `Shadow2D.vk.vert|frag`, `Shadow2DInstanced.vk.vert`, `Shadow2DCutoutInstanced.vk.vert`,
  `ShadowCutout.vk.frag`, `ShadowPoint.vk.vert|frag`, `ShadowPointInstanced.vk.vert`, `ShadowPointCutout.vk.frag`,
  `ShadowPointCutoutInstanced.vk.vert`)

**Interfaces:**
- Consumes: `common.slang`, `frame.slang` (Task 3).
- Produces: `material.slang`, `lights.slang`, `shadows.slang` keeping the override macros `MATERIAL_SET`, `LIGHTS_SET`,
  `LIGHTS_BINDING`, `SHADOW_SET` with the same defaults, and the same function names as the GLSL includes.

- [ ] **Step 1: Port the three includes and `Mesh/Mesh.vk.vert|frag`.** Specialization constant:
  `[vk::constant_id(0)] const int kAlphaMode = kAlphaOpaque;`. Front facing: `bool frontFacing : SV_IsFrontFace`
  fragment input. Point shadows write depth: return a struct with `float depth : SV_Depth`.
- [ ] **Step 2:** `sh $SCRATCH/port-check.sh Mesh/Mesh.vk.vert && sh $SCRATCH/port-check.sh Mesh/Mesh.vk.frag` → both
  `interface ok`.
- [ ] **Step 3: Port the remaining mesh and shadow shaders**, `port-check.sh` after each (instanced variants use
  `SV_VulkanInstanceID`).
- [ ] **Step 4: Decompile review** of every file of this task (Task 3 Step 4 procedure). `shadows.slang` needs the
  closest reading: cascade selection, PCF loops, cube-face math and bias.
- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Content/Shaders
git commit -m "Shaders: port mesh, material, lighting and shadow shaders to Slang"
```

---

### Task 5: Port the Spine, UI and canvas shaders

**Files:**
- `Spine/SpineLit.vk.vert|frag`
- `UI/Ui.vk.vert|frag`, `UI/UiFullscreen.vk.vert`, `UI/UiBlendMask.vk.frag`, `UI/UiBlur.vk.frag`,
  `UI/UiColorMatrix.vk.frag`, `UI/UiDropShadow.vk.frag`, `UI/UiGradient.vk.frag`, `UI/UiPassthrough.vk.frag`
- `include/canvas.slang`, `include/canvas_lights.slang`, `Canvas/Canvas.vk.vert|frag`

**Interfaces:**
- Produces in `canvas.slang`: `canvas_pc` (push constants), `canvas_model_matrix()`, `canvas_canvas_matrix()`,
  `canvas_screen_matrix()`, `canvas_flags()`, the `CANVAS_FLAG_*` macros. The matrix functions build
  `float4x4(c0, c1, c2, c3)` with the GLSL constructor arguments unchanged (row-major rule), so both native Slang
  (`mul(v, M)`) and `-allow-glsl` code (`M * v`) get the GLSL result. `canvas_lights.slang` keeps `CANVAS_LIGHT_SET` and
  `canvas_apply_lights(inout float4 color, float4 baseColor, float2 vertex)`.
- Consumed by Task 7: the translator `#include`s `canvas.slang` and `canvas_lights.slang`.

- [ ] **Step 1: Port Spine and UI**, `port-check.sh` after each. `UiGradient.vk.frag` defines must keep matching
  `VulkanUiRenderer.Tables.cs:474` (same numeric values).
- [ ] **Step 2: Port the canvas includes and `Canvas/*`**, `port-check.sh` after each.
- [ ] **Step 3: Whole-set interface check:** `sh $SCRATCH/port-check.sh --all` → 41 × `interface ok`.
- [ ] **Step 4: Decompile review** of every file of this task.
- [ ] **Step 5: Commit**

```bash
git add MainframeEngine/Content/Shaders
git commit -m "Shaders: port Spine, UI and canvas shaders to Slang"
```

---

### Task 6: Switch the build, CI and tools to slangc and delete the GLSL

**Files:**
- Modify: `build/shaders.sh`, `build/Shaders.targets`, `justfile` (comments on `shaders`), `.github/workflows/ci.yml`
  (shaders job, comment at lines 83–84)
- Modify: `MainframeEngine.Editor/Src/FileSystem/FileKind.cs:59`, `MainframeEngine.Editor/Src/UI/EditorIcons.cs:102`
  (add `.slang`)
- Modify: `Tests/MainframeEngine.Editor.Tests/Projects/ProjectCreationIntegrationTests.cs:77` (comment)
- Delete: every `MainframeEngine/Content/Shaders/**/*.vk.{vert,frag,comp}` and `include/*.glsl`; the `limits.glsl`
  output from the generator
- Regenerate: all 41 `.spv`, `shaders.lock`

**Interfaces:**
- Produces: `build/shaders.sh compile|lock|check|list` over `*.vk.*.slang` sources and `include/*.slang`; MSBuild
  properties `Slangc` (override), `SlangcPath`, `SlangcAvailable`; warning `MFSHADER001` text names slangc.

- [ ] **Step 1: `build/shaders.sh`.** `list_sources` finds `*.vk.vert.slang`, `*.vk.frag.slang`, `*.vk.comp.slang`;
  `list_includes` finds `include/*.slang`; `require slangc`; compile:

```sh
stage_of() {
    case $1 in
        *.vk.vert.slang) echo vertex ;;
        *.vk.frag.slang) echo fragment ;;
        *.vk.comp.slang) echo compute ;;
    esac
}
# in compile(): spv path drops ".slang"
base=${src%.slang}
spv="${out_dir:+$out_dir/}$base.spv"
slangc "$src" -target spirv -capability spirv_1_5 -matrix-layout-row-major \
    -entry main -stage "$(stage_of "$src")" -I "$INCLUDE_DIR" -o "$spv"
spirv-val --target-env "$TARGET_ENV" "$spv"
```

  `write_lock`/`check` use `${src%.slang}.spv`. Update the header comment and the `require` message ("install the
  Vulkan SDK, or slangc + spirv-tools").

- [ ] **Step 2: `build/Shaders.targets`.**
  - `ShaderSource Include="Content/Shaders/**/*.vk.vert.slang;Content/Shaders/**/*.vk.frag.slang;Content/Shaders/**/*.vk.comp.slang"`
    with metadata `Stage` set by three item conditions on `%(Filename)` ending in `.vk.vert` / `.vk.frag` / `.vk.comp`.
  - `ShaderInclude Include="Content/Shaders/include/*.slang"`; drop `$(ShaderLimitsGlsl)` and the GLSL output of the
    generator.
  - `FindGlslc` → `FindSlangc`: `$(Slangc)`, else `$(VULKAN_SDK)/bin/slangc(.exe)`, else `slangc` on PATH; probe with
    `slangc -v`.
  - `CompileShaders` Exec: the Global Constraints command with `-stage %(ShaderSource.Stage)` and output
    `$(ShaderOutputDir)%(ShaderSource.SubDir)%(ShaderSource.Filename).spv` (Filename is `X.vk.frag`). Error regex:
    `CustomErrorRegularExpression="error \d+:"`, warning regex `"warning \d+:"`.
  - `IncludeShadersInOutput` fallback item: `@(ShaderSource->'%(RootDir)%(Directory)%(Filename).spv')`.
  - Update the header comment block.
- [ ] **Step 3: Delete the GLSL and rebuild the committed SPIR-V**

```bash
git rm -q -- ':(glob)MainframeEngine/Content/Shaders/**/*.vk.vert' ':(glob)MainframeEngine/Content/Shaders/**/*.vk.frag' \
  ':(glob)MainframeEngine/Content/Shaders/include/*.glsl'
git ls-files MainframeEngine/Content/Shaders | grep -v '\.slang$\|\.spv$\|shaders.lock$\|limits.json$'   # expect nothing
just shaders
```

  Expected: "Compiled and validated 41 shaders (target vulkan1.2)", `shaders.lock` rewritten.

- [ ] **Step 4: Gates for this task**

```bash
sh $SCRATCH/port-check.sh --all        # still 41 × interface ok against the glslc baseline
just build                              # 0 warnings
just shaders-check
git grep -n -i "glslc\|\.glsl\b" -- ':!docs' ':!memory' ':!*.spv'   # expect no hits outside Task 7's files
git grep -n "fmod(" -- 'MainframeEngine/Content/Shaders'           # expect none
```

- [ ] **Step 5: Fallback check (Review Focus 4)**

Run: `dotnet build MainframeEngine/MainframeEngine.csproj -p:Slangc=/nonexistent/slangc 2>&1 | grep MFSHADER001`
Expected: the warning is printed (the build fails under warnings-as-errors, which is today's behaviour too); then
`dotnet build MainframeEngine/MainframeEngine.csproj -p:CompileShaders=false` succeeds and
`bin/Debug/net10.0/Content/Shaders/Mesh/Mesh.vk.frag.spv` equals the committed file (`cmp`).

- [ ] **Step 6: CI shaders job** — replace the glslc install step:

```yaml
      - name: Install Slang and SPIRV-Tools
        env:
          SLANG_VERSION: "2026.1"
          SLANG_SHA256: "db8ac338b5558aa0f183a63756cfa386751ab79690c5d9ff80e71d32864eb8d5" # GitHub asset digest, 2026-10-08
        run: |
          sudo apt-get update
          sudo apt-get install -y --no-install-recommends spirv-tools
          curl -fsSL -o slang.tar.gz "https://github.com/shader-slang/slang/releases/download/v${SLANG_VERSION}/slang-${SLANG_VERSION}-linux-x86_64.tar.gz"
          echo "${SLANG_SHA256}  slang.tar.gz" | sha256sum -c -
          mkdir -p "$RUNNER_TEMP/slang" && tar -xzf slang.tar.gz -C "$RUNNER_TEMP/slang"
          echo "$RUNNER_TEMP/slang/bin" >> "$GITHUB_PATH"
      - name: Tool versions
        run: slangc -v && spirv-val --version
```

  The digest is GitHub's recorded SHA-256 of `slang-2026.1-linux-x86_64.tar.gz` (69,622,137 bytes); the
  `sha256sum -c` step fails the job if the download ever differs. Update the comment at `ci.yml:83-84` to say slangc. Run `ci.yml` through `actionlint` if installed, else review.
- [ ] **Step 7: Editor file kinds** — add `".slang"` to the shader extensions in `FileKind.cs` and `EditorIcons.cs`
  (keep the existing ones; user projects may hold other files). Run `just test` → PASS.
- [ ] **Step 8: Commit**

```bash
git add -A MainframeEngine/Content/Shaders build justfile .github MainframeEngine.Editor Tests
git commit -m "Shaders: compile with slangc; remove GLSL and glslc"
```

---

### Task 7: Canvas shader translator emits Slang

**Files:**
- Modify: `MainframeEngine/Src/Rendering/Canvas/CanvasShaderCompiler.cs` (template, `GlslType`, record fields)
- Modify: `MainframeEngine/Src/Rendering/Canvas/Shader.cs` (`CanvasShaderBuild`: slangc, hash, messages)
- Modify: `Tools/MainframeEngine.ShaderBuild/Program.cs` (include dir unchanged; messages)
- Modify: `Tests/MainframeEngine.Tests/Canvas/CanvasShaderCompilerTests.cs`
- Create: `Tests/MainframeEngine.RenderTests.Host/Content/Shaders/features.gdshader` is **not** added (the host content
  is golden-tested); the feature fixture lives in `$SCRATCH/slang-port/features.gdshader` and is checked in Step 6
- Regenerate: `Tests/MainframeEngine.RenderTests.Host/Content/Shaders/wave.gdshader.{vert,frag}.spv` + `.spvlock`

**Interfaces:**
- Consumes: `include/canvas.slang`, `include/canvas_lights.slang` (Task 5).
- Produces: `CanvasShaderProgram(string VertexSource, string FragmentSource, …)` (renamed from `VertexGlsl` /
  `FragmentGlsl`; same other members); `CanvasShaderBuild.FindSlangc()`; `CanvasShaderBuild.Build(string gdshaderPath,
  string includeDirectory, string? slangc = null)`.

- [ ] **Step 1: Update the tests to the Slang template** (they assert generated text). Examples of the new expectations:

```csharp
Assert.Contains("[[vk::binding(1, 1)]] Sampler2D fog;", p.FragmentSource, StringComparison.Ordinal);
Assert.Contains("[[vk::location(2)]] float2 v_world;", p.VertexSource, StringComparison.Ordinal);
Assert.Contains("[[vk::location(2)]] nointerpolation int v_id;", p.FragmentSource, StringComparison.Ordinal); // flat varying
Assert.Contains("[[vk::binding(0, 1)]] cbuffer MaterialUniforms", p.FragmentSource, StringComparison.Ordinal);
Assert.Contains("#include \"canvas.slang\"", p.VertexSource, StringComparison.Ordinal);
```

  Keep every existing behavioural assertion (layout offsets, defaults, hints, render modes, varyings split by stage).
- [ ] **Step 2:** `dotnet test Tests/MainframeEngine.Tests --filter CanvasShaderCompilerTests` → FAIL (old template).
- [ ] **Step 3: Rewrite the template.** Common prelude:

```text
// Translated from a Godot canvas_item shader by CanvasShaderCompiler. Do not edit: edit the .gdshader.
#include "canvas.slang"
#define PI 3.1415926535897932384626433833
#define TAU 6.2831853071795864769252867666
#define E 2.7182818284590452353602874714
[[vk::binding(0, 1)]] cbuffer MaterialUniforms { <GLSL-typed members, declaration order> };
[[vk::binding(<n>, 1)]] Sampler2D <name>;
<user globals, each prefixed "static ">
```

  Vertex stage: Godot built-ins as `static` globals (`static vec2 VERTEX; …`), the user helpers and `vertex()` verbatim,
  then a Slang entry point:

```text
struct VsIn { [[vk::location(0)]] vec2 inPosition; [[vk::location(1)]] vec2 inUv; [[vk::location(2)]] vec4 inColor; };
struct VsOut { float4 position : SV_Position; [[vk::location(0)]] vec4 uvVertexInterp; [[vk::location(1)]] vec4 colorInterp;
               <[[vk::location(2+i)]] [nointerpolation] T name; per varying> };
[shader("vertex")]
VsOut main(VsIn input, uint vertexId : SV_VulkanVertexID, uint instanceId : SV_VulkanInstanceID)
{
    VERTEX = input.inPosition; UV = input.inUv; COLOR = input.inColor;
    MODEL_MATRIX = canvas_model_matrix(); CANVAS_MATRIX = canvas_canvas_matrix(); SCREEN_MATRIX = canvas_screen_matrix();
    TIME = canvas_pc.canvasOrigin.z; TEXTURE_PIXEL_SIZE = canvas_pc.modelOrigin.zw; POINT_SIZE = 1.0;
    INSTANCE_CUSTOM = vec4(0.0); INSTANCE_ID = int(instanceId); VERTEX_ID = int(vertexId);
    vertex();                                  // only when the shader has vertex()
    vec2 vertex = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
    VsOut o;
    o.colorInterp = COLOR;
    vertex = (CANVAS_MATRIX * vec4(vertex, 0.0, 1.0)).xy;
    o.uvVertexInterp = vec4(UV, vertex);
    o.position = SCREEN_MATRIX * vec4(vertex, 0.0, 1.0);
    <o.name = name; per varying>
    return o;
}
```

  Varyings are `static` globals the user code writes; the entry point copies them into `VsOut` (and the fragment entry
  point copies them from its input struct into the same-named statics before calling `fragment()`). The fragment stage
  mirrors the old one: `[[vk::binding(0, 0)]] Sampler2D colorTexture; #define TEXTURE colorTexture`,
  `#define CANVAS_LIGHT_SET 2` + `#include "canvas_lights.slang"`, `FRAGCOORD` from `float4 fragCoord : SV_Position`,
  output `[[vk::location(0)]] float4` via `: SV_Target0`. The GLSL syntax of the template body (`*`, `mat4`, `texture`)
  is valid under `-allow-glsl` and keeps GLSL semantics (verified in the spike).
  `GlslType` → `ShaderTypeName` (it still returns `vec2`/`mat4` etc., which `-allow-glsl` accepts).
- [ ] **Step 4:** `dotnet test Tests/MainframeEngine.Tests --filter CanvasShaderCompilerTests` → PASS.
- [ ] **Step 5: `CanvasShaderBuild` uses slangc.** `FindSlangc` mirrors `FindGlslc` (`slangc`/`slangc.exe`); `Compile`
  writes the source to a temp `.slang` file and runs: `slangc <temp> -allow-glsl -target spirv -capability spirv_1_5
  -matrix-layout-row-major -entry main -stage <vertex|fragment> -I <include> -o <out>`. Error text: "slangc failed for
  …". `Hash` hashes `VertexSource`/`FragmentSource`. Update `Shader.cs:57` ("build the project (slangc)") and the XML docs.
- [ ] **Step 6: Feature fixture (Review Focus 5).** Write `$SCRATCH/slang-port/features.gdshader`:

```glsl
shader_type canvas_item;
uniform mat4 xform;
uniform vec4 tint : source_color = vec4(1.0);
uniform float weights[3];
uniform sampler2D noise : filter_nearest, repeat_enable;
varying vec2 v_world;
varying flat int v_id;
float twice(float x) { return x * 2.0; }
void vertex() { v_world = (xform * vec4(VERTEX, 0.0, 1.0)).xy; v_id = VERTEX_ID; }
void fragment() { COLOR = texture(noise, UV + v_world * 0.001) * tint * twice(weights[v_id % 3]); }
```

  Compile it once with the **old** translator, from the Task 5 commit (it still has the GLSL includes and uses glslc):
  `git worktree add $SCRATCH/old $(git log --format=%h -1 --grep "port Spine, UI and canvas")`, then
  `dotnet run --project $SCRATCH/old/Tools/MainframeEngine.ShaderBuild -- build $SCRATCH/slang-port/old-fixture` on one
  copy of the fixture folder and the current tool on another copy (`git stash` is not used: the stash is shared) and once with the new one; run `reflect-diff.py` on both stage pairs → `interface ok`;
  then the decompile review of both `main`s (the `xform * vec4` product must read identically). Remove the temporary
  worktree with `git worktree remove`.
- [ ] **Step 7: Rebuild the committed canvas shader:** `just canvas-shaders` → "1 canvas shader(s), 1 built";
  `just canvas-shaders-check` → "0 stale".
- [ ] **Step 8: Commit**

```bash
git add MainframeEngine/Src/Rendering/Canvas Tools/MainframeEngine.ShaderBuild Tests/MainframeEngine.Tests/Canvas \
  Tests/MainframeEngine.RenderTests.Host/Content/Shaders
git commit -m "Canvas shaders: translate .gdshader to Slang, compile with slangc"
```

---

### Task 8: Full verification

**Files:** none modified unless a gate fails (then fix in the owning task's files and amend nothing: new commit).

- [ ] **Step 1:** `caffeinate -u -t 1200 &` then `just test-render` → PASS against the moltenvk goldens, validation gate and
  allocation gate clean.
- [ ] **Step 2:** `just render-tests-linux` (Docker, lavapipe goldens) → PASS. If Docker is unavailable, say so in the
  final report; CI's render-tests job covers it after the push.
- [ ] **Step 3:** `just test` → PASS; `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`
  → 0 warnings; `just format-check`; `just shaders-check`; `just canvas-shaders-check` → all pass.
- [ ] **Step 4:** `just demo-screenshots` and compare against `docs/images/demo/*.png` with `git diff --stat` (PNG bytes
  should be unchanged or visually identical; inspect any that changed).
- [ ] **Step 5:** If any golden or screenshot differs: stop, show the user the diff images and the shader at fault; do
  not re-record goldens without their approval.

---

### Task 9: Documentation

**Files:**
- Modify: `docs/design/shaders.md` (workflow diagram, tool lookup, include syntax, porting/authoring rules table from
  this plan, file inventory names `.slang`), `docs/design/canvas.md` (translation target), `CLAUDE.md` (Build & Run shader
  paragraph, `just shaders` comment, macOS paragraph "`glslc`" → "`slangc`"), `README.md:80,119,391`,
  `docs/design/build-and-platforms.md` (tool requirements), `docs/design/future/consoles.md` and
  `docs/design/future/rendering-backend-abstraction.md` (shader language decided: ADR 0144; DXIL target with D3D12),
  `docs/milestones.md` (M11 shader row: ✅ language/SPIR-V part done, DXIL with D3D12), `memory/context/lane-agent-brief.md`
  (shader rules: `mul(v, M)`, `SV_VulkanVertexID`, `glsl_mod`), `memory/decisions/0007-*.md` (add "superseded in part by
  ADR 0144: glslc → slangc"), `memory/decisions/0113-*.md` (translation target note)
- Modify: comments that name GLSL files: `MainframeEngine/Src/**` (`include/x.glsl` → `include/x.slang`; "GLSL" →
  "the shaders"), `Tests/MainframeEngine.RenderTests/SkyGridReference.cs:7,17`

- [ ] **Step 1:** Update every file above. `git grep -n -i "glslc\|\.glsl\b\|GLSL"` afterwards: remaining hits are only
  historical ADRs, the progress log, the Godot-language mentions in canvas docs ("Godot's GLSL-like language") and
  `-allow-glsl`.
- [ ] **Step 2:** `just build && just format-check` → clean (comment-only C# changes).
- [ ] **Step 3: Commit**

```bash
git add docs CLAUDE.md README.md memory MainframeEngine/Src Tests/MainframeEngine.RenderTests
git commit -m "Docs: Slang shader language (ADR 0144)"
```
