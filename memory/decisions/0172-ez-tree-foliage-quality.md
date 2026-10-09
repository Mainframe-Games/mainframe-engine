# ADR 0172 — Ez Tree foliage quality: cluster cards, hierarchical wind, bark detail, coverage mips, impostors, new species

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/fo`, finished on `forest/fo2`, forest slice wave 6)
- **Milestone:** Gameplay toolkit G8e.5 (ADR 0164's plan)
- **Spec:** docs/design/future/forest-visual-quality.md#g8e5-foliage-quality-ez-tree-extensions,
  docs/design/future/procedural-trees.md (`TreeImpostor`); current state in docs/design/procedural-trees.md#foliage-quality-adr-0172
  and docs/design/materials-and-meshes.md (FoliageMaterial3D, ImpostorMaterial3D, Texture2D)

## Context

The Forest's trees were Ez Tree's (ADR 0152/0158): one quad per leaf cut from one PNG, a leaf flutter plus a lean, plain
bark tubes, GPU-blitted mips, and Ez Tree's LOD2 out to `MaxDistance`. Distant canopies speckled away (the cut-out
leaves thinned in the mips, and single cards left the sky showing through), every tree switched level a whole 32 m chunk
at a time, the far trees' shadows cost a mesh level each, and there were only Ez Tree's species. The mesh fragment stage
was at 15 of MoltenVK's 16 sampled images (water's scene layout and terrain splat at 16).

## Decision

Everything is opt-in and the defaults draw exactly as before (every older golden is unchanged).

- **Leaf-cluster cards.** `TreeOptions.LeafMode = Cluster`: every `TwigOptions.LeafSlots` (8) leaf slots of a last-level
  branch become one crossed card pair showing a twig grown by `TreeGenerator` itself (`TwigParams`: the tree's last
  level carrying its leaves), baked on the CPU by `TreeClusterBaker` (deterministic, headless, supersampled; 8 variants)
  into albedo + coverage, card-space normal and thickness + AO atlases. Oak Medium: 840 cards instead of 5 320.
  `ClusterFromLod` keeps Ez Tree's sharp single cards on the finer levels. Deviation: the proposal baked through
  `SubViewportCapture`; a CPU rasterizer bakes without a device (tests, tools) and bit-identically everywhere.
- **Hierarchical wind.** `MeshSurface.Custom1/Custom2` (RGBA16F wind stream, binding 4): each vertex's level-1 and
  level-2 branch base and stiffness. `windHierarchy` rotates twig, branch and trunk about their pivots, with a gust
  noise rolling along the wind, before Ez Tree's flutter; colour pass, prepass (also at last frame's wind), and both
  casters run it. Deviation: the gusts are simplex noise, not a 256² texture (no set 0 vertex binding).
- **Bark detail.** `RootFlare` (buttress lobes), `CollarScale`, `BarkMoss` → `FoliageMaterial3D.MossCoverage` (up-facing,
  patchy, climbing from the roots), `BarkDetailScale` → `DetailScale` (the normal map again, UDN). Deviation: no
  `DetailLayers` array at set 2 b6: moss is procedural and the detail normal reuses the normal map, so the binding budget
  does not move.
- **Translucency and alpha.** `FoliageMaterial3D` gets a 64-byte foliage block (`MaterialParams` 160 B): translucency
  colour and scatter, a `ThicknessTexture` (emission slot: thickness-driven wrap transmission), and
  `AlphaAntialiasingMode` (Godot's) mapped onto the TAA dither (single-sample targets have no hardware A2C).
- **Coverage-preserving mips** (Castaño 2010): `TextureImportSettings.PreserveAlphaCoverage`/`AlphaCoverageCutoff`,
  CPU mip chains (`MipChain`) uploaded as provided mips; leaf images (`LeafCoverageMips`), atlases and impostors use them.
- **Octahedral impostors.** `TreeImpostor` (8 × 8 hemi-octahedral views, 128 px, albedo, normal + depth, detail),
  `ImpostorMaterial3D` (a camera-facing quad blending the three nearest views with cubed weights, lit live, with a
  prepass and a directional caster facing the light), `TreeScatter.ImpostorDistance` adds it as level 3, which
  `ShadowCoarseLod` can name. `TreeBakeCache` keeps cluster and impostor bakes on disk by the SHA-256 of their inputs.
- **Per-instance levels.** `TreeScatter.LodSelection = PerInstance`: each tree chooses its level in the vertex shader
  (`InstanceVisibility*` on a per-level material copy) and cross-fades over ±`LodFadeMargin` with complementary dither;
  instances outside the band collapse before the wind; batches draw only while one of their trees is in range.
  Deviation: Godot's per-node `VisibilityRange*Margin` / `FadeMode` are not built; `Tree3D` still switches hard.
- **Shadow density** (added while finishing): `FoliageMaterial3D.ShadowDensity`, `ImpostorMaterial3D.ShadowDensity`,
  `TreeOptions.ClusterShadowDensity`, `TreeScatter.ImpostorShadowDensity` keep a share of a leaf caster's texels by a
  stable Bayer pattern the PCF/PCSS kernel reads as partial shadow. A crossed card pair holding a twig, and one view of a
  whole tree, cast far fuller than the canopy they stand for: with clusters and impostor casters the Forest's floor and
  glades lost every sunfleck.
- **Invariant positions** (added while finishing): the prepass and colour vertex shaders round the long wind differently
  (nothing makes two modules invariant: Slang has no `invariant` qualifier, its `precise` emits no `NoContraction` here,
  and MoltenVK compiles each module with fast math), and the `EQUAL` test dropped whole cards, showing the clear colour.
  `SpirvInvariance` adds `OpDecorate Position Invariant` to the foliage and impostor colour and prepass vertex modules as
  `ShaderModuleCache` loads them, which MoltenVK honours (verified: the holes are gone). Rejected: `LESS_OR_EQUAL` with a
  depth bias and the colour pass's own discards (correct, but 3.5 ms at 1080p: the discards defeat the tile GPU's hidden
  surface removal); patching the `.spv` at build time (two build paths, `shaders.sh` and `Shaders.targets`).
- **Species.** `Birch`, `Beech`, `Spruce`, `Fir` (Small, Medium, Large) with `TreeLevel.LengthProfile` (conical
  conifers, beech's layered crown); leaves and the Birch and Beech bark painted by `TreeTexturePainter` (deviation: no
  ambientCG scans; nothing third-party). They carry 2–4× the leaves of Ez Tree's presets, so the Forest draws them as
  clusters from level 0.
- **The Forest** uses all of it: clusters from level 1 (cluster shadow density left at 1: 0.6 cost ≈ 2 ms), hierarchical wind, flare, collars, moss,
  detail normals, coverage mips, impostors from 85 m casting the coarse cascades (0.5 shadow density), per-instance
  levels over ±4 m, and the new species in 35 % of each zone's cells (`TreeNewSpeciesShare`).
- **Binding budget.** Foliage, impostor, prepass and caster pipelines use the mesh layout: 15 sampled images and 13
  samplers in the fragment stage (water scene and terrain splat stay the 16/14 maxima).

## Measurements

Apple M5, MoltenVK, `just forest-bench` at 1920 × 1080 (1 800 frames), two interleaved rounds on a busy machine (other
lanes' render tests), medians; before = feature tip 306a6183 (probes merged):

- Before p50 18.86 / p90 24.66 / p99 32.50 ms, shadows 3.61 ms, 465 draws; **after** 21.55 / 29.48 / 32.46 ms, shadows
  4.72 ms, 615 draws; after without the new species 20.77 / 29.65 / 32.59 ms; after with 4 × 2048² cascades 25.33 /
  34.84 / 42.72 ms, shadows 9.96 ms, so the Forest keeps 4 × 1024² (G8e.2's "wait for G8e.5" is answered: the impostor
  casters took the far trees out of the coarse cascades, but the near cascades' leaf cards still make 2048² cost ≈ 6 ms).
- The median's +2.7 ms is mostly the per-instance levels (≈ 2 ms; per-chunk measured 15.7 against 18.0 ms in one pair)
  and the new species' variants (0.8 ms); p99 is unchanged. Rejected on the way: biased prepassed foliage (+3.5 ms),
  cluster shadow density 0.6 (+≈ 2 ms of shadows, left off by default in the Forest); skipping the wind of collapsed
  instances took p90 from 40 to 21 ms.
- 0 B per frame: the Forest's benchmark and `tree-forest-g8e --count 2000` (120 frames after a prewarm, prepass and TAA). Validation clean in every tree scene. The first Forest load bakes 23 variants' atlases and impostors in about
  8 s; later loads read the bake cache (trees ≈ 1.1–1.3 s).
- Shots: docs/images/forest/g8e/g8e5-{r1-glade,r3-bridge,r4-vista,r5-floor}-{before,after}.jpg.

## Consequences

- Per-instance levels draw every instance of a batch whose chunk touches a level's range and collapse the others in the
  vertex shader: a GPU-driven (compute) cull would remove that waste. Each level's material copy prepares on first draw
  (the Forest and the allocation gate prewarm them).
- 2048² cascades stay off in the Forest (see above); the leaf casters are still the shadow pass's cost.
- Cluster canopies read full but soft at 20–40 m (an atlas cell is 256 × 512 px for a twig); the art pass (G8e.7) can
  raise `TwigOptions.AtlasWidth/Height` or lower `LeafSlots` per species.
- G8e.1 should sample its probes at the impostor's depth-reconstructed position; impostors on `Tree3D` and Godot's fade
  margins on any `GeometryInstance3D` are not built.
- The bake cache lives per machine; the first Forest load bakes its atlases and impostors (about 8 s).
