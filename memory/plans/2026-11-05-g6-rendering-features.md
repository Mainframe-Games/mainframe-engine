# Plan: G6 rendering features — LOD, decals, the rest of IBL

**Status:** done, see [log](../logs/2026-11-05-g6-rendering-features.md)
**Docs:** [Rendering features proposal](../../docs/design/rendering-features.md) (moved there when G6 shipped) (G6.4 LOD, G6.5 Decals, G6.2 IBL,
G6.1 Import; Open questions 1 and 3), [Materials & meshes](../../docs/design/materials-and-meshes.md),
[Sky](../../docs/design/sky.md), [Post-processing](../../docs/design/post-processing.md) (set 0 bindings),
[ADR 0150](../decisions/0150-pbr-shading-and-sky-ibl.md) (PBR and sky IBL as built), [ADR 0165](../decisions/0165-ssao-gtao.md),
[ADR 0206](../decisions/0206-gpuparticles3d.md), [Editor → Viewport tools](../../docs/design/editor.md#viewport-tools) (G7)
**Started:** 2026-11-05

## Goal

G6's remaining pieces on Vulkan, so the milestone row can close (or stay 🚧 only for the owner's default-shading call).
Already built and not redone: PBR (`ShadingMode.Pbr`, ORM, `OrmPacker`), sky IBL (`SkyRadiance`, `BrdfLut`), SSAO/GTAO,
3D particles, visibility ranges on `GeometryInstance3D`.

- **LOD (G6.4).** `MeshLod` levels on `MeshSurface.Lods` (index-only, or own geometry), appended after the surfaces in the
  `MeshGpu` buffers with a per-surface LOD table. `MeshRenderer.Prepare` picks a level per instance and view by projected
  error: `Error · maxScale · k / distance ≤ threshold / LodBias` with `k = viewHeight · P.M22 / 2` (perspective; ortho
  without the distance). `GeometryInstance3D.LodBias`, `rendering.meshLodThreshold` (default 1 px), shadow casters at the
  camera's level, the sort keys carry the level (3 bits). Authored `<name>_LOD0..7` sibling nodes merge on import into
  one mesh with levels, errors from the model `.meta`'s `lodErrors` (fractions of the bounds radius, default doubling
  from 1 %). `generateLods` is reserved in the meta (default false, no simplifier). Skinned instances draw LOD 0.
