# Spine Integration

## Purpose

Renders Spine skeletal animations as lit, shadow-casting geometry in the 3D scene. Use `SpineNode`;
`SpineRenderer` and `SpineTextureLoader` are implementation details.

## Key types

| Type | File | Role |
|---|---|---|
| `SpineNode : Node3D` | [Nodes/SpineNode.cs](../../MainframeEngine/Src/Nodes/SpineNode.cs) | Loads the atlas and skeleton, drives animation, forwards draw calls |
| `SpineFolder` | same file | `readonly struct`: folder → `Name`, first `*.atlas`, first `*.json` (throws if missing) |
| `SpineRenderer` (internal) | [Rendering/Spine/SpineRenderer.cs](../../MainframeEngine/Src/Rendering/Spine/SpineRenderer.cs) | Builds vertices, owns the pipeline, buffers and descriptor sets |
| `SpineTextureLoader` | [Rendering/Spine/SpineTextureLoader.cs](../../MainframeEngine/Src/Rendering/Spine/SpineTextureLoader.cs) | Decodes atlas pages to RGBA (StbImageSharp); `page.rendererObject = page index` |
| Spine runtime | `Plugins/Spine/spine-csharp` | Vendored git submodule. Do not modify. |

## Usage

```csharp
Node.Initialize(Renderer, shadowSystem);
var boy = new SpineNode(Renderer, new SpineFolder("Content/Models/Spine/SpineBoy"))
{
    Scale = new Vector3(0.1f),
};
boy.SetAnimation("walk");
// OnUpdate:          boy.OnUpdate(gameTime);
// OnShadowPass:      inside RenderShadows → boy.DrawShadow2D(cb) / boy.DrawShadowPoint(cb, pos, range)
// OnRenderMainPass:  boy.Draw(camera, lights);
```

## Per-frame data flow

```mermaid
flowchart TD
    subgraph Update["OnUpdate"]
        U1["Skeleton.UpdateWorldTransform"] --> U2["AnimationState.Update(dt)"]
        U2 --> U3["AnimationState.Apply(Skeleton)"]
        U3 --> U4["SpineRenderer.BuildVertices(ZSpacing, ModelMatrix)"]
        U4 --> V[("CPU: Vertex[ ] (stride 40)<br/>+ Vector3[ ] shadow positions<br/>+ batches by atlas page")]
    end
    subgraph Shadow["OnShadowPass (per light / face)"]
        V --> S1["upload positions → per-image VB"]
        S1 --> S2["GetShadow2DPipeline(12) / GetShadowPointPipeline(12)<br/>push model (64/80 B), draw"]
    end
    subgraph Main["OnRenderMainPass"]
        V --> M1["write VP + lights UBO, upload vertices"]
        M1 --> M2["bind sets 0,1,2 (+ texture set per batch)<br/>push model + worldNormal, CmdDraw per batch"]
    end
```

### Vertex building

- Walks `Skeleton.DrawOrder`. A `RegionAttachment` produces 6 vertices (two triangles, not indexed).
  A `MeshAttachment` produces one vertex per triangle index.
- Each slot is pushed `ZSpacing` (default 0.01) further along z to avoid z-fighting.
- Tint = skeleton RGBA × slot RGBA. When `pma` is set, RGB is also multiplied by alpha.
- A new batch starts whenever the atlas page changes.

| CPU vertex (40 B) | Offset |
|---|---|
| `vec3 Position` | 0 |
| `vec2 Uv` | 12 |
| `vec4 Color` | 20 |
| `float TextureIndex` (written, unused) | 36 |

### Descriptor sets

| Set | With `ShadowSystem` | Without `ShadowSystem` |
|---|---|---|
| 0 | VP UBO (vertex) | VP UBO |
| 1 | Lights UBO, 1200 B (fragment) | Lights UBO |
| 2 | `ShadowSystem.MainDescSetLayout` | **texture** ⚠ |
| 3 | `sampler2D uTexture` per (image × page) | — |

Push constant (vertex, 80 B): `mat4 model` + `vec4 worldNormal`. The normal is
`normalize(TransformNormal(+Z, model))`, so the whole skeleton shares one normal.

Pipeline: `SpineLit.vk.{vert,frag}`, no culling, CCW, alpha blend (`SrcAlpha/OneMinusSrcAlpha` for color,
`One/OneMinusSrcAlpha` for alpha), depth test and write `Less`. The fragment shader discards alpha < 0.01.
Textures are `R8G8B8A8Unorm`, with one shared Linear/ClampToEdge sampler.

## Invariants

- Create a `ShadowSystem` and call `Node.Initialize` before constructing a `SpineNode`. The lit shader
  always expects shadows at set 2.
- Draw Spine after opaque geometry. It alpha-blends but also writes depth.

## Known issues

- **Spine without a `ShadowSystem` breaks:** the texture lands at set 2 while the shader expects it at
  set 3 ([SpineRenderer.cs:729](../../MainframeEngine/Src/Rendering/Spine/SpineRenderer.cs)).
  `Examples/SpineExamples` hits this case.
- **`SpineScale` setter has no effect after construction.** It is copied to `Skeleton.ScaleX/Y` only in the
  constructor ([SpineNode.cs:29-30](../../MainframeEngine/Src/Nodes/SpineNode.cs)) and in `FlipX`.
- **Pose lags one frame:** `UpdateWorldTransform` runs before `Apply` ([SpineNode.cs:48](../../MainframeEngine/Src/Nodes/SpineNode.cs)).
- **`SetAnimation` queues** with `AddAnimation` ([SpineNode.cs:95](../../MainframeEngine/Src/Nodes/SpineNode.cs)), so it plays after the default first animation instead of replacing it.
- **Premultiplied alpha is applied twice:** PMA vertex color combined with a `SrcAlpha` blend.
- **`MaxVertices = 8192` is not bounds-checked** in `AddVertex` ([SpineRenderer.cs:14](../../MainframeEngine/Src/Rendering/Spine/SpineRenderer.cs)), so large skeletons throw.
- Clipping attachments, per-slot blend modes and two-color tint are ignored.
- Only `atlas.Pages[0].pma` is honoured. Texture sets are duplicated per swapchain image.
- Shadow positions are re-uploaded on every shadow sub-pass.
- An unlit path (`Spine.vk.*`) is compiled but not loaded by any code.
- README/CLAUDE.md mention `OnRender`; the actual API is `Draw(camera, lights)`.

## Related docs

[Scene graph & nodes](scene-graph-and-nodes.md) · [Lighting](lighting.md) · [Shadow system](shadow-system.md) ·
[Future: renderer stabilization](future/renderer-stabilization.md) · [Future: color pipeline](future/color-pipeline.md)
