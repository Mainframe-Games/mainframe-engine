# ADR 0257 — World-space UI: opt-in IgnoreExposure, physics-only marker occlusion, layers before panels, panels as scene geometry

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G11 (open questions 2, 5, 6, 7 of the proposal)
- **Docs:** docs/design/world-space-ui.md, memory/plans/2026-11-07-world-ui-g11-rest.md

## Context

The G11 proposal left four questions for the owner. Questions 6 and 7 shipped with defaults in ADRs 0252/0253 and were
marked "the owner may revisit"; questions 2 and 5 stayed open. The owner answered all four on 2026-11-07.

## Decision

- **Question 2 (exposure):** `Label3D` and unshaded `UiPanel3D` get an opt-in `IgnoreExposure`, off by default. It
  divides the surface's colour by the view's effective exposure (manual × the world's exposure × auto exposure), so a
  sign or terminal keeps its authored brightness while the camera adapts. The ACES tonemap still applies, so colours are
  not exact (markers on a screen layer stay the tool for exact colours). Lit (`Shaded`) labels and panels ignore it.
- **Question 5 (marker occlusion):** no depth-buffer occlusion. Markers use physics rays only (`MarkerOcclusion.Physics`);
  geometry without colliders does not hide markers.
- **Question 6 (input precedence):** keep ADR 0253: `InputBeforeUi` nodes, then screen layers, then panels, then game nodes.
- **Question 7 (panels and post):** keep ADR 0252: panels are scene geometry, so TAA, glow, exposure and the tonemap apply.

## Ruled out

- `IgnoreExposure` on by default: Godot tonemaps labels and panels, and a default that skips exposure would make dark
  scenes' signs glare.
- A depth readback for markers: it lags two frames, costs a copy per frame, and the owner prefers one occlusion model.
- Drawing panels after TAA: they would lose depth testing against the resolved scene and fog would no longer match.

## Consequences

- The CPU must know auto exposure's adapted luminance: `AutoExposure` reads its 1 × 1 result back a couple of frames
  late. During a fast adaptation an `IgnoreExposure` label trails the scene by those frames.
- Question 1 (SDF text) remains decided as coverage bitmaps; G11.9 no longer lists depth occlusion.

## Revisit when

- A game needs exact colours on world geometry (an unlit, untonemapped pass would be a new design).
- Foliage or detail without colliders must hide markers.
