# Paths (`Path3D`, `PathFollow3D`, `Path2D`, `PathFollow2D`, moving platforms)

**Status:** G13 is complete. The 3D nodes shipped first
([ADR 0215](../../memory/decisions/0215-path3d-and-pathfollow3d-first-slice.md)), then 2D paths and the 2D curve tool
([ADR 0242](../../memory/decisions/0242-path2d-and-the-curve-tool.md)), then moving platforms, the 3D curve tool, the
closest-point grid and the Forest's rails ([ADR 0243](../../memory/decisions/0243-moving-platforms-and-the-3d-curve-tool.md)).
This doc replaces the proposal; "Changes from the proposal" at the end lists where the build differs.

Every moving-along-a-line feature (camera rails, patrols, lifts, conveyors, traffic) uses these nodes instead of
hand-written splines. Out of scope: mesh generation along a path (`River3D` is the one ribbon generator), a 1D value
`Curve` (G6.3), spline types other than cubic Bézier (Catmull-Rom input becomes Bézier through `SetSmoothPoints`),
navigation along paths (`AStar3D` covers waypoint graphs) and network prediction of followers beyond replicating
`Progress`.

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
| `AnimatableBody3D`, `AnimatableBody2D` | `MainframeEngine/Src/Physics/3D/CollisionObject3D.cs`, `…/2D/CollisionObject2D.cs` | Kinematic bodies moved by their node; carry characters |
| `CurveTerrainFit` | `MainframeEngine/Src/Scene/Nodes3D/Paths/CurveTerrainFit.cs` | Puts a curve's points on a terrain (Fit to Ground, `River3D.FitToTerrain`) |
| `CurveEditor`, `CurveEditing`, `ICurveTarget` (`Curve2DTarget`, `Curve3DTarget`) | `MainframeEngine.Editor/Src/Viewport/Curves/` | The editor's curve tool, 2D and 3D |

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

### Closest-point queries

`GetClosestOffset` and `GetClosestPoint` project the point on the baked segments. On curves of 32 segments or more, the
first query after a bake buckets the segments into a uniform grid (cell = max(4 × step, length / 256), at most 64 k
cells, `int[]` arrays reused across bakes), and each query walks rings of cells outward from the point until no cell
left can hold a closer segment. The answer is the full scan's, ties included (the lower offset wins):
`ClosestPointTests` checks 8 000 random queries against a scan. `GetClosestOffset(point, hintOffset, window)` searches
only the segments within `window` of `hintOffset` (wrapping on a closed curve), for a walker that moves a little each
frame. On a 10 km rail baked every 0.2 m (`PathBenchmarks`), a grid query takes about 0.7 µs and a hinted one about
0.5 µs, where the scan took 72 µs. Queries allocate nothing.

## `Curve2D`

Godot's `Curve2D` in pixels: `AddPoint`, `Get/SetPointPosition/In/Out`, `RemovePoint`, `ClearPoints`, `Sample`,
`SampleBaked`, `SampleBakedWithRotation` (a `Transform2D`: `+X` along the tangent, `+Y` 90° clockwise on screen),
`GetBakedLength`, `GetBakedPoints`, `GetBakedDistances`, `GetClosestOffset`, `GetClosestPoint`, `BakeInterval`
(default 5 px), plus `Closed`, `SegmentCount`, `SampleBakedTangent`, `WrapOffset`, `SetSmoothPoints` and `Reverse` as in
`Curve3D`, and the same grid and hinted closest queries. Storage is one exported `float[]`, 6 floats per point
(position, in, out); no tilt. It bakes through `CurveBaker` with z = 0, so it is as deterministic as `Curve3D`
(`Curve2DTests.FixedCurveBakesToACommittedHash`).

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
(`curve.GetClosestOffset(path.ToLocal(walker.GlobalPosition)) + lookAhead`, or the hinted query near last frame's
offset) and steer the walker toward it. The walker joins the route from wherever it is and resumes after a detour. The
Demo's `PathGuard2D` and `PathGuard3D` are this recipe; with G3's agents the carrot becomes
`NavigationAgent2D.TargetPosition`.

## Uses

- **Camera rails:** `Path3D` (closed, `UpVectorEnabled`) → `PathFollow3D` (`Oriented`, `Speed`) → `Camera3D`, or a
  camera that looks ahead along the curve. The Forest's fly-over and benchmark fly `Curve3D` rails (below).
