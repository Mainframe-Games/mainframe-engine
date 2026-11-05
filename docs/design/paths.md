# Paths (`Path3D`, `PathFollow3D`, `Path2D`, `PathFollow2D`)

**Status:** the 3D nodes shipped (G13 first slice, [ADR 0215](../../memory/decisions/0215-path3d-and-pathfollow3d-first-slice.md));
2D paths and the 2D curve tool shipped (G13.2, G13.4, G13.7, [ADR 0242](../../memory/decisions/0242-path2d-and-the-curve-tool.md)).
The rest of G13 (moving platforms, the 3D curve tool, the closest-point grid) is in the [proposal](future/paths.md).

## Overview

| Type | File | Role |
|---|---|---|
| `Curve3D` | `MainframeEngine/Src/Resources/Curve3D.cs` | Bézier resource baked to equal arc length; `Closed`, tangents, up vectors |
| `Path3D` | `MainframeEngine/Src/Scene/Nodes3D/Paths/Path3D.cs` | Holds a `Curve3D` (local space); re-places its followers when it changes |
| `PathFollow3D` | `MainframeEngine/Src/Scene/Nodes3D/Paths/PathFollow3D.cs` | Places itself and its children on the parent path at `Progress` metres |
| `Curve2D` | `MainframeEngine/Src/Resources/Curve2D.cs` | 2D Bézier resource in pixels (Godot's API), same bake |
| `Path2D`, `PathFollow2D` | `MainframeEngine/Src/Scene/Nodes2D/Paths/` | The same in 2D |
| `CurveBaker` (internal) | `MainframeEngine/Src/Resources/CurveBaker.cs` | The bake both curves share: adaptive subdivision, equal-arc resampling, tangents |
| `PathMotion` (internal) | `MainframeEngine/Src/Scene/Nodes3D/Paths/PathMotion.cs` | The followers' shared wrap, clamp, loop and ping-pong rules |
| `CurveEditor`, `CurveEditing`, `ICurveTarget` | `MainframeEngine.Editor/Src/Viewport/Curves/` | The editor's curve tool |

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
- **`Reverse()`**: the points in the opposite order with `in`/`out` swapped and tilt negated; the shape is unchanged.
- Determinism: open curves bake bit-identically on every CPU (`Curve3DTests.FixedCurveBakesToACommittedHash`). Closed
  up vectors use `MathF.Sin`/`Cos` for the seam correction and are not covered by that guarantee.
- The bake is `CurveBaker`'s, shared with `Curve2D`: each curve writes its segments' absolute control points into the
  baker's scratch and keeps its own extra per-point data (tilt, width, depth, up vectors) from the baked curve
  parameters. Moving it out of `Curve3D` left the committed hash unchanged.

## `Curve2D`

Godot's `Curve2D` in pixels: `AddPoint`, `Get/SetPointPosition/In/Out`, `RemovePoint`, `ClearPoints`, `Sample`,
`SampleBaked`, `SampleBakedWithRotation` (a `Transform2D`: `+X` along the tangent, `+Y` 90° clockwise on screen),
`GetBakedLength`, `GetBakedPoints`, `GetBakedDistances`, `GetClosestOffset`, `GetClosestPoint`, `BakeInterval`
(default 5 px), plus `Closed`, `SegmentCount`, `SampleBakedTangent`, `WrapOffset`, `SetSmoothPoints` and `Reverse` as in
`Curve3D`. Storage is one exported `float[]`, 6 floats per point (position, in, out); no tilt. It bakes through
`CurveBaker` with z = 0, so it is as deterministic as `Curve3D` (`Curve2DTests.FixedCurveBakesToACommittedHash`).

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

## `Path2D` and `PathFollow2D`

`Path2D : Node2D` holds a `Curve2D` (local space) exactly as `Path3D` holds a `Curve3D`: `Curve`, `DebugColor`,
`CurveChanged`, and it re-places its `PathFollow2D` children when the curve changes. `PathFollow2D` has Godot's
`Progress` (pixels), `ProgressRatio`, `HOffset`, `VOffset`, `Rotates` (default on: `+X` along the curve),
`CubicInterp` and `Loop`, and the same extensions as `PathFollow3D`: `Speed` (pixels per second), `PingPong`,
`ProcessCallback`, `Looped` and `EndReached`. As in Godot, with `Rotates` the offsets are along and across the curve;
without it they are along the path's own X and Y. A follower that is not a child of a `Path2D` warns once and stays
put. No allocation per frame (`PathFollow2DTests.FollowingAllocatesNothing`).

A patrol is a follower used as a "carrot": put its `Progress` a little past the walker's closest offset
(`curve.GetClosestOffset(path.ToLocal(walker.GlobalPosition)) + lookAhead`) and steer the walker toward it. The walker
joins the route from wherever it is and resumes after a detour. The Demo's `PathGuard2D` is this recipe.

## Editor

Every `Path3D` in an edited scene draws its baked curve in the viewport: depth-tested in `DebugColor`, or on top in the
selection colour when selected (`ViewportController.DrawPath`), and every `Path2D` in a 2D tab the same way
(`DrawPath2D`). A path made with Add Node starts with an inline curve (`NewNodeDefaults`: a `Curve2D`, or a `Curve3D`
with `UpVectorEnabled`).

**The curve tool (2D).** While a `Path2D` is the selection in a 2D tab, the curve bar shows over the view (the tile
map's bar slot) and `CurveEditor` takes the view's clicks:

| Bar button | Does |
|---|---|
| Select Points (`pointer`) | Drag a point, or a tangent handle (mirrored; Shift breaks the mirror). Cmd/Ctrl+drag a point pulls new, mirrored handles; Cmd/Ctrl+click on empty space adds a point; a right click on a point removes it; a click elsewhere selects nodes as usual |
| Add Point (`circle-plus`) | A click adds a point at the end (and can be dragged straight away) |
| Insert Point (`plus`) | A click on the curve splits that segment there by de Casteljau, so the shape does not change; a ghost marks the spot |
| Delete Point (`trash`) | A click on a point removes it; Delete or Backspace removes the selected point |
| Close / Open Curve (`repeat`) | Toggles `Closed` |
| Reverse (`arrows-exchange`) | `Curve2D.Reverse` |
| Smooth (`wave-sine`) | Smooth, mirrored handles (Catmull-Rom) on the selected point, or on every point when none is selected |

Moves snap to the toolbar's translate step when Snap is on. Every gesture is one undo step (`SetCurvePointsAction`:
the whole point array before and after, applied as copies so later in-place edits never reach the history); Esc or a
right click during a drag puts the curve back. The tool works on an `ICurveTarget` (points and handles in local 3D
coordinates, 2D with z = 0, plus the node's transform), so the 3D curve tool (G13.6) adds a `Path3D` target rather
than a second tool.

![A new Path2D in the Demo's Paths 2D scene: a closed, smoothed curve with points and tangent handles under the curve bar](../images/editor-path2d.png)

## Demo

`paths_2d` (Paths 2D, [Demo](demo.md)): cars lapping a closed track at their own speeds, a lift that ping-pongs along an
open arc and a guard (`PathGuard2D`) walking a patrol route by the carrot recipe. `PathTrack2D` draws a path's baked
curve in the game (paths themselves draw only in the editor).

## Tests

`Tests/MainframeEngine.Tests/Paths/`: `PathCurveTests` (closed seam, `WrapOffset`, smooth points, tangent continuity,
up vectors on a loop and at a closed seam, `Reverse`, serialization, 0 B), `PathFollow3DTests` (placement, loop and
clamp, offsets, every rotation mode, tilt, `Speed` with loop, stop and ping-pong, the physics callback, curve changes,
serialization, 0 B with 16 followers), `Curve2DTests` (Godot's API, closed curves, tangents and rotation, smooth points,
`Reverse`, closest points, serialization, a committed bake hash, 0 B) and `PathFollow2DTests` (the same follower cases
in 2D). The editor's `Curves/CurveEditorTests` cover each gesture and command with its undo and redo, insert keeping
the shape, snapping, cancel and the history never sharing the curve's storage. `Demo.Tests/Paths2DSceneTests` checks
the laps, the lift's turns and the guard's lap; `Tests/QA/editor-walkthrough.qa` draws, closes, smooths, drags and
undoes a path (captures `26e`–`26g`).

## Changes from the proposal

- **Curve tool (G13.7).** Built 2D first and wired like the tile map editor (`CurveEditor` owned by the workspace, the
  paint bar's slot), not as a G8a.2 `IEditorViewportTool`, which is still unbuilt; it works on the proposal's
  `ICurveTarget`, so G13.6 adds a `Path3D` target. Reverse and a Smooth button sit on the bar rather than in the
  inspector; a right click on a point removes it (Godot).
- **`PathFollow2D` offsets.** Without `Rotates`, `HOffset`/`VOffset` are along the path's X and Y, as in Godot; with it,
  along and across the curve.
- **Not yet in `Curve2D` or `Curve3D`:** the closest-point grid and the hinted closest query (linear scans, as before).
  `Reverse` is in both.
- **Demo.** A `paths_2d` scene ships now; the proposal's `paths_3d` (lifts and a turntable a character rides) waits for
  G13.5's moving platforms.
