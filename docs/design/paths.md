# Paths (`Path3D`, `PathFollow3D`)

**Status:** the 3D nodes shipped (G13 first slice, [ADR 0215](../../memory/decisions/0215-path3d-and-pathfollow3d-first-slice.md)).
The rest of G13 (2D paths, moving platforms, the editor curve tool) is in the [proposal](future/paths.md).

## Overview

| Type | File | Role |
|---|---|---|
| `Curve3D` | `MainframeEngine/Src/Resources/Curve3D.cs` | Bézier resource baked to equal arc length; `Closed`, tangents, up vectors |
| `Path3D` | `MainframeEngine/Src/Scene/Nodes3D/Paths/Path3D.cs` | Holds a `Curve3D` (local space); re-places its followers when it changes |
| `PathFollow3D` | `MainframeEngine/Src/Scene/Nodes3D/Paths/PathFollow3D.cs` | Places itself and its children on the parent path at `Progress` metres |

A follower must be a direct child of a `Path3D` (Godot). Anything under the follower rides along:

```csharp
var lane = new Curve3D { UpVectorEnabled = true };
lane.SetSmoothPoints([new(0, 40, 0), new(200, 45, 0), new(200, 40, 200), new(0, 50, 200)], closed: true);
var path = new Path3D { Curve = lane };
var car = new PathFollow3D { Speed = 18, RotationMode = PathRotationMode.Oriented };
car.AddChild(carMesh);
path.AddChild(car);
```

## `Curve3D` additions

- **`Closed`** adds the segment from the last point back to the first (`SegmentCount` = point count). The seam is an
  ordinary segment: sampling, tangents and cubic interpolation are continuous across it. `Sample(index, t)` accepts
  the seam index.
- **Tangents** are baked per point (central differences, wrapped across a closed seam). `SampleBakedTangent` blends
  between them, and `SampleBakedWithRotation` uses them, so rotation is smooth between baked points.
- **`UpVectorEnabled`** (default off, unlike Godot, so rivers bake as before) bakes rotation-minimising up vectors by
  double reflection. `SampleBakedUpVector(offset, applyTilt)` never flips on a vertical climb or a loop. On a closed
  curve the leftover twist is spread along the length so the frame matches at the seam.
- **`WrapOffset(offset)`**: modulo the length when closed, else clamped.
- **`SetSmoothPoints(points, closed, tension = 0.5)`**: a smooth Bézier through the points (Catmull-Rom converted to
  handles). This is the easy way to author lanes and rails in code.
- Determinism: open curves bake bit-identically on every CPU (`Curve3DTests.FixedCurveBakesToACommittedHash`). Closed
  up vectors use `MathF.Sin`/`Cos` for the seam correction and are not covered by that guarantee.

## `PathFollow3D`

| Property | Default | Meaning |
|---|---|---|
| `Progress` | 0 | Metres along the curve; wraps when `Loop` (or the curve is closed), else clamps |
| `ProgressRatio` | — | `Progress / length` (not saved) |
| `HOffset`, `VOffset` | 0 | Offsets along the follower's local X and Y |
| `RotationMode` | `XYZ` | `None` (position only), `Y` (yaw), `XY` (yaw + pitch), `XYZ` (+ tilt), `Oriented` (baked up vectors + tilt; needs `UpVectorEnabled`, else as `XYZ`) |
| `CubicInterp` | true | Catmull-Rom between baked points |
| `Loop` | true | Wrap past the ends |
| `TiltEnabled` | true | Roll with the curve's tilt |
| `Speed` | 0 | Extension: metres per second, advanced by itself |
| `PingPong` | false | Extension: with `Speed` and no `Loop`, turn back at the ends |
| `ProcessCallback` | `Idle` | Extension: advance in `OnProcess` or `OnPhysicsProcess` (for bodies under the follower) |

Signals: `Looped` (speed movement wrapped) and `EndReached` (stopped at an end, or turned there with `PingPong`).

- Forward is `-Z`, the engine's forward, so Godot's `use_model_front` does not exist. The follower's scale is kept.
- The follower writes its transform when a property changes, when it enters the tree and when its path's curve
  changes. It processes only while `Speed ≠ 0`, and only in the chosen callback.
- No allocation per frame (`PathFollow3DTests.FollowingAllocatesNothing`).

## Editor

Every `Path3D` in an edited scene draws its baked curve in the viewport: depth-tested in `DebugColor`, or on top in the
selection colour when selected (`ViewportController.DrawPath`). Points are edited through the inspector or in code
until the curve tool (G13.6) lands.

## Tests

`Tests/MainframeEngine.Tests/Paths/`: `PathCurveTests` (closed seam, `WrapOffset`, smooth points, tangent continuity,
up vectors on a loop and at a closed seam, serialization, 0 B) and `PathFollow3DTests` (placement, loop and clamp,
offsets, every rotation mode, tilt, `Speed` with loop, stop and ping-pong, the physics callback, curve changes,
serialization, 0 B with 16 followers).
