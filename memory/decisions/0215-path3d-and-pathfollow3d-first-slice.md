# ADR 0215 — Path3D and PathFollow3D: the first G13 slice

- **Date:** 2026-10-27
- **Status:** accepted
- **Milestone:** G13 (part)
- **Docs:** docs/design/paths.md, docs/design/future/paths.md

## Context

The Neon example (ADR 0218) moves flying cars, airships and an elevated train around a city on closed loops. The
engine had `Curve3D` (rivers, ADR 0155) but nothing that follows a curve, and its `SampleBakedWithRotation` stepped
once per `BakeInterval` and flipped on vertical segments. G13 (`docs/design/future/paths.md`) designs paths, 2D
paths, moving platforms and an editor curve tool; Neon needs only the 3D nodes.

## Decision

- **`Curve3D` grows what paths need**, all off by default so rivers and the committed bake hash are unchanged:
  - `Closed`: the seam (last point's `out` to the first point's `in`) is an ordinary segment; `SegmentCount`;
    `Sample` accepts the seam index; cubic sampling wraps.
  - Baked unit tangents (central differences, wrapped across the seam) and `SampleBakedTangent`.
    `SampleBakedWithRotation` uses the interpolated tangent, so rotation no longer steps between baked points.
  - `UpVectorEnabled`: rotation-minimising up vectors by double reflection (Wang et al. 2008), and
    `SampleBakedUpVector`. A closed curve spreads its leftover twist linearly along the length (uses `MathF.Sin`/`Cos`,
    so closed up vectors are outside the bit-exact contract; open curves keep it).
  - `WrapOffset` (modulo when closed, clamp when open) and `SetSmoothPoints` (Catmull-Rom through points as Bézier
    handles, open or closed).
- **`Path3D : Node3D`** holds a `Curve3D` through `ResourceSubscription`, raises `CurveChanged` and re-places its
  `PathFollow3D` children. The editor draws every path's baked polyline (depth-tested, `DebugColor`; selected on top).
- **`PathFollow3D : Node3D`** with Godot's names and defaults: `Progress`, `ProgressRatio`, `HOffset`, `VOffset`,
  `RotationMode` (None, Y, XY, XYZ, Oriented), `CubicInterp`, `Loop`, `TiltEnabled`. Forward is −Z (ADR 0155), so
  Godot's `use_model_front` is not needed. Scale is kept.
- **Extensions**, so traffic moves without code: `Speed` (m/s), `PingPong`, `ProcessCallback` (Idle or Physics, and
  only that callback is enabled) and the `Looped` / `EndReached` signals.
- Icons `route-2` and `map-pin` join the editor atlas.

## Consequences

- Placing a follower is a few baked samples: 0 B per frame (`PathFollow3DTests.FollowingAllocatesNothing`).
- Not in this slice: `Curve2D`/`Path2D`/`PathFollow2D`, the closest-point grid and hinted query, `Reverse`,
  `AnimatableBody*` and platform velocity, and the editor curve tool. Paths are authored in code or as `.mscene`
  data until G13.6.
