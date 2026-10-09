# ADR 0164 — Forest visual quality: the "Unreal look" plan (G8e), baked probe GI, Ez Tree plus extensions

- **Date:** 2026-10-08
- **Status:** accepted (design only; no code yet)
- **Milestone:** Gameplay toolkit G8e
- **Spec:** docs/design/future/forest-visual-quality.md; frame diagram docs/images/forest-visual-quality-frame.svg

## Context

The forest slice ([forest.md](../../docs/design/forest.md), [ADR 0155](0155-curve3d-river3d-forest-project.md)) is
playable. Brogan, 2026-10-08: "I want the forest level to look like it was made in Unreal Engine." Its reference shots
show flat shade under the canopy (ambient is the sky's irradiance by normal only), 2 × 1024² sun shadows to 60 m with a
fixed filter, screen-space shafts only from visible sky, single-leaf cards, no refraction or SSR, a ribbon fall, hard
prop–terrain seams, and 18.4 ms p50 at 1440p on an M5 before any of it is fixed.

The renderer has no compute, no 3D textures and no MSAA. In flight: the post-processing system with a depth prepass,
motion vectors and jitter (ADR 0163, lane P), then GTAO (lane S) and TAA (lane T), which Brogan moved onto Vulkan ahead
of M11; `SceneTextures` refraction can follow them.

## Decision

- **Brogan approved eight ordered phases** on 2026-10-08 and asked for a proposal (G8e):
  1. precomputed sky occlusion and bounce light, combined with GTAO;
  2. shadow quality: cached cascades, PCSS, contact shadows, a far terrain shadow;
  3. volumetric light;
  4. a cinematic post chain (tonemappers, LUT, bloom, DoF, film effects);
  5. foliage through Ez Tree extensions;
  6. water: SSR, refraction, caustics, a real fall;
  7. an art pass;
  8. TAA upscaling.
- **GI is a baked `LightProbeVolume`**, not DDGI (needs ray tracing or compute every frame), SSGI (the occluding canopy
  is off screen) or lightmaps (instanced, swaying foliage cannot have them). A CPU bake traces the terrain height field,
  tree capsules, a leaf-density grid per tree variant and static meshes; each probe stores SH L1 sky visibility (re-lit
  by any sky) and SH L1 RGB multi-bounce irradiance (one sun direction) in a terrain-following grid (2 m, eight layers
  above the ground; ≈ 5 MB for the 256 m valley) in one `Texture3D`. Shading combines it with GTAO's AO and bent
  normals and adds specular occlusion. To stay within 16 fragment images, the sky irradiance cube becomes SH L2 in the
  lights UBO.
- **Static precomputation is accepted:** the valley, its trees and its sun do not move; no Lumen, Nanite, VSM or ray
  tracing equivalents are planned.
- **Foliage stays Ez Tree plus extensions**: leaf-cluster cards baked from Ez Tree's own twigs, hierarchical
  (pivot-based) wind, root flares, collars, moss and detail normals, thickness-driven translucency, coverage-preserving
  alpha mips with derivative-sharpened dithered alpha (A2C needs MSAA, which the engine lacks), dithered LOD fades,
  G8b's `TreeImpostor`, and birch, spruce, fir and beech presets. No SpeedTree; no Megascans (licensed for Unreal only):
  content stays CC0 and procedural.
- **Volumetrics are a ½-resolution screen-space ray march** of the sun's cascades with temporal accumulation; froxels
  wait for M11's compute.
- **Upscaling is in-house TAAU** in the TAA resolve (render at 0.75 for 1440p), with RCAS and FSR 1 (MIT). FSR 2/3 wait
  for compute; DLSS, XeSS and MetalFX are out.
- Settings use Godot names where Godot has the feature (`VolumetricFog*`, `Tonemapper` values, `Adjustment*`,
  `CameraAttributesPractical` DoF, `GIMode`, `VisibilityRange*Margin`, `AlphaAntialiasingMode`, `scaling3DMode`);
  everything is off by default.

## Alternatives considered

- **DDGI now:** needs per-frame ray tracing or compute ray marching. The probe layout keeps a later dynamic update
  possible.
- **Froxel fog without compute** (2D-atlas volume plus a log-step scan): workable but its 16-pixel cells smear the thin
  canopy shafts that make the look.
- **A ray-marched height-field shadow** for distance: one more fragment image and no trees; a static atlas tile of the
  terrain and impostor casters costs no binding.
- **Buying or licensing trees** (SpeedTree, Megascans): licence and pipeline cost; Ez Tree's skeleton already matches
  the parametric model.

## Consequences

- Target: 60 fps at 2560 × 1440 output on an M-series Pro and an RTX 3060 (≈ 12.4 ms estimated GPU at High).
- New engine types and data: `Texture3D`, `LightProbeVolume`, `LightProbeData`, `MeshSurface.Custom1/2`,
  `CameraAttributesPractical`; the GTAO target widens to RGBA8 (AO, contact shadow, bent normal).
- The Forest commits a probe bake keyed by its valley hash; a stale bake falls back to today's sky-only ambient.
- Waves: W5 G8e.2 and G8e.4; W6 G8e.1, G8e.3, G8e.5, G8e.6; W7 G8e.8 and G8e.7. Each phase gets its own ADR.
