# ADR 0070 — Cascaded shadow maps: practical splits, sphere fit, texel snapping

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M4 (W4 lane m4)

## Context

Before M4 the directional shadow was a fixed ±20-unit orthographic box at the world origin. It did not follow the
camera, and its resolution was the same near and far. Shadows v2 asks for cascades for the primary directional light
that are stable: no shimmering edges while the camera moves or rotates.

## Decision

- **Which light:** the first directional light with `CastsShadows` gets up to 4 cascades (`CascadeCount`), in one
  2D-array image. Each layer is `ShadowResolution` (default 2048).
- **Splits:** the practical scheme, `λ·log + (1−λ)·uniform` with λ = 0.75 (`CascadeSplitLambda`). They span the
  camera's near plane to `min(far, MaxShadowDistance = 100)`.
- **Fit:** each cascade fits the **bounding sphere** of its frustum slice, computed in view space. The radius
  depends only on the projection and the split depths, so the cascade never changes size when the camera rotates.
  Fitting the slice's light-space box would change size with the angle and shimmer.
- **Snapping:** the sphere centre is **snapped to the texel grid** in a light space that depends only on the light
  direction (x, y and depth). The orthographic window is ±radius, so texels are fixed in the world: sub-texel camera
  moves change nothing, and larger ones shift the map by whole texels.
- **Near plane:** it is pulled back towards the light to the front of the casters' bounds: at least 10 units, for
  Spine and other unbounded casters, and at most 1000. It is rounded to whole units.
- **Selection:** the shader picks the cascade by view depth. It blends into the next one over `CascadeBlend`
  (default 0.1) of each cascade, and fades to lit over the last band.
- **Secondary directional lights:** they get one atlas tile of `ShadowResolution / 2`, fitted and snapped the same
  way over the whole shadow distance.

## Consequences

- The sphere wastes some resolution compared with a tight box: about 30 % of the map's area lies outside the slice.
  This is the price of stability.
- Unit tests pin the stability (`SubTexelCameraMovesLeaveTheSnappedMatrixUnchanged`,
  `LargerMovesShiftTheMapByWholeTexels`). The `shadow-shimmer` render test checks it on the GPU: a one-pixel,
  sub-texel camera move gives the same frame shifted by one pixel. Without snapping, more than 1 % of the pixels
  change.
- `StableCascades = false` exists only to demonstrate the difference.
