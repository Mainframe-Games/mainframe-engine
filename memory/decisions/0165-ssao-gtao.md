# ADR 0165 — SSAO is ground-truth ambient occlusion (GTAO) on the post-processing stages

- **Date:** 2026-10-09
- **Status:** accepted (implemented on `forest/s`, forest slice wave 5)
- **Milestone:** Gameplay toolkit G6.6; forest visual quality G8e.1 (its GTAO half)
- **Spec:** docs/design/future/rendering-features.md (G6.6 SSAO), docs/design/future/forest-visual-quality.md (G8e.1,
  G8e.2); current state in docs/design/post-processing.md#ssao and docs/design/lighting.md#screen-space-ambient-occlusion

## Context

Brogan asked for SSAO to improve the Forest: trunks, rocks, ferns and grass float on the ground, and the shade under
the canopy is as bright as open ground. ADR 0163 built the foundation: the depth prepass, the `AfterPrepass` stage, the
target pool, and set 0 binding 5 (`ssaoTexture`, a white 1×1 image) for the lit shaders. G6.6 designed half-resolution
GTAO with a bilateral blur and upsample; G8e.1 and G8e.2 later want the AO target to carry a contact shadow and a bent
normal as well.

## Decision

- **GTAO (Jimenez et al. 2016), not hemisphere SSAO or Godot's ASSAO.** It integrates the visible horizon analytically
  per slice, so a few samples give a cosine-weighted visibility that matches the PBR ambient term. `SsaoEffect` is an
  `AfterPrepass` effect (`PostEffectOrder.Ssao`, `Needs = DepthPrepass`), four fullscreen fragment passes:
  1. **GTAO at half resolution** (`R16G16_SFLOAT`: visibility, linear depth). Positions and normals are rebuilt from the
     prepass depth (no normal attachment); 2 slices × 4 steps each way; quadratic step spacing; falloff over the outer
     62 % of the radius. Each slice's arc is divided by the same arc without occluders, so an open surface is exactly 1
     whatever the slice count (a ratio estimator, instead of relying on the blur to average the per-pixel bias).
  2. **Denoise twice**: 4 × 4 depth-aware bilateral blurs, windows −1..2 then −2..1, each holding the 4 × 4 Bayer
     rotation pattern once. Weights come from the distance to the centre's depth plane, so grazing ground keeps blurring
     and silhouettes stop it. One pass left visible 4 × 4 blocks around grass in the Forest; two centred passes do not.
  3. **Joint bilateral upsample** to full resolution (`R8G8B8A8_UNORM`), with a closest-depth fallback for thin objects
     the half-resolution image missed; intensity and power are applied after the blur.
- **Noise is still without TAA.** The rotation pattern changes per frame only while the projection jitters (TAA's jitter
  index), so screenshots and `--fixed-fps` runs are deterministic and nothing crawls without a temporal resolve.