- **Tweens and keyframes:** `Progress` and `ProgressRatio` are plain floats. `Tween.TweenMethod` with a cached
  `Action<float>` moves a follower without boxing (`TweenProperty` boxes each step until G1.7); G1a's value tracks bind
  `ProgressRatio` like any float.
- **Moving platforms:** `Path*` → `PathFollow*` (`Speed`, `PingPong`, `ProcessCallback = Physics`) → `AnimatableBody*`
  → mesh and collision shape.
- **Networking:** followers are a pure function of the curve and `Progress`; replicate `Progress` and clients reproduce
  the pose exactly (curves bake identically on every CPU). `Speed`-driven followers in the physics callback agree after
  the same number of steps.

## Moving platforms (G13.5)

`AnimatableBody3D` and `AnimatableBody2D` (Godot's names, icon `elevator`) are static bodies that their node moves: as
for a kinematic `RigidBody*`, each move reaches the physics body in one step as a velocity, so it pushes rigid bodies.
Move them from `OnPhysicsProcess`: a follower in `PathProcessCallback.Physics`, or code. `GetStepVelocity()` and
`GetStepAngularVelocity()` report the last step's motion. The body records now keep that motion (`StepVelocity`,
`MovedByTarget`), so a kinematic `RigidBody*` moved by its node reports its real `LinearVelocity` too (setting one
takes over again).

`CharacterBody3D` and `CharacterBody2D` ride what they stand on:

- **Carrying.** After its own slide, `MoveAndSlide` moves the body by where its floor's rigid motion takes it in the
  coming step (`BodyRecord*.PlatformDisplacement`): exact for a kinematic body whose node already moved this step
  (rotation included, so a turntable carries a rider in a circle with no outward drift), else from the body's last
  step (dynamic bodies: their current velocities). Moving after the slide keeps a descending lift from fighting the
  recovery pass. Put platforms before riders in the tree so they move first in each step.
- **`PlatformFloorLayers`** (all by default): only floors on these layers carry. Walls never do.
- **`PlatformOnLeave`** (`AddVelocity`, `AddUpwardVelocity`, `DoNothing`): what `Velocity` keeps of the platform's
  velocity when the body leaves it, by jumping or walking off.
- **`GetPlatformVelocity()`**: the floor's velocity in the last `MoveAndSlide`.

`MovingPlatformTests` (3D and 2D): a rider stays within 1 cm of its spot on a ping-pong lift for 10 s and on a
sideways ferry, a turntable rider keeps its radius within 1 cm over 10 s, each `PlatformOnLeave` mode on a jump,
`PlatformFloorLayers`, a pushed box, and a kinematic `RigidBody*`'s reported velocity.

## Editor

Every `Path3D` in an edited scene draws its baked curve in the viewport: depth-tested in `DebugColor`, or on top in the
selection colour when selected (`ViewportController.DrawPath`), and every `Path2D` in a 2D tab the same way
(`DrawPath2D`). A path made with Add Node starts with an inline curve (`NewNodeDefaults`: a `Curve2D`, or a `Curve3D`
with `UpVectorEnabled`).

