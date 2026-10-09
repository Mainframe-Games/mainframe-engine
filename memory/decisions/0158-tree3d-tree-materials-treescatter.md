# ADR 0158 — Tree3D, tree materials and TreeScatter

- **Date:** 2026-10-08
- **Status:** accepted
- **Milestone:** Gameplay toolkit G8 (forest slice, wave 2, lane G)
- **Spec:** docs/design/future/procedural-trees.md (`Tree3D`, styles, wind, LOD chain, scatter integration);
  docs/superpowers/plans/2026-10-08-forest-vertical-slice.md (shared contracts)
- **Docs:** docs/design/procedural-trees.md, docs/design/materials-and-meshes.md, docs/design/water.md

## Context

Wave 1 ported Ez Tree (ADR 0152: `TreeGenerator`, presets, `TreeMeshData` with `Custom0` and LowPoly `Colors`) and
built the rendering pieces (ADR 0150 PBR and sky IBL; ADR 0151 vertex streams, `MultiMesh`, visibility ranges,
`FoliageMaterial3D` with wind and swaying cut-out casters). Nothing drew a generated tree: no node, no materials, no
LODs, no forest. The forest slice needs about 2 000 trees across a 256 m valley at a playable frame rate, with trunks
the player cannot walk through. Impostors, the tree inspector and G6.4's screen-space LOD are deferred.

## Decision

- **Mesh wiring.** `ToSurface`/`ToArrayMesh` put `Custom0` and `Colors` into `MeshSurface`'s streams; `River3D` puts
  its ribbon's `Custom0` (depth, flow, foam) there too.
- **`Tree3D`** (`[Tool]` `Node3D`): `Options` or `Preset`, `Seed` (−1 = the options'), `Style`, `BakedMesh`, material
  overrides, `CastShadows`, `Collision`, and the LOD distances `Lod1Distance` 30 m / `Lod2Distance` 75 m (Ez Tree's 100
  and 250 units × 0.3) / `MaxDistance`.
  - **LODs as children with visibility ranges**, not G6.4 `MeshLod`s (not built): three internal, unsaved `TreeLod3D`
    (`MeshInstance3D`) children with contiguous ranges (`TreeMesh.LodRange`).
  - **One bounds for all levels.** The renderer measures range distances to the centre of each instance's bounds, and a
    tree's levels differ in bounds, so near a switch two levels or none drew. `GeometryInstance3D` gains `CustomAabb`
    (Godot's `custom_aabb`, runtime only; two lines in `MeshRenderer.Prepare`); every level gets the union.
  - **Regeneration** on ready, after property or `Options` changes (at most once per frame; the node processes only
    while dirty), or `Regenerate()`. Trees naming a preset share the preset's options and, per seed and style, one
    generated `TreeMesh` (weak cache), so equal trees batch.
  - **Bake** writes a `TreeMesh` `.mres` (a new resource): the three `ArrayMesh`es without materials, the trunk
    capsule, the options inline, seed, style and generator version. Materials are not saved because the packed ORM
    texture is code-made (not saved with scenes); the options rebuild them. No inspector UI, no staleness check.
  - **Trunk collision**: an internal `StaticBody3D` + `CapsuleShape3D` from `TreeMeshData.Trunk`, on by default.
- **Materials** (`TreeMaterials`, shared per look):
  - **Realistic bark is a PBR `FoliageMaterial3D`, not a `StandardMaterial3D`, and it sways.** The leaves' branch bend
    leans each vertex downwind by `Custom0.x²`; a leaf carries its twig's weight and phase, so bark with the same bend
    keeps the leaves on their twigs, while the trunk (weight ≈ 0 at the base) barely moves. Static bark would need the
    leaves' bend off (Ez Tree's look: only leaves flutter). Checked in close-ups at wind strength 2.5.
  - So that bark keeps its roughness map, `FoliageMaterial3D` gains `OrmTexture` (set 2's existing ORM slot; the foliage
    fragment shader's PBR path multiplies roughness, metallic and the vertex AO by `materialOrm`). `OrmPacker` packs the
    ambientCG `_Roughness.jpg` (R channel) into a linear ORM texture (AO 1, metallic 0) once per bark set.
  - Realistic leaves: PBR `FoliageMaterial3D`, cut out, `BackFace.Flip`, translucency 0.5, roughness 0.65.
  - LowPoly: flat PBR `StandardMaterial3D`s in the palette colours; leaves multiplied by the blob vertex colours. No wind.
- **`TreeScatter`** (`[Tool]` `Node3D`) with `TreeSpecies` resources (options or preset, variant seeds or baked
  variants, style) and `TreePlacement`s (position, yaw, scale, species; saved as a flat float array):
  - bucketed into `ChunkSize` (32 m) chunks; one `TreeScatterBatch3D` (`MultiMeshInstance3D`) per chunk, species
    variant and level, with the level's range and the union `CustomAabb`: chunk-granular LOD, static instance buffers,
    nothing per frame. A placement's variant is a hash of its position.
  - **Collision for every tree**: one static body per chunk with a capsule per tree. Measured: +15 ms of build and
    +0.01 ms per physics frame for 2 000 trees with a character walking among them, so no player-radius streaming.
  - **Shadows: `ShadowMaxLod` = 1 by default** — level-2 chunks (from 75 m) do not cast. They put about 500 trees into
    the last cascade at the end of the 100 m shadow range, for shadows distance and fog hide (−10–15 % shadow GPU).
    Per-cascade culling is by batch bounds: level-2 batches never reach cascade 0 (a render self-check), but near
    cascades draw whole 32 m chunks (about 180 trees in cascade 0); 16 m chunks halved that with no measurable gain.
- **Measured** (Release, M5, 640×480, 2 000 trees, fly-through at eye height): CPU 0.75–0.9 ms per frame, 0 B
  allocated; frame 26–27 ms with the default High sun shadows (4 × 2048², 100 m), of which the shadow pass is 23–24 ms;
  about 13 ms with 4 × 1024², 15.6 ms with 2 × 2048² to 60 m, 21 ms with 3 × 2048², display-capped 8.4 ms without
  shadows (at 1920×1080: 30 ms default, 17.8 ms with 4 × 1024²). Alpha-tested leaf cards defeat the tile GPU's hidden-surface removal, so shadow cost follows cascades ×
  resolution² × canopy overdraw; the wind's vertex work is about 10 %.

## Consequences

- The forest scene (wave 3) should place trees with `TreeScatter`, bake its variants, and run its sun at 2–3 cascades
  or 1024² maps to hold 60 fps; impostors (G8b.6) and coarser per-level shadow casters (Godot's `ArrayMesh.shadow_mesh`)
  are the structural fixes.
- `FoliageMaterial3D.OrmTexture` changes `Foliage.vk.frag` (PBR path only): existing foliage without the map shades as
  before (the 1×1 white fallback); lane F2's grass and the foliage goldens are unaffected.
- `GeometryInstance3D.CustomAabb` is available to any HLOD setup; it is not serialized.
- Not in this slice: impostors, the tree inspector and icons, `RuntimeGeneration` modes, bake staleness, `ForceLod`,
  visibility-range fades, LowPoly wind, `BarkTextureScale.y`, worker-thread generation.
- Tests: `Tree3DTests`, `TreeScatterTests` (including a collision cost report), `OrmPackerTests`; render tests
  `tree-realistic` (windy vs still), `tree-lowpoly`, `tree-forest` goldens, a 2 000-tree allocation gate and CPU bar.