- **Godot's settings and names** on `PostProcessSettings`/`WorldEnvironment`, Godot's defaults: `SsaoEnabled` (false),
  `SsaoRadius` 1, `SsaoIntensity` 2, `SsaoPower` 1.5, `SsaoDetail` 0.5, `SsaoHorizon` 0.06, `SsaoSharpness` 0.98,
  `SsaoLightAffect` 0, `SsaoAoChannelAffect` 0. Their meaning on GTAO: intensity and power act on the occlusion,
  `ao = (1 − intensity · (1 − visibility))^power` (1 and 1 are the physical values); detail weights a near-field term
  from the first half of the steps (Godot's second, smaller pass, from the same samples); horizon lowers each horizon by
  that fraction of 90°; sharpness sets the bilateral depth tolerances; light affect darkens direct light; AO channel
  affect blends from `min(material AO, SSAO)` (Godot's default behaviour) to their product. `PostTonemap` ignores the
  SSAO settings, so SSAO alone keeps the engine tonemap.
- **The lit shaders take an explicit `ssao`.** `ambientOcclusionAt(SV_Position)` and the overloads
  `shadeLightsBlinnPhong(…, ssao)` / `shadeLightsPbr(s, Ngeo, worldPos, ssao)`: ambient × `ssaoCombine(m, ssao)`,
  reflections also × a specular-occlusion ratio (Lagarde and de Rousiers 2014, capped so reflections are occluded at
  least as much as diffuse), every light × `ssaoDirect(ssao)`. The two scalars live in a new `FrameData.AmbientOcclusion`
  (576 bytes now); the overloads without `ssao` (Spine) pass constants. With the white image and zero parameters every
  factor is exactly 1, so frames without SSAO are bit-identical (checked against the moltenvk goldens). Mesh (opaque and
  cutout), foliage (and its translucency), terrain splat and water (only the light scattered in the water body: the AO
  under water is the bed's) use it.
- **The output's layout leaves room.** r = AO; g reserved for G8e.2's contact shadow, b/a for G8e.1's octahedral
  view-space bent normal, all 1 now like the fallback. Widening writes more channels; nothing is rebound.
- **A debug view**: `RenderDebugView.AmbientOcclusion` (and `MAINFRAME_DEBUG_VIEW=ao|velocity` at start-up) shows the AO.
- **The Forest turns it on**, subtly: radius 0.8 m, intensity 1.1, power 1.2, detail 0.4.

## Cost

`just forest-bench` (1920 × 1080, 1800 frames, Apple M-series, MoltenVK). Other lanes were running GPU tests and
benchmarks at the same time, so the frame times are noisy (the sun-shadow GPU time, 2.04 ms on a quiet machine, read
2.4–2.6 ms during these runs):

| Configuration | p50 | p99 | SSAO GPU p50 |
|---|---|---|---|
| Before (no SSAO, no prepass) | 13.52 ms | 20.47 ms | — |
| Before, prepass forced (`MAINFRAME_DEPTH_PREPASS=1`) | 12.46 ms | 19.60 ms | — |
| SSAO on (prepass on with it), quietest run | 13.16 ms | 21.78 ms | — |
| SSAO on, three runs with GPU timestamps | 14.03–14.10 ms | 26.5–37.3 ms | 0.97–1.09 ms |

The four passes measure about 1 ms of GPU time under that contention, roughly 0.8 ms on a quiet machine (scaled like the
shadows); against the prepass-only frame that is the p50 difference too. The prepass SSAO turns on pays for itself in
the Forest (ADR 0163), so the frame with SSAO is about as fast as the frame before it. `SsaoEffect.LastGpuMilliseconds`
(`RenderServer.SsaoGpuMilliseconds`) times the passes; the Forest benchmark reports it.

## Alternatives considered

- **Hemisphere SSAO (Crytek-style) or Godot's ASSAO.** More samples or passes for the same quality; ASSAO's adaptive
  multi-pass is more code than this needs. Only the setting names come from Godot.
- **A normals attachment in the prepass.** Every prepass pipeline would change, and a second attachment costs bandwidth;
  normals from depth are good enough at half resolution.
- **Parameters in the AO image instead of `FrameData`** (e.g. g = the direct-light factor). It would take the channels
  G8e.1 and G8e.2 need. 16 bytes of `FrameData` cost nothing.
- **Bent normals and specular occlusion from the cone now.** The bent normal needs two more channels through both
  half-resolution targets and a change to `iblDiffuse`'s callers; it belongs with G8e.1's probes. Specular occlusion from
  the AO alone (Lagarde) is cheap and is done.
- **A temporal accumulation.** Waits for TAA (lane T): the rotation already varies per frame when the projection jitters.

## Consequences

- The prepass runs whenever SSAO is on (for the Forest that is a speed-up of its own, ADR 0163).
- Thin geometry is treated as infinitely thick: a fern darkens the ground behind it within the radius. A thickness
  heuristic is an option if it shows.
- Next passes and no-depth-test overlays drawn with the mesh shader read the AO of what is behind them.
- No quality setting yet (`rendering.ssaoQuality` in G6.6's design): 2 slices × 4 steps are fixed constants on
  `SsaoEffect`, passed in the push block, so the setting is a one-line change.