A `River3D` is edited by the same tool in 3D, with bank and depth handles and its own ground snap
([Water → Editor](water.md#editor)).

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

**The curve tool (3D, G13.6).** With a `Path3D` selected in a 3D view the same bar and gestures edit its `Curve3D`
through a `Curve3DTarget`. Points and handles are hit on screen; a drag moves in the plane through the grabbed point
facing the camera, and Alt+drag on a point moves it only up and down (Alt elsewhere still orbits). New points go level
with the last point (or the path), or onto the terrain. Moves snap to the translate step on all three axes. The bar
adds:

| Bar button | Does |
|---|---|
| Tilt Handles (`rotate`) | The selected point's tilt ring: a circle around the curve's tangent through its untilted up vector, with a handle at the tilt. Dragging it rolls the curve there ("Tilt 30.0°" by the mouse; snaps to the rotate step) |
| Snap to Ground (`mountain`) | New and dragged points go onto the scene's first `Terrain3D` under the mouse (`Terrain3D.Raycast`), Height Above it |
| Lower / Raise (`minus`, `plus`) and the value | Height Above the terrain, in 0.5 m steps |
| Fit to Ground (`arrow-bar-to-down`) | Every point onto the terrain, Height Above it, in one undo step (`CurveTerrainFit.Fit`, which `River3D.FitToTerrain` uses too) |

Without a terrain the two terrain buttons say so in their tooltips. Each gesture, including a tilt drag and Fit to
Ground, is one `SetCurvePointsAction`.

![The Demo's camera rail selected in Paths 3D: its points, mirrored handles and point 3's tilt ring, with the curve bar's 3D tools](../images/editor-path3d.png)

## Demo and the Forest

- `paths_2d` (Paths 2D, [Demo](demo.md)): cars lapping a closed track at their own speeds, a lift that ping-pongs along
  an open arc and a guard (`PathGuard2D`) walking a patrol route by the carrot recipe. `PathTrack2D` draws a path's
  baked curve in the game (paths themselves draw only in the editor).
- `paths_3d` (Paths 3D, in the Physics category): a capsule (`PathRider3D`: WASD walks, Space or the panel's Jump jumps)
  on a lift that ping-pongs up to a landing, a ferry between two pads, a turntable (`Turntable3D`, an
  `AnimatableBody3D` turned by code), a guard (`PathGuard3D`) on a patrol route with the hinted query, and a closed
  camera rail the panel can ride. `PathMarkers3D` shows each path in the game.
- The Forest ([Forest](forest.md)): the fly-over (`ForestCameras`) is a closed `Curve3D` through the benchmark's
  points sampled by arc length, still lifted 1.5 m clear of the ground and water per baked point; the benchmark
  (`ForestBenchmark`) flies an open `Curve3D` through the same points at a steady speed. Their own Catmull-Rom loops
  and arc-length tables are gone.

## Tests

`Tests/MainframeEngine.Tests/Paths/`: `PathCurveTests` (closed seam, `WrapOffset`, smooth points, tangent continuity,
up vectors on a loop and at a closed seam, `Reverse`, serialization, 0 B), `PathFollow3DTests` (placement, loop and
clamp, offsets, every rotation mode, tilt, `Speed` with loop, stop and ping-pong, the physics callback, curve changes,
serialization, 0 B with 16 followers), `Curve2DTests` (Godot's API, closed curves, tangents and rotation, smooth points,
`Reverse`, closest points, serialization, a committed bake hash, 0 B), `PathFollow2DTests` (the same follower cases
in 2D), `ClosestPointTests` (grid against scan, hinted, 0 B) and `MovingPlatformTests`. The editor's
`Curves/CurveEditorTests` and `CurveEditor3DTests` cover each gesture and command with its undo and redo, insert
keeping the shape, snapping, cancel, the history never sharing the curve's storage, camera-plane and vertical drags,
the tilt ring, ground snap and Fit to Ground. `Demo.Tests/Paths2DSceneTests` and `Paths3DSceneTests` check laps, lifts
and the guards' laps; `Tests/QA/editor-walkthrough.qa` draws, closes, smooths, drags and undoes a 2D path (captures
`26e`–`26g`) and shows the 3D bar and a tilt ring (`26h`). `PathBenchmarks` times the queries and 1 000 followers.

## Changes from the proposal

- **Curve tool.** Built 2D first and wired like the tile map editor (`CurveEditor` owned by the workspace, the paint
  bar's slot), not as a G8a.2 `IEditorViewportTool`, which is still unbuilt. One tool serves 2D and 3D through
  `ICurveTarget`; `River3DTool` (G8c.4) is not built, but the river's fit to the terrain is the tool's
  (`CurveTerrainFit`). Reverse, Smooth, Height Above and Fit to Ground sit on the bar rather than in the inspector; a
  right click on a point removes it (Godot). Ground snap targets only `Terrain3D` (the proposal's open question 4).
- **`PathFollow2D` offsets.** Without `Rotates`, `HOffset`/`VOffset` are along the path's X and Y, as in Godot; with it,
  along and across the curve.
- **Platforms carry by displacement, not velocity.** The rider moves by the platform's exact rigid motion for the
  step after its slide (the proposal moved it by `v + ω × r` before the slide), so turntables do not fling riders
  outward and lifts do not fight recovery.
- **Closed up-vector seams** spread the twist evenly; no seam-tilt option (open question 6). Followers in the physics
  callback are not interpolated themselves; put visuals under the body (open question 3). `Closed` rivers stay
  rejected (open question 5). `UpVectorEnabled` stays off on the resource and on for curves the editor creates (open
  question 1); the follower extensions stay on the follower (open question 2).
- **Benchmarks** were not added to `baseline.json`: it is recorded only on the quiet reference machine.
- **Demo.** Both `paths_2d` and `paths_3d` ship; the 3D scene's rider walks with the existing `move_*` actions and
  jumps with Space or the panel, since the Demo has no jump action.
