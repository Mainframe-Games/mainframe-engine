# ADR 0243 — Moving platforms, the 3D curve tool and the closest-point grid: G13 complete

- **Date:** 2026-11-05
- **Status:** accepted
- **Milestone:** G13 (G13.1 rest, G13.5, G13.6, G13.8)
- **Docs:** docs/design/paths.md (the proposal moved into it), docs/design/physics.md

## Context

ADRs 0215 and 0242 shipped the path nodes, 2D paths and the 2D curve tool. G13's proposal also asks for moving
platforms that carry characters, the curve tool in 3D views, an accelerated closest-point query, and the Forest's two
hand-written rails on `Curve3D`.

## Decision

- **Platforms carry by displacement.** A character on a kinematic or dynamic floor moves, after its own slide, by
  where that body's rigid motion takes it in the coming step: exact from the node's new pose when the platform moved
  this step, else from its last step's velocities. Not `v + ω × r` before the slide (the proposal): that drifts
  outward on a turntable (second-order error, ~17 cm over 10 s at 90°/s and 2 m) and makes a descending lift fight
  the recovery pass.
- Body records keep each step's motion of a kinematic body moved by its node (`StepVelocity`, `MovedByTarget`); its
  reported velocity is that motion until code sets one. `AnimatableBody3D`/`2D` are `StaticBody*` subclasses whose
  kind is kinematic.
- **One curve tool, two views.** `CurveEditor` takes 3D gestures through screen-space hit tests and camera-plane drags
  on an `ICurveTarget` (`Curve3DTarget` adds tilt and the curve's frame), sharing the bar, commands and
  `SetCurvePointsAction` with 2D. Alt on a point drags vertically; Alt elsewhere still orbits. Ground snap and Fit to
  Ground use the scene's first `Terrain3D` only.
- **`CurveTerrainFit.Fit`** is the river's fit, extracted (same arithmetic: `HeightAt + (−depth)` equals
  `HeightAt − depth` exactly).
- **The grid** lives in `CurveBaker` so both curves get it; it must give the full scan's answer bit for bit, ties
  included, and does (8 000 random queries). Curves under 32 segments keep the scan.
- **Forest rails** sample `Curve3D`s by arc length; the fly-over keeps its clearance lift per baked point. The
  benchmark's flight is now at a steady speed, so its frame-n poses (and `benchmark-baseline.json`) change.

## Consequences

- G13 is complete; `docs/design/future/paths.md` is gone (its content and open questions' answers are in paths.md).
- The Forest benchmark baseline must be re-recorded on the quiet reference machine.
- `IEditorViewportTool` (G8a.2) remains unbuilt; the curve tool will move onto it when it lands.
