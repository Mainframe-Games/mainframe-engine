# ADR 0177 — Histogram auto exposure with highlight protection

- **Date:** 2026-10-09
- **Status:** accepted (built)
- **Milestone:** Gameplay toolkit G8 (G8d.3 follow-up, the Forest's look)
- **Spec:** docs/design/color-pipeline.md#histogram-mode-adr-0177, docs/design/post-processing.md#auto-exposure

## Context

Auto exposure (ADR 0154) exposes for the mean log2 luminance of a 64 × 64 image of the scene. Brogan playtested the
Forest (2026-10-09): from the start position, looking up the path, the view is mostly canopy shade with a sun-facing
slope and treetops in direct sun, and the sunlit areas looked "way too blown out". The shade-dominated mean pins the
exposure at its maximum boost (`AutoExposureScale / AutoExposureMinLuminance`) and pushes the sunlit pixels past the
tonemap's shoulder, where they wash to cream. Commit fc93d611 worked around it (sun 2, sky 2, a stronger probe fill and a
boost cap of min luminance 0.03); the real fix is to stop exposing for the shade alone.

Proposal G8d.3 already described a 64-bin histogram with percentile clipping (Unreal's Auto Exposure Histogram). The
renderer stays on Vulkan 1.2 fragment shaders until M11 (no compute).

## Decision

- **`AutoExposureMode { Average, Histogram }`** on `PostProcessSettings` / `PostProcessProfile` (export group "Auto
  Exposure"). `Average` stays the default and is unchanged, so every existing scene and golden renders the same.
- **Histogram, all fragment passes** after the existing 64 × 64 log-luminance pass: per-row histograms into a
  64 bins × 64 rows `R32_SFLOAT` target (each fragment gathers its row's texels that fall in its bin, so nothing blends),
  column sums into 64 × 1, then a 1 × 1 adapt pass that writes the same adapted-luminance image the tonemap and glow
  already read. Bin `i` of 64 covers `AutoExposureHistogramLogMin + i·w` (−10 … 6 by default, a quarter stop per bin).
- **Unreal's settings and names:** `AutoExposureLowPercent` 10 and `AutoExposureHighPercent` 90 (the band average of
  the bin centres between the two percentiles, edge bins counted in part), `AutoExposureHistogramLogMin`/`LogMax`,
  `AutoExposureMetering` (`CenterWeighted` by default as in Unreal's histogram, weight `1 + 3·e^(−4·d²)`; or `Uniform`).
  The result is clamped by the existing min/max luminance and adapted by the existing speed.
- **Highlight protection** (`AutoExposureHighlightProtection`, off by default; `AutoExposureHighlightPercent` 98,
  `AutoExposureHighlightWhite` 2): the adapted luminance is raised to at least `scale × exposure × 2^p / white`, `p` the log
  luminance at the highlight percentile and `exposure` the manual exposure, so that percentile reaches the tonemap at most
  at the white target. It may darken past the min-luminance boost, never past the max luminance. This is what fixes
  Brogan's view: the shade still sets the exposure until the sunlit pixels would cross the target.
- **One math, two languages:** `AutoExposureHistogram` (C#: `Bin`, `MeteringWeight`, `PercentileLog`, `BandAverageLog`,
  `TargetLuminance`, `HighlightLimit`) is the unit-tested reference; `include/auto_exposure.slang` mirrors it step for step.
- **Timing:** `AutoExposure.LastGpuMilliseconds` (timestamps around the passes, SsaoEffect's pattern),
  `RenderServer.AutoExposureGpuMilliseconds`, and the Forest benchmark's `AutoExposureGpuP50Ms`.
- **The Forest** (`forest.mres`, `ForestLook.CreateProfile`): `Histogram`, centre-weighted, 10/90, highlight protection
  at the 98th percentile to 0.8. Scale 0.16, speed 0.6 and min luminance 0.03 stay; the sun, sky and probe values
  (`Lighting.Energy` 4, `SkyOcclusion` 0.15) stay, so the bake stays current.

## Alternatives

- **A 70/98 band in the Forest** (exposing for the bright end): it sets the exposure from the lit parts in every view,
  so pure-shade views (R6) and open views alike would need a different `AutoExposureScale`; protection only acts when
  highlights would clip, and leaves shade-only views (R3, R5, R6) exactly as they were.
- **Restoring min luminance 0.02** (the G8e.7 boost) now that protection guards the slopes: R3 53 → 76, R5 73 → 97 and
  R6 36 → 55 (mean sRGB luma); R6 stops reading as deep shade. Kept at 0.03.
- **White 0.9 / 1.0:** 1.0 barely binds in the start view (slope luma 183 → 182); 0.9 is halfway. 0.6 holds the slope's
  colour best (sat 0.53) but the shaded path drops to 25.
- **Compute-shader histogram with atomics:** the natural form, after M11. **Additive point scatter** into the bins needs
  `R32_SFLOAT` blending, which is not guaranteed.
- **A histogram debug view** (Unreal's HDR visualisation): not built; `AutoExposureHistogram` and the 64 × 1 target make
  it a small `RenderDebugView` later.

## Consequences

- Start view (`--resolution 1920x1080 --fixed-fps 60 --max-frames 240`), sRGB luma: frame mean 70.8 → 58.6, p95 191 →
  177, p99 222 → 214; sunlit slope (x 520–900, y 330–520) 183 → 170 with saturation 0.39 → 0.46 (rgb (189, 187, 121) →
  (177, 174, 102)); shaded path (x 450–900, y 600–1000) 51 → 38. `docs/images/forest/g8e/g8e-histo-start-{before,after}.jpg`.
- Reference shots (mean luma, before → after): R1 105 → 101, R2 66 → 56, R3 53 → 53, R4 82 → 81, R5 73 → 73,
  R6 36 → 36, R7 80 → 61. Protection binds where a sunlit slope or the fall's white water is in view (R2, R7, the start
  view); shade-only views are unchanged.
- Cost: the auto exposure passes' GPU p50 in the Forest benchmark (1920 × 1080, Apple M5, two runs each) is 0.13 ms in
  Average mode and 0.15–0.16 ms in Histogram mode (+0.02–0.03 ms); frame-time p50/p99 were within run-to-run noise
  (17.0–19.6 / 32–46 ms either way, other lanes' GPU work running); 0 B allocated.
- Histogram mode adds a 64 × 64 and a 64 × 1 `R32_SFLOAT` target and three pipelines; 0 B per frame; validation clean.
- The adapted luminance is still a 1 × 1 image no CPU reads; the tonemap and glow did not change.
