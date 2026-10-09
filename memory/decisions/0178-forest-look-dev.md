# ADR 0178 — Forest look-dev against Unreal references: low back-light, warm haze, muted greens, AgX, flowers, dust

- **Date:** 2026-10-10
- **Status:** accepted
- **Milestone:** Gameplay toolkit G8 (G8e look-dev, after G8e.7's art pass, ADR 0175, and histogram exposure, ADR 0177)
- **Spec:** docs/design/forest.md → Look-dev (ADR 0178)

## Context

After the art pass the Forest still did not read like an Unreal scene. Brogan gave five screenshots of Unreal Engine
forest scenes (third-party images: kept out of the repo; described here in words only) and asked for their mood, colours
and lighting, not a pixel match:

1. A sunlit meadow of tall grass in front of a stand of tall pines, the sun low behind them, the air between glowing.
2. A pile of grey, mossy boulders in a clearing, the trees behind fading into a bright, warm haze.
3. A path between white-barked birches, the floor of grass, ferns and leaf litter, backlit.
4. A close-up of a fallen, mossy log among ferns, a shallow depth of field, the sun through the leaves behind.
5. A misty glade: light shafts through tall trunks, a very bright, soft background.

Measured with the same metrics (frame scaled to 160 × 90; luma p5 / p50 / p95; mean HSL saturation): luma 22–54 / 70–105
/ 146–228, saturation 0.15–0.36, almost no pixel below luma 16. Our R1–R7 had saturation 0.37–0.48 and crushed shade
(R6: 17 % of pixels below luma 16, the start view 9.6 %). What makes the references read as "Unreal": the camera looks
roughly towards a low sun behind trees; a warm white-grey haze fades the distance to low contrast and lifts every black;
greens are olive and sage, only back-lit leaves and grass glow yellow-green; contrast is soft; the floor is dense (grass,
ferns, flowers, mossy logs and rocks); dust hangs in the shafts.

## Decision

Forest content only; no engine code (every look feature needed exists: volumetric fog with ambient inject, AgX,
foliage translucency, the grade fitter, `SprayCards3D`).

- **Sun**: azimuth 25° (north-north-east), elevation 25° (105° / 21° before), colour (1, 0.93, 0.82). The walk's first
  leg (north from the trailhead through the glade to the fall) and most shots look into it. The sun's rotation is now
  written as Euler angles (`RotationDegrees`), readable in the scene file. Ambient 2 → 2.4. Probes re-baked.
- **Atmosphere**: far fog (0.78, 0.8, 0.77), density 0.004, height 8 m, falloff 0.05, sun scatter 0.12; volumetric fog
  density 0.01, albedo (1, 1, 0.95), anisotropy 0.65, sky affect 0.35, ambient inject 0.3. Screen-space shafts stay off.
- **Tonemap: AgX** (ACES before). G8e.7 found AgX flat in the then-dark shade; with the lifted shade and haze its soft
  shoulder and white-going highlights are the references' soft contrast, and ACES turned back-lit leaves saturated yellow.
- **Colour**: `ForestVegetation.Mute` multiplies every tree and bush leaf tint towards olive (conifers less), the aspens
  take the birch leaf (their Ez Tree preset is autumn yellow); hue jitter trees 0.04, grass 0.06, ferns 0.05; the grass,
  litter, moss and rock layer tints darker and less saturated; terrain roughness 0.8 + 0.2 r (grazing glare into the sun).
- **Grade**: `grade_from_shots.py` targets are now the references' measured statistics (p5/p50/p95 0.147/0.364/0.786,
  foliage saturation 0.457, their shade and light casts; `--reference a.png … --` re-measures them), black lift 0.03.
- **Ground cover**: `ForestFlowers.MeadowTuft` (wide 30-blade tufts with straw blades) for the meadow and tall grass;
  grass translucency 0.75 in pale yellow-green; woodland grass 1.8, ferns 0.28 (translucency 0.7); buttercup, daisy and
  heather patches (`ForestFlowers`, `Valley.Flowers`); R5's fern dell (`ValleyLayout.FernDell`: a 1.4× mossy log, a second
  log, 70 ferns in a multimesh); R6's small meadow on the outcrop (`ValleyLayout.PineGlade`).
- **Dust motes**: `ForestDust` puts 64 `SprayCards3D` specks (2 cm, white disc texture, 11 s cycle) at six places
  (`ValleyLayout.DustClouds`, `Valley.Dust`).
- **Shots** recomposed after the references (R1 meadow into the sun, R2 the fall with haze, R3 up the stream to the bridge
  between birches, R4 the pond's south shore up the misty valley, R5 the fern dell with near and far depth of field at
  `BlurAmount` 0.3 (`ReferenceShot.BlurAmount`, new), R6 pines against the sun, R7 the fall from the south bank); clearings
  and a corridor for the start view and R4. The lookout's old vista corridor stays (removing it grew 123 trees, +1.5 ms).
- **Exposure**: ADR 0177's histogram settings unchanged (scale 0.16, min luminance 0.03, centre-weighted 10–90 %,
  protection at p98 to 0.8): `HighlightWhite` 0.9 changed nothing (protection does not engage once the shade is lifted)
  and scale 0.2 pushed the medians past the references.

## Consequences

- Stats after (luma p5/p50/p95, saturation, < 16): R1 42/106/201 0.28 0 %; R2 35/74/182 0.28 0 %; R3 35/78/188 0.31 0 %;
  R4 44/106/206 0.23 0 %; R5 42/88/203 0.35 0.1 %; R6 38/102/191 0.33 0 %; R7 43/85/170 0.22 0 %; start view 30/94/207
  0.31 0 % (14/42/169 0.40 9.6 % before). Saturation is inside the references' range; nothing crushes.
- `just forest-bench` 1080p TAAU, interleaved with the feature tip: p50 17.50 / 18.70 → 18.43 / 18.44 ms, p99 23.5 / 25.2
  → 24.4 / 25.0 ms, 0 B per frame; ground cover is most of the cost. The baseline file is unchanged.
- Changing the sun, sky, ambient, trees or splat stales the probe bake: `--write-scenes`, **rebuild**, then bake (a bake
  run before the rebuild hashed the old scene from the build output and the game rendered without probes).
- Gaps (need engine or asset work): scanned trees and fern variety; a GPU particle system for real shimmering dust; a
  proper atmosphere model for aerial perspective; denser, sharper volumetric shafts (more slices, a second phase lobe);
  higher-resolution near shadows under the canopy; flowers as scanned cards.

## Alternatives considered

- **Keep ACES and push the grade harder**: the grade cannot undo ACES's saturated yellow highlights in back-lit leaves.
- **Bloom for the sky glow**: the glow at threshold 4 is only the sun; a low threshold bloomed every sunlit leaf. The haze
  gives the soft bright background instead.
- **Retint Ez Tree's presets in the engine**: changes every project's trees; the Forest mutes its own copies.
- **A particle system for the dust**: G6.3; `SprayCards3D` specks are a few hundred quads and need no engine change.
- **Higher exposure to hit the references' medians**: the haze already lifts the low end; more exposure washed R1 and R4.
