# ADR 0175 — Forest art pass: terrain macro texture and TerrainBlend, instance variation, clutter, R6/R7, a fitted grade

- **Date:** 2026-10-09
- **Status:** accepted
- **Milestone:** Gameplay toolkit G8 (G8e.7, the last feature phase of the visual-quality plan, ADR 0164)
- **Spec:** docs/design/future/forest-visual-quality.md → G8e.7 Art pass

## Context

Everything of G8e.1–G8e.8 was merged (probes, shadows, volumetric fog, the cinematic post chain, foliage, water, TAAU at
0.75), and the Forest still did not read like an Unreal scene. Brogan's review, and the earlier lanes' notes:
- R1 (the glade into the sun) was hazy and washed out, its ground a uniform bright tan with sparse, dark grass tufts;
- R3 had gone too dark since the probes and the shadow changes (mean luminance 36 of 255);
- props stood on the ground with hard seams; the floor between plants was bare texture;
- a stand of one species read as copies; the grade was authored by hand before the shots existed.

The proposal asked for a terrain macro texture (RVT-lite) bound in set 0, `StandardMaterial3D.TerrainBlend`, more CC0
scans, near clutter, per-instance hue and value jitter, saplings, a denser pine slope, recomposed shots plus R6 and R7,
and a LUT authored from the renders, within about +1 ms p50.

## Decision

- **Terrain macro texture** (`TerrainMacroTexture`, `Terrain3D.MacroTextureEnabled` / `MacroTextureResolution` /
  `MacroTexture` / `RebakeMacroTexture`): the terrain's surface from above in a two-layer RGBA8 `Texture2DArray`
  (2048²: 12.5 cm over 256 m). Layer 0: √albedo and roughness; layer 1: the normal's x, z and the ground height as a
  16-bit code (blue, alpha). **Baked on the CPU**, not by rendering the splat material top-down: the splat shader's
  blend at macro scale (top-4 weights, height blending, tints, its PCG macro noise) over 32² levels of the packed layer
  images, deterministic and unit-testable, and ≈ 1.5–2 s on a worker thread at build (re-baked half a second after
  edits pause). A GPU bake would need a new pipeline variant writing two targets for a texture made once.
