# ADR 0173 — Water reads the scene: a scene copy for refraction, SSR and caustics; falls with spray

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/wa`, forest slice wave 6)
- **Milestone:** Gameplay toolkit G8e.6 (water), with the G8c.3 and G8c.6–8 subsets it needs
- **Spec:** docs/design/future/forest-visual-quality.md (G8e.6), docs/design/future/water.md (`SceneTextures`, falls);
  current state in docs/design/water.md (Scene textures, Falls, Spray), post-processing.md (Water scene copy, TAA)

## Context

ADR 0159's `WaterMaterial3D` is the proposal's look without `SceneTextures`: alpha-blended over the bed, absorption from
the vertex column, the sky as the only reflection. In the Forest the pond read as a dark crater (it reflected a dimmed
sky, not the trees and banks around it), the stream's bed showed but never bent, and the 3.5 m fall was a steep ribbon.
ADR 0163 gave the renderer post stages, a depth prepass and `SceneTextures` for post effects, but nothing a surface drawn
inside the scene pass can read. The mesh fragment stage was at 15 of MoltenVK's 16 sampled images (terrain splat 16).

## Decision

- **A scene copy inside the scene pass, per view (`WaterSceneTextures`).** A view whose draws contain refracting water
  ends its scene pass after the opaque half (`RenderServer.DrawWorld`), copies the colour and the **linear view depth of
  the scene pass's own depth** into one `R16G16B16A16_SFLOAT` chain (rgb colour, a depth in metres; ≤ 6 levels: level 0
  exact, then box-filtered colour and **minimum** depth), with fragment passes, and resumes with a render pass that loads
  colour and depth and is compatible with the scene pass. The main view and every sub-viewport have their own (the
  editor shows water as the game). One image instead of the proposal's two keeps the binding budget; half-float depth is
  ≈ 0.05 % relative (2 cm at 40 m). Reading the scene depth rather than the prepass's means water needs no prepass and
  sees the grid and Spine.
- **Opt-in per material: `WaterMaterial3D.RefractionEnabled`** (default off, so every existing scene and golden is
  unchanged), with `RefractionStrength` (1 = water's IOR), `RefractionRoughness`, `ScreenSpaceReflections`,
  `CausticsTexture` / `Scale` / `Strength` / `MaxDepth`. Views without refracting water never split.
- **Bindings: set 3 of a water-scene pipeline layout.** `ShaderSetId.MeshWaterScene`: sets 0–2 are the mesh layout's
  (the material keeps its standard set; the caustic pattern rides in the emission slot), set 3 is the chain (one combined
  image sampler). Fragment stage: 16 images, 14 samplers, within MoltenVK's limits. The extra material parameters and the
  view's quality are a 64-byte push block. Without a copy the material draws its ADR 0159 pipeline, unchanged.
- **The shader** (`Water/WaterScene.vk.frag`, helpers shared with the old shader in `include/water.slang`): the true
  column from the copy (shore foam rings rocks), Snell refraction at the rippled surface with a foreground-rejection
  fallback to the unbent pixel and depth-dependent blur, caustics projected along the sun onto the bed (two scrolled
  layers of a generated pattern, `min`-blended, × the bed's sun shadow and the sunlight left after the water),
  Beer–Lambert absorption over the refracted path, screen-space reflections falling back to the sky, glints, foam, fog,
  a soft edge towards the unbent copy.
- **SSR in the water shader, against the half-resolution level.** The reflected ray is clipped to the near plane, the
  distance and the screen and marched in equal screen-space steps (perspective-correct depth) against level 1 with a
  crossing test, IGN-jittered per TAA sample, then bisected. `rendering.waterSsr` / `RenderServer.WaterSsr`: Off, Low
  (default, 16 + 4 steps, 40 m), High (28 + 6, 60 m; the Forest). A separate half-resolution pass would need the water's
  normals in a buffer first (water is forward-shaded); the half-resolution level gives the cache behaviour and the
  "nearest depth" robustness for a fraction of that work. A view-space march with quadratic steps was tried first: its
  long late steps jumped over short objects in screen space (the pillar's reflection broke up), which the screen-space
  march does not.
- **TAA:** refracting water replaces the pixel (`BlendMode.ColorOnlyReactive`: `rgb = src`, `a = dst · (1 − src.a)`)
  with reactivity 0.85 × its soft edge, so the history keeps ≈ 0.31 there (ripples and the bent bed do not smear, the
  jittered march still averages). Falls and spray use `AlphaReactive`. Other views: `ColorOnly` / `Alpha`.
- **Falls in `River3D`** (`FallSlope` 0.7, `FallMinHeight` 1 m, `FallMaterial`, `Spray`, `SprayMaterial`): a run of
  sections steeper than the slope that drops far enough leaves the ribbon; a ballistic jet (horizontal launch landing at
  the foot) is drawn by an unowned `Falls` child with **`WaterfallMaterial3D`** (streaks falling at `√(v² + 2gh)`,
  aerating to white, thinning edges, sun glow through the sheet); plunge foam fades below the foot.
- **Spray: `SprayCards3D` + `SprayMaterial3D`**, the cheap stand-in until G6.3: 6–16 camera-facing cards per fall (vertex
  billboarding from a centre-only mesh), each rising, growing and fading on its own phase, alpha from a generated mist
  noise, soft against the scene copy's depth, lit by sky and sun with forward scattering.
- **Generated textures** (CC0 by construction): `WaterTextures.Caustics` (warped Worley edges) and `WaterTextures.Mist`
  (tileable value-noise fBm).
- **The Forest** turns refraction on for the stream and the pond (High SSR), makes the pond peatier so the reflections
  read, and gets its fall and spray from `River3D`.

## Measurements (Apple M5, MoltenVK, 1920 × 1080, machine shared with other lanes' GPU jobs)

- `just forest-bench` (1800 frames): before (base 7a32e8fb) p50 17.71 / p99 48.41 ms; after, interleaved with a control
  run of the same build with refraction off: on p50 16.69 / 16.71, p99 26.19 / 38.60 ms; off p50 19.14 / 16.69, p99
  50.19 / 25.67 ms. The p50s sit at the 60 Hz frame (16.7 ms) and the spread between identical runs (2.5 ms p50, 25 ms
  p99) is far larger than the water's cost, which this machine could not resolve. By construction: one full-screen copy
  and five downsamples (≈ 22 MB at 1080p), the pass split (≈ 0 on desktop GPUs), and up to ≈ 35 taps per water pixel
  at High SSR, on the few percent of the screen water covers in the benchmark.
- 0 B per frame (`water-refraction` orbiting with TAA, `water-fall` with TAA, the Forest's benchmark); validation clean
  in every scene, main view and post-processed sub-viewport.

## Consequences

- The water-scene fragment stage is at 16 sampled images: G8e.1 must keep set 0 at five images or fewer (it plans to
  retire the contact-shadow binding and the irradiance cube), or water moves its caustics into a free channel.
- On tile-based GPUs the split stores and reloads HDR colour and depth mid-frame; `rendering.sceneTextures` (Off / Half)
  from the proposal is not built: refraction is per material for now.
- Reflections exist only for what is on screen; misses show the sky, too bright under a canopy until G8e.1's probes give
  a sky visibility to multiply by.
- Fog is applied over the refracted bed, which the copy already fogged: slightly greyer beds over long paths.
- Transparent surfaces behind water are not in the copy; water writes no depth; no underwater (back-face) shading;
  plunge pools are not carved; spray cards are a few soft sprites.
