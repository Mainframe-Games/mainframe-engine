# ADR 0170 — Baked light probes: SH L1 sky visibility and bounce, combined with GTAO's bent normals

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/gi2`, forest slice wave 6)
- **Milestone:** Gameplay toolkit G8e.1 (ADR 0164's plan)
- **Spec:** docs/design/future/forest-visual-quality.md#g8e1-sky-occlusion-and-bounce-light-lightprobevolume; current
  state in docs/design/lighting.md#light-probes, docs/design/post-processing.md#ambient-occlusion-binding and
  docs/design/forest.md#light-probes

## Context

The Forest's shade under the canopy was lit by the whole sky (the IBL irradiance cube), so it read flat and blue-grey
next to the sunlit glade, and water and wet rock under the trees reflected the full bright sky. ADR 0164 chose a baked
probe volume (no ray tracing or compute before M11; screen-space GI misses the canopy above the camera; 1 500 swaying,
instanced trees cannot have lightmaps), combined with lane S's GTAO (ADR 0165) at small scale. The binding budget was
the constraint: the lit fragment stage declared 15 images (terrain splat 16) of MoltenVK's 16, and water's set 3 (ADR
0173) brought the water-scene layout to 16 images and 14 samplers, so set 0 had to stay at five images.

## Decision

- **Node and resource.** `LightProbeVolume : Node3D` (`[Tool]`; Godot-style exports: `Size`, `Layout` `Box` or
  `TerrainFollowing`, `ProbeSpacing`, `LayerHeights` (≤ 8), `Terrain`, `RaysPerProbe`, `Bounces`, `Energy`,
  `SkyOcclusion`, `OcclusionTint`, `Data`, `BakeWhenStale`) and `LightProbeData : Resource` (the grid, `BakeHash`, and a
  `.probes` binary beside the `.mres`: float32 ground heights, float16 coefficients; `*.probes` is LFS).
  `GeometryInstance3D.GIMode` (Godot's `Disabled` / `Static` / `Dynamic`, default `Static`) picks what the bake traces;
  every lit surface samples the probes. The first visible volume with data in a world is used.
- **What a probe stores.** SH L1 sky visibility (transmittance to the sky per direction, apart from the sky's colour, so
  a new sky needs no re-bake) and SH L1 RGB bounce radiance: 16 floats, four `RGBA16F` texels. The GPU volume is one
  `Texture3D` of `X × (5 · layers) × Z`: four slabs plus the ground height per column (terrain-following), stacked along
  Y with the slab texel centres clamped, so hardware trilinear never blends two slabs and the layout needs one binding.
- **CPU bake** (`ProbeBaker`, `Parallel.For` over probes, deterministic for a seed whatever the thread count): proxies
  for the terrain (height field, min–max pyramid), trees and scatters (branch capsules from the Ez Tree skeleton plus a
  0.5 m leaf-density grid with Beer–Lambert transmittance and stochastic leaf events), `Static` meshes (BVH) and water.
  Visibility pass → fix-up of probes inside geometry (> 25 % back-face hits; neighbours' mean, repeated) → bounce passes
  reusing the previous pass's nearest probe for indirect light → a [1 2 1] blur. Leaves pass their
  `FoliageMaterial3D.Translucency` of the light behind them, as the foliage shader does (proxy version 2).
- **Staleness.** `BakeHash` hashes every input the bake reads (proxy geometry, materials and their translucency, sun,
  sky, grid, settings, a proxy version). `CurrentBakeHash()` fingerprints the same inputs without building proxies, so a
  committed bake is checked in ≈ 40 ms at load. Missing or stale: the world renders without probes (exactly the old
  frame); with `BakeWhenStale` the volume bakes in the background and swaps the data in when done.
- **Baking.** The editor's Bake Lighting button on the volume's inspector (background, cancel, saves
  `<scene>-lighting.mres` or over the existing data, one undo entry); `Bake()` / `BakeInBackground()` in code;
  `--bake-lighting` on any game (`GameSession.BakeLighting` at its third update: bakes every volume, saves under
  `--project`'s folder so the files can be committed, quits; works headless).
- **Binding budget: the contact shadow joins the AO image; binding 6 is the probes.** The SSAO target becomes one
  `R16G16B16A16_SFLOAT` screen-space occlusion image at set 0 binding 5: r AO, g the sun's contact shadow, b the view
  depth of the prepass surface, a GTAO's view-space bent normal (x, y quantised to 32 steps, packed into one exactly
  representable half float). The contact-shadow pass reads SSAO's output and writes the combined image; ADR 0167's
  binding 6 is retired and the probe volume takes it. Set 0 stays at five images (radiance, irradiance, BRDF LUT,
  occlusion, probes), so every layout's count is unchanged (lit 15/13, terrain splat 16/14, water scene 16/14). The
  sky irradiance cube is **not** moved to SH L2: with the fold it frees nothing that is needed now (kept as an option
  for the next binding).
- **Shading** (`include/probes.slang`, `include/indirect.slang`, called from `shadeLightsPbr` and
  `shadeLightsBlinnPhong` when `probesActive()`): sample at `P + Ngeo · 0.3 · spacing + V · 0.1`; diffuse = (sky
  irradiance around a bent normal × `probeSkyLight(visibility)` + bounce × `Energy`) × Jimenez's multi-bounce AO; the
  bent normal is N leaned to GTAO's (when SSAO ran) and then to the probe's open direction; specular = sky reflection ×
  specular occlusion (visibility along R widened with roughness) + the bounce along R for the blocked part. Foliage's
  back side passes `Translucency` × the probes' light around −N, and the vertex canopy AO keeps 40 % of its strength
  (`kCanopyAoWithProbes`). The volumetric fog's ambient in-scatter × `probeOpenSky` (every fourth march step).
  `FrameData` grows by 96 bytes to 672 (`probeOrigin`, `probeSpacing`, `probeCounts`, two layer rows, `probeTint`).
- **Art controls.** `SkyOcclusion` (Unreal's sky occlusion strength: 1 the bake, 0 no occlusion) and `OcclusionTint`
  (Unreal's occlusion tint): `probeSkyLight(v) = v + (1 − v) · (1 − SkyOcclusion) · tint`. The bake under-represents
  how much light a canopy scatters (the leaves' sub-pixel gaps, their spectral transmission), so the full physical
  occlusion read near-black next to a sunlit glade; a partial, canopy-tinted occlusion keeps the shade readable and
  green. White tint and strength 1 are the bake as it is.
- **The Forest commits its bake** (`Content/Scenes/forest-lighting.mres` + `.probes`, 4.3 MB) and sets `BakeWhenStale`,
  because its valley is generated at load: a generator change re-bakes in the background (about a minute) until the
  bake is committed again (`--bake-lighting --project Examples/Forest`). Terrain-following at 2 m over 256 m, eight
  layers to 27 m (133 128 probes; 5.3 MB on the GPU), 192 rays, two bounces, `Energy` 2.4, `SkyOcclusion` 0.5,
  `OcclusionTint` (200, 225, 170), and the look's `AutoExposureMinLuminance` 0.05 → 0.04 so the eye adapts a little further
  into the darker shade (the sunlit shots are above the clamp and do not change exposure).
- **Not built (deferred):** the probe debug view (SH spheres, invalid probes red), an "indirect only" view mode, rotated
  volumes, several volumes blended, `rendering.probes` quality levels, the sky irradiance as SH L2.

## Consequences

- **Look** (R1–R5 regenerated; before/after pairs `docs/images/forest/g8e/g8e1-*`): the canopy's undersides and the
  understory take a green tint and depth, the leaves glow (translucency of the probes' light, softened canopy AO), water
  and rock under the trees stop mirroring the bright sky, the glade keeps its light. Mean sRGB of the frame, before →
  after: R1 (114, 108, 77) → (115, 111, 73); R2 (44, 46, 23) → (34, 39, 15); R3 (32, 41, 23) → (30, 38, 16); R4 (58, 63,
  35) → (60, 70, 33); R5 (88, 79, 39) → (93, 84, 38).
- **Cost** (`just forest-bench`, 1920 × 1080, Apple M5, MoltenVK, interleaved with the volume hidden on a machine other
  lanes were using; the sun shadows read 3.5–4.9 ms against 2.0 ms quiet). Three interleaved pairs with the first
  shaders: p50 17.99 / 19.58 / 19.44 ms against 17.30 / 18.24 / 18.21 ms without probes (+0.7–1.3 ms), p99 32.2 / 33.1 /
  56.1 against 31.9 / 35.8 / 62.7 ms (noise), the volumetric fog's GPU time 1.54–1.65 against 1.31–1.34 ms. The fog
  looked the probes up at every step (+0.45 ms); every fourth step and a branchless layer lookup (no local array)
  brought the last pair, on a busier machine, to p50 20.86 against 20.92 ms and fog 1.49 against 1.41 ms: within the
  noise, near the proposal's 0.3 ms budget. The probes' shading is four or five fetches per lit pixel. 0 B per frame (render-test allocation gate with
  SSAO, contact shadows and volumetric fog on). The bake: 47.1 M rays in 59–82 s on every core (contended); ADR 0164's
  target was ≤ 60 s.
- **Defaults unchanged:** no volume means `probeOrigin.w` = 0, a 1×1×1 zero texture at binding 6 and every shader path
  as before; the SSAO and contact-shadow images change format but not values (existing goldens unchanged).
- **Tests:** unit (SH math, analytic scenes: half sky over a plane, none in a closed room, bounce off sunlit and sky-lit
  floors, Beer–Lambert canopy, fix-up and dilation, determinism across thread counts, the heightfield pyramid, the data
  file round trip, fingerprints, background bake, staleness, leaf translucency, the tint, the host flag; editor: Bake
  Lighting is one undo); render (`probes` scene: shed darkening by the predicted ratio, none without a volume, red
  bleed off a sunlit wall, a tinted shade, the allocation gate; goldens for moltenvk and lavapipe).
- **Static by design:** one sun direction per bake; moving the sun far or a large static object makes it stale.
  Dynamic objects receive but do not occlude. A DDGI-style per-frame update can reuse the probe layout once M11 adds
  compute.