- **Decals (G6.5).** A `Decal` node (Godot's properties and defaults, projects along local −Y) drawn as forward decals:
  per view the 32 nearest visible decals (`MAX_DECALS` in `limits.json`) go to a decal UBO in set 0, every visible
  opaque `StandardMaterial3D` instance gets a 32-bit mask of the decals its bounds touch (a new `MeshInstanceData`
  field), and `Mesh.vk.frag` blends albedo, normal, ORM and emission before lighting, for Blinn-Phong and PBR. One
  `DecalAtlas` (shelf-packed RGBA8 with mips) at set 0. `sticker` icon; an editor box gizmo with size handles.
- **IBL rest (G6.2).** Radiance `.hdr` panoramas decoded through StbImageSharp's float path into an RGBA16F sky image;
  `Sky.RadianceSize` (Godot's `radiance_size`) and `Sky.ProcessMode` (`Automatic`, `RealTime`).
- **Import rest (G6.1.3).** `ModelImporter` reads metallic/roughness factors and the metalness, roughness and occlusion
  textures into PBR materials; model `.meta` `"shading": "pbr" | "blinnPhong"`.

## Not in scope

- Generated LODs: a mesh simplifier is a new dependency (proposal open question 1). `generateLods` is reserved only.
- Flipping `StandardMaterial3D`'s default `ShadingMode` to `Pbr` (open question 3): asked the owner 2026-11-05. Built
  only on an explicit "flip"; otherwise Not done.
- G6.6 SSAO extras (`rendering.ssaoQuality`), LOD cross-fade, decals on foliage, terrain, water, spatial shaders,
  particles, MultiMesh instances and transparent surfaces; Godot's decal `cull_mask`.

## Design notes (decided here)

- **LOD storage.** `MeshLod { Error, Indices, Geometry }` is a `Resource` so `ArrayMesh` LODs serialize inline (ADR 0011).
  `MeshGpu.Lods[s]` is an array of `LodRange(SurfaceRange Range, float Error)`; level 0 is the surface. Draw items and
  casters carry `Lod`; draws read `mesh.Range(surface, lod)`. LOD vertices with own geometry get default streams.
- **Sort keys.** Opaque `[pipeline 12][material 20][mesh 17][surface 12][lod 3]`; caster key appends `[lod 3]`.
- **View height.** `Prepare` takes the view's pixel height (main view `RenderExtent`, sub-viewports their size).
- **Decal mask.** `MeshInstanceData` grows from 80 to 84 bytes (`uint DecalMask` at 80, location 10); only the lit
  `Mesh.vk.vert`/`MeshExt.vk.vert` pipelines read it, so the other pipelines' attribute lists are unchanged.
- **Decal sampling.** Atlas at set 0 binding 9 (combined, linear-clamp, mips), UBO at binding 8. Albedo and emission
  sRGB-encoded in the atlas and decoded in the shader (proposal). `textureGrad` with derivatives taken before branches.

## Steps

- [x] **LOD data and GPU.** `MeshLod`, `MeshSurface.Lods`, `MeshGpu` LOD table and upload, sort keys. Verified by:
  `MeshLodTests` (upload ranges, validation, serialization round trip), `DrawSortKeyTests` for the new layouts.
- [x] **LOD selection.** `LodSelection` (pure function), `LodBias`, `RenderServer.MeshLodThreshold` +
  `rendering.meshLodThreshold`, casters at the camera's level, `MeshRendererStats.LodDraws`. Verified by: unit tests for
  perspective/ortho, bias, threshold, scale; settings round trip.
- [x] **Authored LOD import.** `_LODn` siblings merge; `lodErrors`, `generateLods` meta keys. Verified by: `TestModel`
  with `_LOD0`/`_LOD1`/`_LOD2` siblings imports as one mesh with two levels and the meta's errors.
- [x] **LOD render test and benchmark.** `lod` render test (golden + self-check of `Stats` per distance), validation and
  allocation gates; `MeshDrawListBenchmarks` with LOD. Verified by: `just test-render` green, PNG inspected.
- [x] **Decal limits, UBO and atlas.** `MAX_DECALS`, `DecalAtlas`, set 0 bindings 8 and 9 with fallbacks. Verified by:
  `DecalAtlasTests` (packing, rebuild only on change), shaders rebuilt, `just shaders-check`.
- [x] **`Decal` node, masks and shader loop.** Per-view cull and nearest 32, instance masks, `Mesh.vk.frag` loop.
  Verified by: `DecalSelectionTests` (frustum, nearest 32, masks, transparent gets none); `decals` render test.
- [x] **Decal editor.** `sticker` icon, box gizmo with size handles. Verified by: editor tests; a QA capture.
- [x] **Decal gates.** Allocation gate (32 decals × 1 000 instances), `DecalAssignBenchmarks`. Verified by: gates green.
- [x] **IBL rest.** `.hdr` panoramas, `Sky.RadianceSize`, `Sky.ProcessMode`. Verified by: `.hdr` decode test, a
  `pbr-ibl-hdr` render test, sky tests.
- [x] **Import rest.** PBR import and the `shading` meta. Verified by: `TestModel` with metallic-roughness textures.
- [x] **Docs and close.** Proposal content into `materials-and-meshes.md`, `sky.md`, `asset-pipeline.md` with "Changes
  from the proposal"; delete the proposal and repoint links; ADRs from 0245; milestones row; README, landing page, Demo
  screenshot. Verified by: `check.sh`, docs-site build.

## Done when

- The `mf-ship` gates pass: `just build`, the Release `-warnaserror` build, `just test`, `just test-render`,
  `just format-check`, `just shaders-check`, `check.sh`.
- `lod` and `decals` render tests exist with inspected moltenvk goldens.
- The G6 row is ✅, or 🚧 naming the default-shading flip as owner-pending.
- One PR is open with What changed / For review / Verification / Not done / Human checks.

## Open questions

- Flip the default `ShadingMode` to `Pbr`? The owner (asked 2026-11-05).
- The LOD simplifier dependency (meshoptimizer shim or in-house)? The owner; generated LODs wait for it.
