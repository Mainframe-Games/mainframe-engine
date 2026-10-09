# ADR 0155 — Curve3D, River3D geometry and the Forest example project

- **Date:** 2026-10-08
- **Status:** accepted (implemented on `forest/e`, forest slice wave 1)
- **Milestone:** Gameplay toolkit G8 (G8c.2–G8c.3 subset, G8d.9–G8d.10 subset)
- **Spec:** docs/design/future/water.md, docs/design/future/forest-showcase.md; plan
  docs/superpowers/plans/2026-10-08-forest-vertical-slice.md
- **Docs:** docs/design/water.md, docs/design/forest.md

## Context

The forest slice needs a stream to walk through and a project to walk in before the terrain, trees and water material
exist. ADR 0149 made water an engine feature and the Forest an example project; this lane builds their first pieces in
the shape the proposals give them, on today's renderer, while other lanes work on PBR, vertex streams, terrain and trees.

## Decision

- **`Curve3D`** is an engine resource with Godot's API subset plus per-point `Width` (full width, default 1 m) and
  `Depth` (default 0 m), stored as one exported `float[]` of 12 floats per point. Baking is lazy: adaptive subdivision
  of each Bézier segment, then equal-arc-length resampling (step ≤ `BakeInterval`). It uses only correctly rounded
  scalar operations in a fixed order, never the SIMD `Vector3.Dot/Length/Lerp`, so bakes are bit-identical on x64 and
  arm64; a test holds a hash of a fixed bake. `SampleBakedWithRotation` looks along **−Z** (the engine's forward)
  rather than Godot's +Z.
- **`River3D`** (`Node3D`, `IWaterBody3D`) generates its ribbon with an internal `RiverBuilder`: downhill-clamped
  heights, sections every `SectionLength`, flow speed `MinSpeed + SpeedPerSqrtSlope × √slope` (slope per metre of arc),
  UV.u across (left bank 0, facing downstream), UV.v = arc length / `UvLength`, normals up. The streams are a
  `RiverMeshData` record (positions, normals, UVs, `Custom0`, indices); the `ArrayMesh` gets everything except `Custom0`
  until lane A2's `MeshSurface.Custom0` merges. The ribbon is an unowned `MeshInstance3D` child created in `OnReady`, so
  scenes never save it, drawn with a blended blue `StandardMaterial3D` until `WaterMaterial3D`.
- **Water queries** are `World3D.Water` (`WaterQueries`): bodies register while in the tree; `TrySample` (highest surface
  wins), `WaterDepthAt`, `SurfaceHeightAt` (NaN when dry), `FlowAt`, `ImmersionAt` (0 when dry), `IsUnderwater`,
  `WadeSpeedScale`. A river answers through a 4 m grid of section segments; a point is inside when it lies between two
  section lines and within the interpolated half width, so bends have no gaps. 0 B per query.
- **`Examples/Forest`** mirrors `Examples/Demo`: its own `Forest.slnx` (not in `MainframeEngine.slnx`), a node library,
  `Forest.Desktop` with `--write-scenes`, `Forest.Tests`, the Demo's `.gitattributes` plus `*.hdr`, `*.exr`, `*.cube`.
  The scene is generated in code for now with a byte-for-byte contract test, like the Demo; the content wave switches to
  editor-authored files as the proposal says. `just forest` runs in Release.
- **`FirstPersonController`** stays Forest code (forest-showcase decision 1). Two choices differ from the proposal's
  sketch:
  - **Yaw lives on the head, not the body.** Bodies are physics-interpolated on their node transform (ADR 0024); a yaw
    written to the body between steps is replaced by the render pose, so mouse look would lag or snap.
  - **The controller keeps its own horizontal velocity** and moves along the floor plane at that speed (Godot's
    `floor_constant_speed`). Reading it back from `Velocity` loses the component `MoveAndSlide` removes into a slope on
    every step: a 30° ramp settled at 0.7 m/s instead of 2.5 m/s.
- **Footstep surfaces** are an interface, `IFootstepSurface` (bodies such as `SurfaceBody3D` name their surface), with a
  `SurfaceResolver` delegate for the terrain (`Terrain3D.SurfaceAt`) and a default; the engine has no node metadata to
  carry a `surface` string.
- **`--autowalk`** is a `ForestDev` autoload driven by game arguments after `++`: a fixed input loop, then a 600-frame
  main-thread allocation window over the whole frame, exiting 1 if anything was allocated.

## Consequences

- Wave 2 adds carving, falls, pond joins, the terrain as a water body and `WaterMaterial3D` on top of `RiverBuilder` and
  `WaterQueries`; the coordinator wires `RiverMeshData.Custom0` into the mesh when A2 merges.
- The Forest builds and runs against the checkout with no LFS content; its tests need no GPU. It is not in CI yet.
- Rivers assume translation and yaw only (no tilt or scale) for queries.
- The editor may list the unowned `Ribbon` child; a hidden-internal-child concept is left to the River3D editor work.
