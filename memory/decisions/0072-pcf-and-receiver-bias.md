# ADR 0072 — PCF filtering and receiver-side bias

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M4 (W4 lane m4)

## Context

M1 sampled one hardware comparison tap for 2D maps and one hard tap for cubes, with constant shader biases (0.001
depth, 0.015 distance). Edges were stair-stepped, and acne or peter-panning depended on the scene. The plan's default
for the open question was "PCF only, no EVSM".

## Decision

- **Filters:** `ShadowSystem.Filter` is one of:
  - `Hard`: 1 tap;
  - `Pcf3x3`: 9 taps;
  - `Poisson16` (default): 16 taps in a Poisson disc of `FilterRadius` texels (default 1.5).

  Every tap is a linear comparison sample: a 2×2 hardware PCF where the depth format filters. Cubes use a
  `samplerCubeShadow` and a 20-tap disc, or 1 tap in `Hard` mode.
- **No per-pixel rotation:** the Poisson taps are fixed. Screen-space rotation noise would move with the camera and
  defeat cascade stabilisation (ADR 0070).
- **Receiver bias:** before projecting, the surface point moves towards the light by `ShadowBias` texels
  (default 0.5), and along the geometric normal by `ShadowNormalBias` texels (default 1.5) × sin(angle to the
  light). Here a texel is its world size at that point: constant for orthographic maps, proportional to the distance
  for spots and cubes. The same rule serves every map type and every cascade, so the bias scales per cascade
  automatically.
- **Raster bias:** the casters keep a slope-scaled depth bias as dynamic state (`DepthBiasConstant` 1.25,
  `DepthBiasSlope` 1.75; 0 for cubes, which write linear distance).
- **Per light:** both biases are settings on each light, exported on the light nodes.

## Consequences

- Soft, stable edges without acne in the test scenes and the Sandbox. Contact shadows stay attached, because the
  normal offset vanishes for surfaces facing the light.
- Shading cost: up to 16 taps per directional/spot light and 20 per point light. Measured GPU time for the whole
  shadow pass plus sampling stays within the 120 fps budget.
- EVSM or contact hardening (PCSS) could come later behind the same `Filter` setting.
