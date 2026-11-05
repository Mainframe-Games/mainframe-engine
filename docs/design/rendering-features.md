# Rendering features (G6)

## Purpose

The Gameplay toolkit's rendering milestone (G6): PBR with image-based lighting, 3D particles, mesh levels of detail,
decals and SSAO, built on Vulkan ahead of the M11 backend abstraction. This page is the map of what was built, where
each part is documented, and how it differs from the [proposal](#changes-from-the-proposal) it was built from. LOD and
decals are documented here in full; the other parts have their own pages.

| Part | Status | Documented in |
|---|---|---|
| G6.1 PBR (`ShadingMode.Pbr`, ORM, `OrmPacker`, PBR import) | ✅ ADR 0150 | [Lighting](lighting.md), [Materials & meshes](materials-and-meshes.md#standardmaterial3d), [Asset pipeline](asset-pipeline.md#pbr-materials-g61) |
| G6.2 IBL (`SkyRadiance`, `BrdfLut`, `.hdr` panoramas, `Sky.RadianceSize`, `Sky.ProcessMode`) | ✅ ADR 0150; the default flip is owner-pending | [Sky](sky.md#image-based-lighting) |
| G6.3 3D particles and billboards | ✅ ADR 0199, 0206 | [3D particles](particles-3d.md), [Billboards](materials-and-meshes.md#billboards-adr-0199) |
| G6.4 LOD | ✅ ADR 0245 (generated LODs wait for a simplifier) | [below](#levels-of-detail-g64) |
| G6.5 Decals | ✅ ADR 0246 | [below](#decals-g65) |
| G6.6 SSAO (GTAO) | ✅ ADR 0165 | [Post-processing → SSAO](post-processing.md#ssao) |

## Binding budget

Every lit surface samples in the mesh fragment stage, which has a hard budget: ≤ 16 samplers and sampled images per stage
and ≤ 4 descriptor sets (MoltenVK and the Android baseline profile, [mobile.md → rendering](future/mobile.md#rendering)).
`ImageBasedLightingTests.FragmentStageStaysWithinTheBindingBudget` holds the counts.

| Fragment stage | Images | Samplers |
|---|---|---|
| Set 0 (frame): radiance and irradiance cubes, BRDF LUT, screen-space AO, probe volume, decal atlas (combined); terrain macro (sampled image, no sampler) | 6 (+1) | 6 |
| Set 1 (shadows): cascade array, atlas, 4 point cubes | 6 | 7 |
| Set 2 (`StandardMaterial3D`): albedo, normal, emission, ORM | 4 | 1 |
| **Total** | **16** | **14** |

The terrain splat set (2 samplers, 4 images) gives 16 images and 15 samplers. Set 3 stays free. New per-view textures go
into set 0, never a new set.

## Levels of detail (G6.4)

### Data

- `MeshSurface.Lods` (`MeshLod[]`, `[Export]`): the surface's coarser levels in ascending `Error`; the surface itself is
  level 0, and a surface has at most `MeshLod.MaxLevels` (8) levels.
- `MeshLod`: a `Resource` with `Error` (geometric error in mesh units), and either `Indices` (a triangle list into the
  surface's own vertices, Godot's index-only LODs) or `Geometry` (a `MeshSurface` with its own vertices, authored LODs).
  A level is drawn with its surface's material and pipeline; own geometry uses its own vertex streams or the defaults.
- Validation (`MeshSurface.Validate`): errors finite and ascending, indices in range and a multiple of three, no LODs of
  LODs, a level's geometry skinned exactly when its surface is.
- `ArrayMesh` LODs serialize inline like the rest of the mesh (ADR 0011). Assign a new `Lods` array (or call
  `NotifyChanged`) after changing a level.

### GPU layout

`MeshGpu.Layout` places every surface's vertices and indices first (unchanged), then each surface's levels: index ranges
appended to the index buffer, own geometry appended to the vertex buffer (and to the colour, wind and skinning streams).
`MeshGpu.Lods[s][l - 1]` is a `LodRange(SurfaceRange Range, float Error)`; draws read `MeshGpu.Range(surface, lod)`. A
skinned instance's view shares the source's table.

### Selection

In `MeshRenderer.Prepare`, per instance and view, only for meshes with levels (`MeshGpu.MaxLevels > 1`):

- `k = viewHeight · |P.M22| / 2` pixels per unit at distance 1 (`LodSelection.PixelScale`: `1 / tan(fovY / 2)` for a
  perspective camera, `2 / orthoHeight` for an orthographic one); `viewHeight` is the view's render height (the main
  view's `RenderExtent`, a sub-viewport's own size).
- `distance` = camera to the closest point of the instance's world bounds (0 inside: level 0); orthographic cameras do
  not divide by it.
- `allowed = threshold / bias / (k · maxScale / distance)` in mesh units, where `maxScale` is the model matrix's largest
  axis scale, `threshold` is `RenderServer.MeshLodThreshold` (`rendering.meshLodThreshold` in `project.mfproj`, default
  1 px, 0 = always level 0) and `bias` is `GeometryInstance3D.LodBias` (default 1, higher keeps detail).
- Each surface draws its coarsest level with `Error ≤ allowed` (`LodSelection.Select`).
- **Shadow casters** draw the level the view's camera picked, computed before the camera cull, so shadows match the lit
  surface even for instances outside the view.
- **Visibility ranges** (`VisibilityRangeBegin`/`End`, Godot's) were already on `GeometryInstance3D`; they hide an
  instance and its shadow.
- `MeshDrawStats.LodSurfaceInstances` counts surface instances drawn above level 0; `GeometryInstance3D.RenderLod`
  (internal) is the coarsest level the last prepared view drew.

### Sort keys

- Opaque: `[pipeline 12][material 20][mesh 17][surface 12][lod 3]` (`DrawSortKey.Opaque`): up to 131 072 resident meshes
  (ids are dense; more only costs batching, not correctness, since runs compare the objects).
- Caster: `[cull 2][mirrored 1][cutout material 20][mesh 20][surface 12][lod 3]` (`MeshRenderer.CasterKey`).
- Equal levels stay adjacent, so instancing still merges them; runs (`SameDraw`, `SameCaster`) also compare the level.

### Authored LODs on import

Sibling mesh nodes named `<name>_LOD0` … `<name>_LOD7` (any case; `_`, `.`, `-` or a space before `LOD`) merge into one
`MeshInstance3D` named `<name>` at LOD0's place (`ModelLods.Merge`, before clips resolve their node paths). Surfaces pair
by index; a level keeps a surface's chain only while every earlier level had that surface; gaps close up (LOD0, LOD2 →
levels 0, 1). Other levels' vertices move into LOD0's space with their offsets kept (Unity's LOD groups). Their nodes and
children are dropped. Skinned meshes and groups without `_LOD0` stay as they are (a warning).

Model `.meta` settings:

- `"lodErrors": [0.01, 0.02, …]`: level n's error as a fraction of the LOD0 mesh's bounds radius; missing entries continue
  the default series (1 %, doubling per level).
- `"generateLods": false`: reserved for generated LODs (a mesh simplifier is a dependency decision, below). `true` only
  logs that it is not built yet.

### Performance

`MeshDrawListBenchmarks.BuildAndSortOpaqueWithLod10k` runs the selection for every one of 10 000 instances: ≈ 2.4 ns per
instance over `BuildAndSortOpaque10k` (+16 %, measured on a busy machine). Meshes without levels skip it.

## Decals (G6.5)

### Model

Forward decals, as Godot's Mobile renderer: the opaque fragment shaders apply the decals that touch the pixel **before**
lighting, so a decal changes albedo, normal, ORM and emission and is then lit like the surface. No pass, depth read or
render-target change.

### `Decal` node

`Decal : Node3D` (Godot's names and defaults; `sticker` icon) projects along its local −Y through a box of `Size` (default
2 × 2 × 2):

| Property | Default | Effect |
|---|---|---|
| `TextureAlbedo`, `TextureNormal`, `TextureOrm`, `TextureEmission` | null | The maps (a decal without any is skipped) |
| `EmissionEnergy` | 1 | Scales the emission |
| `Modulate` | white | sRGB tint of albedo and emission; alpha scales the coverage |
| `AlbedoMix` | 1 | How much the albedo replaces the surface's |
| `NormalFade` | 0 | Fades on surfaces turned away from the projection axis |
| `UpperFade`, `LowerFade` | 0.3 | Fade curves towards the box's top and bottom faces |
| `DistanceFadeEnabled`, `DistanceFadeBegin`, `DistanceFadeLength` | false, 40, 10 | Fades out with camera distance |

`World3D.Decals` lists a world's decals. `Decal.BoxMatrix` and `WorldBounds` give the projection box.

### Per view

`DecalSelection` (one per `MeshViewDraws`, so per view):

1. **Select:** visible decals with a texture, inside the frustum and not faded out by distance; when there are more than
   `MAX_DECALS` (32, `limits.json`), the 32 nearest (distance from the camera to the box). Fewer keep world order.
2. **Masks:** each visible opaque instance whose material takes decals (`MaterialGpu.ReceivesDecals`: opaque or cutout
   `StandardMaterial3D`, and `TerrainSplatMaterial3D`) gets the bits of the selected decals whose world bounds overlap
   its own (`MaskFor`), in `MeshInstanceData.DecalMask`. `MultiMesh` instances, blended surfaces, foliage, water,
   impostors, spatial shaders, particles, Spine and the sky get none.
3. **Upload:** the frame slot's decal UBO (16-byte header + 32 × 192-byte `DecalGpu`): the world → unit-box transform
   (three columns), the four atlas tiles, the linear modulate, the fades, the emission energy and distance fade, and the
   box's X and Y axes.

`MeshInstanceData` grew from 80 to 84 bytes: `DecalMask` at offset 80, vertex location 10, read only by `Mesh.vk.vert`
and `MeshExt.vk.vert` (the outline pipeline gets its own attribute list without it, and writes 0).

### Set 0

| Binding | What | Without decals |
|---|---|---|
| 8 | The view's decal UBO (`decalData`) | A zeroed UBO in `FrameContext` |
| 9 | The decal atlas (`decalAtlas`, linear clamp with mips) | The white 1 × 1 image |

The render server sets `FrameContext.Decals` to the view's selection before each `Begin`; `BindEnvironment` rewrites
the two bindings only when the buffer or the atlas changed.

### Atlas

`DecalAtlas` (one per `MeshRenderer`, shared by every world) shelf-packs every decal texture into one linear RGBA8
power-of-two atlas (256 … 4096) with a 4-texel edge-extended border per tile and GPU-generated mips (`DecalAtlasPacker`).
Albedo and emission keep their sRGB encoding and are decoded in the shader; normal and ORM maps are linear. It rebuilds
only when a decal shows a texture it does not hold (or that changed): with the textures it already held, unless that no
longer fits at full size, then with the world's decals alone. Textures that do not fit at all are halved (a warning).

### Shader

`include/decals.slang`: `applyDecals(mask, worldPos, dpdx, dpdy, Ngeo, inout DecalSurface)` loops over the mask's bits
(`firstbitlow`), skips pixels outside the unit box, and applies, in index order:

- the fades: `pow(1 − |2y|, upper or lower) · distance fade`, times `smoothstep(NormalFade, 1, N·up · 0.5 + 0.5)` when
  `NormalFade > 0`;
- albedo: `lerp(albedo, decode(texel) · modulate, coverage · AlbedoMix)` with coverage = fade × modulate alpha × texel
  alpha;
- normal: the map in the box's frame (X right, −Z up the image, Y out), blended by coverage (it replaces the material's
  mapped normal where it covers);
- ORM: blended by coverage (PBR surfaces);
- emission: added after lighting.

The atlas is sampled with `SampleGrad`, gradients from the world position's screen derivatives (taken before any
branch). `Mesh.vk.frag` computes the mapped normal and the ORM before the shading branches so one decal pass serves
Blinn-Phong and PBR; without decals the result is bit-identical to before. `TerrainSplat.vk.frag` applies decals after
its layer blend.

### Editor

A scene icon (`Assets/gizmos/decal.svg`) with a projection arrow, clickable to select; when selected, `DecalHandles` draws
the box and the projection arrow, with a handle on the +X, +Y and +Z faces that sets that `Size` component (the box stays
centred). One undo step per drag.

![A Decal added under the blue light: its projection box and handles in the view, its textures and fades in the Inspector](../images/editor-decal.png)

### Performance

`DecalAssignBenchmarks.Assign32x10k`: selecting 32 of 64 decals and masking 10 000 instances, ≈ 0.35 ms, 0 B. Pixels pay
one uniform test without decals; each covering decal costs one box test and up to four atlas samples.

## IBL additions (G6.2)

- **`.hdr` panoramas:** a `Sky.Panorama` ending in `.hdr` decodes through StbImageSharp's float path (`HdrImage`) into an
  `R16G16B16A16_SFLOAT` image, so the sky and the light baked from it keep values above 1. LDR panoramas still work.
- **`Sky.RadianceSize`** (Godot's `radiance_size`): 32 … 1024 pixels per cube face, default 128.
- **`Sky.ProcessMode`** (Godot's `process_mode`): `Automatic` re-bakes a changing sky at most every 4 frames, `RealTime`
  every frame it changes.

Details in [Sky → Image-based lighting](sky.md#image-based-lighting).

## Non-goals

As in the proposal: GPU particle simulation, particle collision and trails; particle shadows; HLOD, impostors (G8b built
its own for trees), LOD cross-fade and automatic LOD for skinned meshes; clustered or tiled lighting; SSR, SSIL, SDFGI,
VoxelGI, lightmaps, reflection probes; custom material shaders beyond ADR 0205; Burley diffuse, clearcoat, anisotropy,
subsurface and sheen.

## Testing

- **Unit:** `MeshLodTests` (layout, validation, selection for perspective and orthographic cameras, bias, threshold,
  scale, sort keys), `MeshLodSettingsTests` (`meshLodThreshold` round trip), `MeshSerializationTests` (LODs and
  `LodBias`), `ModelLodImportTests` (`_LODn` merge, `lodErrors`, gaps), `ModelPbrImportTests` (factors, packed and
  separate ORM, the `shading` setting), `DecalTests` (defaults, fades, selection, nearest 32, masks, the UBO entry, the
  atlas packer), `HdrSkyTests`, `ImageBasedLightingTests` (binding budget), `HandleTests.DecalHandlesSizeTheBoxOnEachAxis`.
- **Render:** `lod` (golden; levels grow with distance at both pixel scales, a low bias, an index-only level, a
  visibility range), `decals` (golden; atlas rebuilt once, four decals selected), `pbr-ibl-hdr` (golden; RGBA16F sky),
  with allocation gates for `lod` and a moving decal.
- **Benchmarks:** `MeshDrawListBenchmarks.BuildAndSortOpaqueWithLod10k`, `DecalAssignBenchmarks.Assign32x10k` (no
  `baseline.json` entries yet: record on the quiet machine).

## Changes from the proposal

- **PBR stays opt-in.** `StandardMaterial3D`'s default `ShadingMode` is still `BlinnPhong`, and model import's `shading`
  setting defaults to `blinnPhong`, until the owner answers proposal open question 3 (flip the default, with a
  `[SerializedVersion(2)]` migration and re-recorded goldens). `"shading": "pbr"` per model opts in.
- **One ORM texture, not three.** As built in ADR 0150, `StandardMaterial3D` has `OrmTexture` (and `AmbientOcclusion`)
  instead of per-map textures with `TextureChannel`s; the importer packs glTF's maps with `OrmPacker.PackGltf`.
- **Assimp's slots (open question 4):** the bundled Assimp reports glTF's `metallicRoughnessTexture` as `Metalness` /
  `DiffuseRoughness` and its `occlusionTexture` as `Lightmap`; the importer also tries `Unknown` and `AmbientOcclusion`.
- **Imported roughness without a factor** comes from the Blinn-Phong shininess (√(2 / (n + 2))) instead of 1.
- **Sky defaults:** `RadianceSize` defaults to 128 (Godot's is 256) so existing PBR goldens keep matching; the process
  modes are `Automatic` (throttled re-bake) and `RealTime`, without Godot's high-quality incremental mode.
- **`Decal` is a `Node3D`**, not a `VisualInstance3D` (it draws nothing itself, so it carries no `CastShadows` or render
  priority).
- **Decals reach terrain too** (`TerrainSplatMaterial3D`), not only `StandardMaterial3D` surfaces; `MultiMesh` instances
  get none (their instance data is uploaded once, not per frame).
- **`DecalGpu` is 192 bytes** (it carries the box's X and Y axes for the normal frame), not 176.
- **The atlas is shared** by every world of the renderer rather than built per world, and keeps the textures it held
  across rebuilds while they fit.
- **LOD draws** take their level from `MeshGpu.Range`; the proposal's per-`SurfaceRange` table became `MeshGpu.Lods`.
  Skinned instances can use authored levels (their skinning covers the level vertices).
- **Visibility ranges** existed already (built with G8b's trees); G6.4 kept them.
- **SSAO** was built before M11 (ADR 0163, 0165), against the proposal's "after M11 step 1"; `rendering.ssaoQuality` is
  not built.

## Open questions

1. **Default shading** (proposal question 3): flip `StandardMaterial3D` and model import to PBR? The owner.
2. **LOD simplifier** (question 1): meshoptimizer through a `Native/` shim, or an in-house quadric simplifier, for
   `generateLods`. The owner.
3. **Vertex tangents** for PBR normal maps (question 2): still the derivative frame (ADR 0016) until an asset shows the
   seams.

## Related docs

[Materials & meshes](materials-and-meshes.md) · [Lighting](lighting.md) · [Sky](sky.md) ·
[Asset pipeline](asset-pipeline.md) · [3D particles](particles-3d.md) · [Post-processing](post-processing.md) ·
[Shaders](shaders.md) · [Testing](testing.md) · [Mobile core (M12)](future/mobile.md) ·
[Rendering backend abstraction (M11)](future/rendering-backend-abstraction.md) · ADRs 0150, 0165, 0199, 0206, 0245, 0246
