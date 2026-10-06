# Materials & Meshes

## Purpose

How geometry is described and drawn: `Mesh` resources (explicit `ArrayMesh`es and generated primitives),
`StandardMaterial3D`, `Texture2D`, and the nodes that place them (`MeshInstance3D`, `Sprite3D`). The render
server never draws these one by one. Every frame it culls them, sorts them, and draws each run of equal
pipeline + material + mesh as **one instanced draw**. A shared, state-hashed pipeline cache means a thousand
materials with the same fixed-function state cost one `VkPipeline`. The same machinery renders shadow casters,
the object-ID picking pass and offscreen `SubViewport`s.

Before M3, every `ShapeBase` (`Box3d`, `Quad`) built its own pipeline, descriptor pool and UBOs. It uploaded the
1200-byte lights UBO per object and drew a flat colour. Those types are gone: old scenes load them as
`MeshInstance3D`s (see [Compatibility](#compatibility)).

Decisions: [ADR 0013 Assimp for import](../../memory/decisions/0013-assimp-for-model-import.md),
[0014 Blinn-Phong now, PBR later](../../memory/decisions/0014-blinn-phong-now-pbr-later.md),
[0015 instanced batching](../../memory/decisions/0015-instanced-batching-instance-buffer.md),
[0016 normal maps without tangents](../../memory/decisions/0016-normal-maps-without-tangents.md),
[0017 on-demand object-ID pass](../../memory/decisions/0017-on-demand-object-id-pass.md),
[0018 removed node types](../../memory/decisions/0018-removed-node-types-upgrade.md),
[0019 one sampler per material](../../memory/decisions/0019-one-sampler-per-material.md).

## Key types

| Type | File | Role |
|---|---|---|
| `Mesh`, `MeshSurface`, `ArrayMesh` | [Rendering/Resources/Mesh.cs](../../MainframeEngine/Src/Rendering/Resources/Mesh.cs) | Geometry: surfaces (submeshes) with positions, normals, UVs, indices and a default material; `Bounds` (AABB); `Version` |
| `PrimitiveMesh`, `BoxMesh`, `PlaneMesh`, `QuadMesh`, `SphereMesh`, `CylinderMesh`, `CapsuleMesh` | [PrimitiveMeshes.cs](../../MainframeEngine/Src/Rendering/Resources/PrimitiveMeshes.cs) | Generated on first use from a few exported parameters (Godot names and defaults); `FlipFaces`, `Material` |
| `MeshGeometry`, `MeshBuilder` | [MeshGeometry.cs](../../MainframeEngine/Src/Rendering/Resources/MeshGeometry.cs) | Smooth normals; consistent triangle winding for the generators |
| `Material`, `StandardMaterial3D`, `MaterialRenderState`, `AlphaMode`, `CullMode`, `ShadingMode` | [Material.cs](../../MainframeEngine/Src/Rendering/Resources/Material.cs) | Surface shading; the state that selects a pipeline |
| `Texture2D`, `TextureImportSettings` | [Texture2D.cs](../../MainframeEngine/Src/Rendering/Resources/Texture2D.cs) | Images; import settings from `.meta` (see [Asset pipeline](asset-pipeline.md)) |
| `GeometryInstance3D`, `MeshInstance3D`, `Sprite3D` | [Scene/Nodes3D/GeometryInstance3D.cs](../../MainframeEngine/Src/Scene/Nodes3D/GeometryInstance3D.cs) | Nodes; `MaterialOverride`, `CastShadows`, `ObjectId` |
| `MeshVertex`, `MeshInstanceData`, `VertexLayouts` | [Rendering/Meshes/MeshVertex.cs](../../MainframeEngine/Src/Rendering/Meshes/MeshVertex.cs) | 32-byte vertex; 80-byte instance; vertex input layouts |
| `Aabb`, `Frustum` | [Aabb.cs](../../MainframeEngine/Src/Rendering/Meshes/Aabb.cs) | Bounds, transformed bounds, frustum culling |
| `PipelineKey`, `PipelineStateCache`, `ShaderSetId` | [PipelineStateCache.cs](../../MainframeEngine/Src/Rendering/Meshes/PipelineStateCache.cs) | State-hash cache of mesh pipelines |
| `DrawSortKey`, `DrawList<T>` | [DrawList.cs](../../MainframeEngine/Src/Rendering/Meshes/DrawList.cs) | 64-bit sort keys; reusable sorted lists |
| `MeshRenderer` (internal) | [MeshRenderer.cs](../../MainframeEngine/Src/Rendering/Meshes/MeshRenderer.cs) | GPU copies (ref-counted), culling, sorting, instance writes, draws, shadow casters |
| `MeshGpu`, `MaterialGpu`, `TextureGpu` (internal) | [MeshGpuResources.cs](../../MainframeEngine/Src/Rendering/Meshes/MeshGpuResources.cs) | Per-resource GPU state |
| `InstanceBuffer`, `MaterialDescriptorAllocator` (internal) | [InstanceBuffer.cs](../../MainframeEngine/Src/Rendering/Meshes/InstanceBuffer.cs) | Per-frame-slot instance ring; material set pools |
| `PickResult`, `PickHandle`, `ObjectIdPicker` | [ObjectIdPicker.cs](../../MainframeEngine/Src/Rendering/Meshes/ObjectIdPicker.cs) | GPU picking |
| `SubViewport`, `SubViewportCompositor` | [Scene/SubViewport.cs](../../MainframeEngine/Src/Scene/SubViewport.cs), [SubViewportCompositor.cs](../../MainframeEngine/Src/Rendering/Meshes/SubViewportCompositor.cs) | Offscreen views |

## Resources

### Meshes

A `Mesh` has one or more `MeshSurface`s. Each surface is an indexed triangle list (counter-clockwise front faces)
with:

- position, normal and UV arrays, one entry per vertex;
- an optional default `Material`.

Missing normals are generated smooth (area-weighted) at upload, and missing UVs are zero. `Validate()` reports bad
data, and a mesh that fails validation is logged and skipped.

Version tracking:

- `Mesh.Version` changes on any edit, including edits to an `ArrayMesh`'s surfaces. The renderer then re-uploads
  the mesh on the next frame.
- After editing a surface's arrays in place, call `MeshSurface.NotifyChanged()`.

Primitive meshes serialize only their parameters. They regenerate when a parameter changes. All primitives use
these conventions:

- Y is up and front faces are counter-clockwise.
- The UV origin is top-left, matching Vulkan image rows and glTF.
- Shapes are centred on the origin.

| Primitive | Parameters (defaults) | Vertices |
|---|---|---|
| `BoxMesh` | `Size` (1,1,1) | 24 (one UV square per face) |
| `PlaneMesh` | `Size` (2,2) in XZ facing +Y, `SubdivideWidth/Depth` | (w+2)(d+2) |
| `QuadMesh` | `Size` (1,1) in XY facing +Z, `CenterOffset` | 4 |
| `SphereMesh` | `Radius` 0.5, `Height` 1 (ellipsoid when ≠ 2r), `RadialSegments` 64, `Rings` 32, `IsHemisphere` | (rings+1)(radial+1) |
| `CylinderMesh` | `TopRadius`/`BottomRadius` 0.5, `Height` 2, `RadialSegments` 64, `Rings` 4, `CapTop/CapBottom` | side grid + caps |
| `CapsuleMesh` | `Radius` 0.5, `Height` 2 (caps included), `RadialSegments` 64, `Rings` 8 per hemisphere | 2(rings+1)(radial+1) |

The generators add triangles through `MeshBuilder`, which orients each triangle to agree with its vertex normals.
The unit tests check the winding, outward normals, counts and bounds of every primitive.

### StandardMaterial3D

The shading is Blinn-Phong for now (see [ADR 0014](../../memory/decisions/0014-blinn-phong-now-pbr-later.md)). Like
every colour in the engine, colours are authored in sRGB and converted to linear when packed.

| Group | Properties |
|---|---|
| Albedo | `AlbedoColor` (alpha too), `AlbedoTexture` (× colour) |
| Normal map | `NormalTexture` (tangent space, OpenGL/glTF convention: +Y up), `NormalScale` |
| Specular | `Specular` (strength, default 0.3), `Shininess` (exponent, default 32): the old shape shader's values |
| Emission | `EmissionColor`, `EmissionEnergy` (can exceed 1: HDR), `EmissionTexture` |
| Transparency | `Transparency`: `Opaque`, `Cutout` (discard below `AlphaCutoff`), `Blend` (straight alpha, back to front, no depth write, casts no shadow) |
| Rendering | `ShadingMode` (`BlinnPhong`, `Unshaded`), `CullMode` (`Back`, `Front`, `Disabled`), `DoubleSided` (cull nothing, back faces lit with the flipped normal), `RenderPriority` (transparent order) |
| UV | `UvScale`, `UvOffset` |

Each property setter bumps `Material.Version`. On the next frame the renderer:

- re-uploads the parameter UBO;
- rewrites the descriptor set if a texture changed;
- re-resolves the pipelines if `RenderState` changed.

`StandardMaterial3D.Default` (white, lit, opaque) is used when a surface has no material. Other `Material`
subclasses (apart from `OutlineMaterial3D`, below) are not supported by the renderer yet: they draw with the default
material and log a warning once.

### Next passes and OutlineMaterial3D (ADR 0132)

- **`Material.NextPass`** (Godot's `next_pass`): a material drawn after this one over the same surfaces. Chains are
  followed for up to `Material.MaxPassChain` (8) materials, so a cycle stops there. Changing any `NextPass` bumps
  `Material.ChainGeneration`, which makes nodes re-resolve their chains.
- **`OutlineMaterial3D`**: Godot's common inverted-hull outline shader as a built-in material.
  - Properties: `Color` (sRGB) and `Width` (render-target pixels).
  - Front faces are culled and it is unshaded and blended, like the Godot shader, which writes `ALPHA`. It casts no
    shadow.
  - `Mesh/MeshOutline.vk.vert` pushes each vertex along its clip-space normal by `Width` pixels, using Godot's
    formula (the model-view 3×3, not the normal matrix). The surface drawn before it hides everything but the rim.
  - Hard-edged meshes show gaps at their corners: each face's vertices move along that face's own normal. Godot's
    shader does the same.

### Texture2D

Images load through `ResourceLoader` with their `.meta` import settings (see
[Asset pipeline](asset-pipeline.md#textures)). A texture's colour space can depend on its usage. With
`colorSpace: auto`, the renderer uploads it once per colour space its users need:

- `R8G8B8A8_SRGB` for albedo and emission slots;
- `R8G8B8A8_UNORM` for normal maps.

`ImportSettings` set `Srgb` or `Linear` force one space. Uploads go through the upload queue, and when
`Mipmaps` is set the mip chain is generated on the GPU by blits. Each upload gets its own sampler: filter, wrap,
and anisotropy, clamped to `IVulkanContext.MaxSamplerAnisotropy`. The renderer enables `samplerAnisotropy` when the
device has it.

Textures created in code (`FromPixels`, `FromEncoded`) are not saved with scenes. Only file-backed textures
persist, as references.

## Nodes

| Node | Draws | Notes |
|---|---|---|
| `GeometryInstance3D` | — | Base: `MaterialOverride` (all surfaces), `MaterialOverlay` (drawn over every surface, with its next passes), batched by the server (never calls `Draw`), `ObjectId` = `NodeId` |
| `MeshInstance3D` | `Mesh` | Primitives, imported models, procedural meshes |
| `Sprite3D` | a `QuadMesh` sized `Texture` px × `PixelSize` | `Modulate`, `Shaded` (default unshaded), `DoubleSided` (default true), `AlphaCut` (`Disabled` = blend, `Discard` = cutout), `Offset`. Billboarding is not supported yet |

A node's material for surface *i* is resolved in this order: `MaterialOverride`, then the mesh surface's material,
then `StandardMaterial3D.Default`.

GPU references are resolved when the node is first drawn, and again when the following change:

- its mesh object, or the mesh's upload generation;
- its override or overlay;
- any material's next pass (`Material.ChainGeneration`);
- a subclass stamp (Sprite3D).

**Extra passes** (`GeometryInstance3D.GpuExtraPasses`) are resolved with the materials:

1. each surface material's next-pass chain;
2. then the overlay and its chain, over every surface.

An extra pass becomes a draw item in the opaque or transparent list, chosen by its own material's state. Its pipeline
tests depth less-or-equal, so it lands exactly on the surface drawn before it. Extra passes cast no shadow, and the
object-ID pass skips them.

The references are released when the node is freed, or at server shutdown.

## Frame

```mermaid
flowchart LR
    P["RenderServer.PrepareFrame (before BeginFrame)<br/>sync node → MeshGpu/MaterialGpu (uploads join the frame)<br/>cull vs camera frustum · build keys · sort"] --> S["RenderShadows<br/>cull casters per pass · write their instances · one instanced draw per run per pass"]
    S --> O["RenderOffscreen<br/>SubViewports (HDR pass → ID pass → tonemap)<br/>root object-ID pass when picks are pending"]
    O --> M["RenderMain (scene pass)<br/>sky · visuals with priority &lt; 0 · opaque/cutout runs · other visuals · transparent back to front"]
```

### Build (`MeshRenderer.Prepare`, once per view per frame)

For every `GeometryInstance3D` in the world's `GeometryList` that is visible in the tree and has a mesh:

1. **Sync**: compare against the node's resolved references. When they differ, acquire the new `MeshGpu` and
   `MaterialGpu`s and release the old ones (reference-counted, shared across nodes). Each mesh and material is then
   refreshed at most once per frame: re-upload on a version change, rewrite the material set if one of its textures
   was re-uploaded.
2. **Shadow casters**, main view only: one entry per surface whose material casts shadows, with its world bounds.
   The key is (cull mode, mirrored, cutout material, mesh, surface). Casters are not culled against the camera but
   against each shadow pass (see [Shadow system](shadow-system.md#caster-culling)).
3. **Cull**: transform the mesh `Aabb` by the model matrix (Arvo's method), then test it against the camera
   `Frustum` (planes from the view-projection, Vulkan's [0, 1] depth).
4. **Keys**: one draw item per surface (`DrawSortKey`).
   - **Opaque/cutout**: `[pipeline 12 bits][material 20][mesh 20][surface 12]`.
   - **Transparent**: `[render priority 8][inverted view depth 32][pipeline 12][material 12]`. This sorts
     back to front within a priority.
5. **Sort**: sort `(key, index)` pairs with `Span.Sort`. Only keys and indices move. The `DrawList`s grow and are
   reused, so a steady-state build allocates nothing.

The per-instance mirrored flag (negative determinant) selects a pipeline whose front face is clockwise. This keeps
culling and `gl_FrontFacing` right under negative scale.

### Record

- **Preparation epoch**: `PrepareFrame` starts a new epoch, so a frame that `BeginFrame` skipped (swapchain
  recreation) never leaves stale lists for the next frame.
- **Instances**: on first use in a frame, the view's opaque and transparent instances are written to the frame
  slot's `InstanceBuffer`, in sorted order. Each instance is an 80-byte `MeshInstanceData`: the model matrix and
  the object id.
  - The `InstanceBuffer` is persistently mapped and bump-allocated from zero each frame.
  - If a frame needs more room, it switches to a buffer twice the size. The old buffer stays valid for the draws
    already recorded from it, through the deletion queue.
  - The ID pass and the colour pass share the same instances.
- **Draws**:
  - Set 0 (frame) and set 1 (shadows) are bound once; set 2 (the material) whenever the material changes.
  - Each run of equal (pipeline, material, mesh, surface) becomes one `vkCmdDrawIndexed` with
    `instanceCount = run length` and `firstInstance = base + index`.
  - Mesh vertex and index buffers are bound when the mesh changes, and the instance buffer once at binding 1.
- **Shadows**: per shadow pass, `CullShadowCasters` keeps the casters inside the light's frustum (and range) and
  writes their instances; `DrawShadowCasters` records one draw per run of equal (cull, mirrored, cutout material,
  mesh surface). `ShadowSystem.GetInstancedCasterPipeline(point, cull, mirrored, cutout)` builds the pipelines:
  positions at binding 0, the model matrix at binding 1; cutout casters also read the UV and alpha-test against
  their material (set 1 of the cutout layout). See [Shadow system](shadow-system.md#caster-culling).

### Pipelines (`PipelineStateCache`)

`PipelineKey` = (shader set, vertex layout, alpha mode, effective cull, mirrored, depth write, render pass).
`PipelineKey.ForMaterial` derives it from a material's `MaterialRenderState`:

- double-sided resolves to `Disabled`;
- blend turns depth writes off;
- for the ID shaders, blend counts as opaque and depth writes are always on.

`GetOrCreate` creates a pipeline on the first request through the persisted `VkPipelineCache`. Each pipeline gets
a dense id for the sort keys, and the pipelines live until disposal. Lookups don't allocate. Each `MaterialGpu`
caches its twelve entries (the 3 shader sets × [surface, extra pass] × [normal, mirrored]), so steady-state frames
don't even hash.

`PipelineKey.ExtraPass` marks next-pass and overlay pipelines, whose depth compare is less-or-equal instead of less.

| Shader set | Shaders | Render pass |
|---|---|---|
| `MeshLit` | `Mesh/Mesh.vk.vert` + `Mesh/Mesh.vk.frag` (alpha mode = specialization constant 0) | the scene pass; offscreen HDR targets are render-pass compatible |
| `MeshObjectId` | `Mesh/Mesh.vk.vert` + `Mesh/MeshId.vk.frag` | a prototype of the object-ID target pass (all ID targets are compatible) |
| `MeshOutline` | `Mesh/MeshOutline.vk.vert` + `Mesh/Mesh.vk.frag` (`OutlineMaterial3D` in colour passes) | the scene pass |

### Descriptor sets

| Set | Contents | Owner |
|---|---|---|
| 0 | camera (`FrameData`), lights UBO | `FrameContext` (per frame slot and view) |
| 1 | shadow uniforms + maps (`ShadowSystem` or the fallback) | shadows |
| 2 | material: b0 parameters UBO (80 B, device-local), b1 one `sampler`, b2–b4 albedo/normal/emission `texture2D` (1×1 fallbacks) | `MaterialGpu` |
| binding 1 (vertex) | per-instance model matrix + object id | `InstanceBuffer` |

The material set uses a single `sampler` with separate `texture2D`s. Before M4 the shadow set held 15 samplers and this kept the
fragment stage within MoltenVK's limit of 16 samplers
([ADR 0019](../../memory/decisions/0019-one-sampler-per-material.md)). The sampler is that of the material's
first texture. A material's set is never updated in place, because frames in flight may still bind it. Instead, a
new set is written, and the old one is freed by `MaterialDescriptorAllocator` once its frame has completed. The
allocator's pools are freeable and their live counts are exact.

## Shaders

- `Mesh.vk.vert` builds the model matrix from the instance attributes (the C# rows are the GLSL columns). The
  normal matrix is the **cofactor** of the upper 3×3, multiplied by the sign of the determinant: it handles
  non-uniform scale and mirroring without an inverse.
- `include/material.glsl` holds the material set, the UV transform, albedo and emission lookups, and
  `materialNormal`. That function reconstructs a tangent frame from screen-space derivatives of position and UV,
  solving dP/du and dP/dv with the 2×2 inverse of the UV Jacobian. No vertex tangents are needed, and mirrored UVs
  and the flipped viewport keep their handedness ([ADR 0016](../../memory/decisions/0016-normal-maps-without-tangents.md)).
- `Mesh.vk.frag` takes these steps in order:
  1. Cutout `discard` (only in cutout pipelines).
  2. Flips the normal on back faces of double-sided materials.
  3. Lights: `shadeLightsBlinnPhong` with the material's specular and shininess, or unshaded.
  4. Adds emission.
  5. Writes alpha only for blend pipelines.
- `MeshId.vk.frag` writes the object id as `uint` and keeps the cutout discard.
- `lights.glsl`: `shadeLights` (Spine) = `shadeLightsBlinnPhong(…, 0.3, 32)`. `counts.w = 1` disables shadow-map
  sampling for a view (offscreen worlds).

## Picking (object IDs)

`RenderServer.PickAsync(x, y)` returns a `Task<PickResult>` (`ObjectId`, `Node`, `Hit`) for a pixel of the main
view: framebuffer pixels, origin top-left. `RequestPick` and `TryGetPickResult` are the polling form, and
`SubViewport.PickAsync`/`RequestPick` pick within an offscreen view.

1. Each viewport has an `ObjectIdPicker`. The first frame with requests renders an **object-ID pass** into an
   `R32_UINT` + depth `RenderTarget` the size of the view. The pass draws the view's mesh draws with the
   `MeshObjectId` pipelines, and id 0 means nothing.
2. Up to 64 requested pixels are copied into the frame slot's readback buffer, after an explicit
   colour-write → transfer-read barrier (`RenderTarget.End`; MoltenVK does not honour the pass's outgoing dependency
   between encoders). A `TRANSFER → HOST` barrier makes them visible to the CPU after the fence.
3. `PrepareFrame` completes the requests once `DeletionQueue.CompletedFrame` passes that frame, about two frames
   later. It resolves each id through `SceneTree.Find(NodeId)`.

The frame never waits. Tasks complete on the render thread, at the start of `PrepareFrame`, after every result has
been gathered: continuations run inline on the main thread and may pick again or change the tree. The root view
renders the ID pass only on frames with pending picks. A `SubViewport` renders it every frame when `ObjectIds` is
set, for editor hover. A view whose colour updates are disabled still answers picks: the frame renders only its ID
pass. A view without a camera answers every pick with a miss.

## Offscreen views (`SubViewport`)

`SubViewport` is a `SceneViewport` node, so its children live in its own `World3D`, with its own cameras, lights
and environment. While it is inside a tree with `UpdateMode != Disabled` (`Once` renders a single frame), the
render server renders it after the shadow pass and before the main pass. It uses frame view *k* of the
`FrameContext`: own camera, lights and extent.

| Target | Format | Use |
|---|---|---|
| `SceneImage` (+ `DepthImage`) | `R16G16B16A16_SFLOAT` + scene depth format (sampleable) | HDR colour; render-pass compatible with the main scene pass, so every scene pipeline (sky, grid, Spine, meshes) draws into it |
| `ColorImage` | `R8G8B8A8_UNORM` | tonemapped (exposure, ACES) and sRGB-encoded by `SubViewportCompositor`, ready for UI; publish its render target once with `UiServer.RegisterTexture(name, viewport.ColorTarget!)` and show it as `<img src="engine://name"/>` |
| `ObjectIdImage` | `R32_UINT` + depth | picking (`ObjectIds` or pending picks) |

**Transparent background and readback (ADR 0136).**
- `TransparentBg` clears the HDR target to transparent black, and the tonemap keeps the scene's alpha (push-constant
  bit 1; every other view stays opaque). The result is objects over a transparent background.
- `CaptureImage(Action<FrameCapture>)` copies `ColorImage` (sRGB-encoded RGBA8) to a readback buffer after the view's
  next render (`SubViewportCapture`, like the object-ID picker's readback). The image arrives on the main thread once
  the frame's fence has signalled, which takes a frame or two.
- Godot's `get_texture().get_image()`; used for item icons. The LDR image gains `TRANSFER_SRC` usage.

There is one set of shadow maps. It belongs to the main world, so offscreen worlds are lit without shadows
(`counts.w`) — unless the main world has no visuals and a rendering `SubViewport` sets `Shadows`: the first such view
then gets the shadow maps for its own world and camera (the editor's view). Up to
`FrameContext.MaxViews - 1` (7) offscreen views render per frame.

## Compatibility

`Box3d` and `Quad` were removed. `RemovedNodeTypes` upgrades their scene entries when a scene loads:

- `Box3d` becomes a `MeshInstance3D` with a `BoxMesh` and a `StandardMaterial3D` with its `Color`.
- `Quad` becomes a `MeshInstance3D` with a back-facing `QuadMesh` (`FlipFaces`, which matches the old −Z quad)
  and a material with `CullMode.Disabled`.

The remaining properties (transform, `CastShadows`, …) apply as usual, and saving writes the new types. Games can
register upgrades for their own retired types.

## Performance

Measured on an Apple M5 with MoltenVK:

- **10 000 `MeshInstance3D`s**, one mesh and one material, with a shadow-casting sun:
  - 2 colour draws (the boxes and the floor) and up to 2 shadow draws per cascade.
  - 8.3 ms per frame (≈120 fps, the display rate with VSync off) in both Debug and Release, with validation off.
  - **0 B** of managed allocation per frame (render test `TenThousandInstancesAllocateNothingPerFrame`).
- **CPU side** ([baseline.json](../../Tests/MainframeEngine.Benchmarks/baseline.json)), for 10k instances:
  - `MeshDrawListBenchmarks.BuildAndSortOpaque10k` ≈ 0.14 ms (cull + key + sort);
  - `WriteInstances10k` ≈ 0.05 ms;
  - 0 B allocated.

## Testing

- **Unit tests** ([Tests/…/Rendering/Meshes](../../Tests/MainframeEngine.Tests/Rendering/Meshes/),
  [Scene/MeshSerializationTests.cs](../../Tests/MainframeEngine.Tests/Scene/MeshSerializationTests.cs),
  [Scene/ModelImportTests.cs](../../Tests/MainframeEngine.Tests/Scene/ModelImportTests.cs)):
  - the primitive generators;
  - `Aabb` and `Frustum`;
  - material state, keys and hashing;
  - pipeline-cache hits (with a fake factory) and allocation-free lookups;
  - sort order and allocation-free sorting;
  - `.mscene`/`.mres` round trips of meshes, materials and textures (with `.meta` settings);
  - removed-type upgrades, and missing types keeping their resources;
  - the glTF import.
- **Render tests** (MoltenVK goldens):
  - `materials`: textured, cutout, normal map, emissive, mirrored, blend;
  - `gltf`: the imported model, also mirrored;
  - `instances`: 1 000 boxes in 2 draws, self-checked;
  - `picking`: picks in the main view and a `SubViewport`, self-checked, with the view shown through a `UiDocument` `<img src="engine://picking-preview"/>`;
  - the 10k allocation gate and the 10k frame-time test (< 16.7 ms enforced in Release).
  - The pre-M3 `lit-shapes`, `multi-light` and `spine` goldens still match after the port to `MeshInstance3D`.

## Known issues

- Blended surfaces cast no shadow (cutout materials cast alpha-tested shadows since M4).
- No PBR, skinning, morph targets, LODs or GPU-driven culling (shadow casters are culled per pass on the CPU).
- `Sprite3D` has no billboard mode.
- Only `StandardMaterial3D` and `OutlineMaterial3D` are rendered. Custom shaders and other material types come later.
- Spine, the grid and the sky do not appear in the object-ID pass.
- Each `Sprite3D` owns its quad mesh and material, so sprites are not batched together. To batch many, share a
  `MeshInstance3D` with a `QuadMesh` and one material.
- Entries upgraded from removed types (`Box3d`/`Quad`) skip `[SerializedMigration]`s of their replacement type.
- Offscreen views have no shadows while the main world draws anything (one set of shadow maps; `SubViewport.Shadows`).
- Mesh data in `.mscene`/`.mres` is JSON arrays (ADR 0011). Imported models are not re-serialized: scenes
  reference the model file.

## Related docs

[GPU resources](gpu-resources.md) · [Asset pipeline](asset-pipeline.md) · [Shaders](shaders.md) ·
[Shadow system](shadow-system.md) · [Color pipeline](color-pipeline.md) ·
[Scene graph & nodes](scene-graph-and-nodes.md) · [Scene serialization](scene-serialization.md) ·
[Testing](testing.md)