- **Binding: set 0 binding 7 is a sampled image without a sampler.** The brief's concern was MoltenVK's 16 per stage.
  `vulkaninfo` on the M5: `maxPerStageDescriptorSamplers` 16, `maxPerStageDescriptorSampledImages` **256**. The layouts
  were at 16/14 counting combined image samplers as both, so the binding limit is the sampler count, not the image
  count. A `SAMPLED_IMAGE` costs no sampler: the mesh layout goes to 16 images / 13 samplers, terrain splat and water
  scene to 17 / 14. The shader samples it with the material's own sampler (`materialSampler`) and clamps the UV; the
  height is rebuilt from four `Load`s. No golden changed (all 167 render tests pass unchanged). Retiring the irradiance
  cube for SH L2 (the proposal's fallback) would have changed every lit golden for nothing needed now.
- **`StandardMaterial3D.TerrainBlend`** (0 = off) and **`TerrainBlendHeight`** (0.3 m): PBR surfaces blend albedo,
  roughness and 85 % of the normal towards the macro texel within the height above its ground, the edge broken by
  two octaves of 3D noise and raised on up-facing faces; uniform test, explicit gradients. `MaterialParams` grows to
  192 bytes (`variation`, `terrainBlend`), `FrameData` to 720 (`terrainMacroRect`, `terrainMacroHeight`).
- **Per-instance colour variation** (`InstanceVariation`; `FoliageMaterial3D` / `ImpostorMaterial3D`
  `InstanceValueJitter`, `InstanceHueJitter`; `TreeScatter.InstanceValueJitter` / `InstanceHueJitter`): a PCG hash of
  the instance translation quantised to 1/16 m, applied in the vertex stage (foliage: × the vertex colour; impostor: a
  flat varying), so a tree's levels and impostor agree and nothing is stored per instance. Off by default.
- **Forest content.** Nine Poly Haven scans at 1K (root clusters, a root, bark debris, moss tufts, a stone, grass clumps,
  a dead branch, nettles; `pine_roots` was dropped for having two materials) and ambientCG's ScatteredLeaves004 as the
  debris layer, +45 MB (152 MB of the 400 MB budget), NOTICE rows, `fetch_assets.py --only/--assets`. `ForestClutter`:
  leaf-litter cards to 24 m, twigs, procedural pine cones, procedural pebbles in the stone's textures, moss tufts and
  nettles to 15–30 m, scanned grass clumps in the glade; props and clutter blend into the macro texture. Saplings in 8 %
  of the forest's gaps and the pine slope at 70 % of its cells (`TreeUnderstorey`); per-tree and per-clump variation.
  No new pine-cone scan exists on either site: the cone is procedural.
- **Shots**: R1 recomposed (lower, the sun behind a crown, a full meadow in front), **R6 Under the pines** and **R7 The
  fall, close** added (with clearings); `just forest-screenshots` renders seven; before/after JPGs `g8e7-*` (4.1 MB) from
  the feature tip at the same poses.
- **Look fixes**: the volumetric fog halved (0.018 → 0.009, noise 0.5 → 0.7) and the far fog 0.005 → 0.003 (the milk was
  the volumetric fog over the open glade looking into the sun); the glade's turf darker and greener with moss in its
  hollows and grass to 36 m; the auto-exposure minimum 0.04 → 0.02 (R3 sat on the clamp: 36 → 60).
- **Grade**: ACES kept (AgX read flat and washed in the shade). `Tools/grade_from_shots.py` fits the grade to ungraded
  renders of R1–R7 (curve through p5/p50/p95, foliage saturation, the shade's and the light's casts moved halfway) and
  generates `ForestGradeFit.cs`; `ForestGrade` applies it and `--write-scenes` writes `forest-morning.cube` (the name the
  scene already used; the proposal's `forest_morning.cube`). Resolve and Krita are not available, so the LUT is
  procedural from the renders.
- **Pre-warm** gives every geometry bounds that pass the frustum test for its eight frames: batches behind the camera
  (the pebbles at the pond) created their pipelines in play otherwise (a 70 ms hitch, 1.5 kB).

## Consequences

- Measured (Apple M5, MoltenVK, `just forest-bench`, interleaved with the feature tip): 1080p TAAU p50 16.74–16.81 →
  16.82–16.95 ms (frames quantise at 16.7 ms on this display), mean 17.1 → 18.2 ms; 2560 × 1308 TAAU p50 20.03 → 20.95 ms;
  2560 × 1308 native 27.44 → 29.03 ms, of which the understorey ≈ 1.1, the clutter ≈ 0.2, grass to 36 m ≈ 0.3, the
  terrain blend nothing measurable. p90 at 1080p 18.3–19.0 → 21.4–21.6 ms (more of the glade's low flight crosses
  16.7 ms). 0 B per frame (benchmark and `--autowalk`). A first understorey (2 700 trees) cost 3–4 ms and was cut.
- Mean luminance before → after (1600 px frames): R1 109 → 105, R2 52 → 74, R3 36 → 60, R4 64 → 93, R5 59 → 93,
  R6 23 → 37, R7 63 → 90.
- Not done: `TreeClusterShadowDensity` 0.6 (R2's glade fleck, ≈ 2 ms of shadows), twig atlas cells at 512 × 1024 (4× the
  cluster atlases, ≈ 1.2 GB for 18 species), cheaper per-instance LOD cross-fades (≈ 2 ms, ADR 0172), the macro texture
  shading distant terrain and feeding the probe baker, R7's fall still G8e.6's rectangular jet close up.
- Every change to the valley's trees or splat makes the committed probe bake stale: re-bake (`--bake-lighting`), then
  refit the grade.

## Alternatives considered

- **A layer in an existing array** (the brief's example): no existing array matches 2048² RGBA; the BRDF LUT and the
  probe volume have other shapes. Rejected for the sampler-free binding.
- **The macro in the emission slot of set 2**: per material, not per world, and the slot is in use for foliage.
- **RGBA16F layers for the height**: 64 MB instead of 32 for precision the 16-bit code already gives.
- **A separate sapling scatter without shadows**: duplicates the species' variants and atlases (load and memory) for
  ≈ 0.3 ms; fewer saplings in the main scatter instead.
