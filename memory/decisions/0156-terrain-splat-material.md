# ADR 0156 — TerrainSplatMaterial3D: splat layers in texture arrays

- **Date:** 2026-10-08
- **Status:** accepted
- **Milestone:** Gameplay toolkit G8a (G8a.14), forest slice wave 2 lane F1
- **Spec:** docs/design/future/terrain.md (Realistic profile); current state in docs/design/terrain.md and
  docs/design/materials-and-meshes.md

## Context

ADR 0153 left the Realistic terrain drawing with any `Material`, a single colour in practice. The forest needs a
ground made of several PBR texture sets (grass, dirt, rock, moss) painted by the terrain's two RGBA8 splat maps
(`TerrainData.GetSplatTexture(0/1)`), without visible tiling and without stretched textures on steep slopes.

The mesh renderer has one set-2 layout for every material: a UBO, one sampler and four images. Eight layers of three
maps each do not fit it, and the fragment stage already uses 13 images and 10 samplers (shadows 6, IBL 3, material 4
images + 1 sampler) of the 16/16 budget. Other lanes are editing `MeshRenderer.cs` at the same time.

## Decision

- **Resources.** `TerrainLayer : Resource` (albedo, normal, ORM and an optional height texture; `TilingMeters`,
  `HeightBlendContrast`, `Tint`, `NormalScale`, `Name`, `Tag`) and `TerrainSplatMaterial3D : Material` (`Layers`, at
  most 8 used; `LayerTextureSize` 1024; triplanar start/end 35°/45°; `AntiTiling`; detail/far distances 60/150 m;
  `FarTilingScale` 4; `MacroStrength` 0.15 over `MacroScaleMeters` 48). Layer changes touch the material (the
  `TreeOptions.Level` subscription pattern). Both serialize inline.
- **Terrain link.** `Terrain3D.Material`'s setter sets `TerrainSplatMaterial3D.Terrain` (runtime only), so the renderer
  reads `Terrain.Data.GetSplatTexture` and follows data replacement and painting with no other wiring; scene loads
  relink through the same setter. A material without a Realistic terrain draws layer 0. One terrain per material.
- **Packing.** The renderer (`TerrainLayerPacker`) packs the layers into three `Texture2DArray`s of
  `LayerTextureSize`²: albedo (sRGB) with the height in alpha, normal and ORM (linear), GPU mips. Images are box-filtered
  down or resampled up bilinearly with wrapping. It re-packs only when the content stamp (size, layer count, each
  texture's identity and version) changes, so tiling, tint or contrast edits only re-upload the 304-byte parameters.
- **Height source.** The task allowed albedo alpha or an ORM-derived height. We take, in order: the layer's `Height`
  texture (R), the albedo's alpha when it has one (a texel below 255; the common "albedo + height" packing), the ORM's
  occlusion, else 0.5. Downloaded texture sets (ambientCG ships displacement maps) and code-generated ones both work.
- **Own set and pipeline layout, contained.** A partial class file `MeshRenderer.TerrainSplat.cs` owns a second set
  layout (b0 UBO, b1 layer sampler, b2 map sampler, b3–b5 the three arrays, b6–b7 the two splat maps), a second
  pipeline layout built by `Frame.CreatePipelineLayout(shadows, [splat layout])` (identical sets 0/1 and push range,
  so the sets bound once per list stay valid) and a second `MaterialDescriptorAllocator` (now sized per layout).
  `MeshRenderer.cs` changes are a handful of lines: `ShaderSetId.MeshTerrainSplat` in the material's colour shaders,
  `LayoutFor(shaders)` in the pipeline factory, and binding `MaterialGpu.Splat.Set` with the splat layout in
  `DrawList`. The material keeps an ordinary default set 2 that the object-ID pass binds with the shared layout, so
  picking, shadow casters (opaque, no material) and the caster/ID pipelines are untouched. A parallel terrain
  renderer was rejected: it would duplicate culling, batching, LOD visibility, instance writes and picking.
- **Budget.** Fragment stage with the terrain set: 14 images, 11 samplers, 3 sets (≤ 16/16/4). The proposal's
  `MacroColor` map and two-layer weight array are not used (two `Texture2D`s, the terrain's own).
- **Shading** (`Terrain/TerrainSplat.vk.frag`, one file, no new include so the lock's include list is unchanged):
  - the four strongest weights near, two far (a selection over 8);
  - each layer planar on world XZ (analytic frame: no derivative tangent frame) and/or triplanar on slopes, with every
    layer going triplanar between the start and end angles (no per-layer flag);
  - hex-tiling written from Mikkelsen 2022 (random rotation and offset per triangle-grid vertex, barycentric weights
    to the 7th power favouring the brighter sample, normals rotated back) in the near band;
  - height blending `wᵢ' = max(hᵢ + wᵢ − (maxⱼ(hⱼ + wⱼ) − contrastᵢ), 0)`;
  - whiteout normal blending per projection (Golus), layers blended in world space;
  - a near/far blend between the distances (far: `FarTilingScale` × tiling, no hex), value-noise macro variation,
    `shadeLightsPbr`, `applyFog`. Every lookup is `SampleGrad` with gradients taken first.
  - No Blinn-Phong fallback (PBR exists since ADR 0150).
- **Footsteps.** `Terrain3D.SurfaceTagAt(x, z)` returns the strongest layer's `Tag` (null over water, past the layers
  or without a splat material), for the forest's `FirstPersonController.SurfaceResolver`.

## Consequences

- The forest's content wave assigns a `TerrainSplatMaterial3D` with its downloaded layers and paints weights with
  `SetWeightsFrom`; footsteps read `SurfaceTagAt`.
- GPU memory at the default size is about 128 MB for 8 layers (three RGBA8 arrays with mips); `LayerTextureSize` 512
  quarters it. BC7/ASTC waits for the M12 KTX2 work.
- Packing is synchronous on the render thread when a layer's textures change (a load-time cost, and a hitch on
  reimport).
- Painting still re-uploads whole splat maps (region uploads are G8a.15).
- Another material with its own set layout can follow the same partial-class pattern; a third would justify a
  per-material layout table in `MeshRenderer`.
