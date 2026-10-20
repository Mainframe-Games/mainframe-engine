# ADR 0203 — BitMask (Godot's BitMap) and Geometry2D on Clipper2

- **Date:** 2026-10-20
- **Status:** accepted
- **Milestone:** — (Godot compatibility for the Buy My Snake Oil port, mainframe-engine#112)
- **Docs:** docs/design/math.md#2d-helpers

## Context

Buy My Snake Oil's soft bodies (the softbody2d addon, ported to C#) build each body from its texture's alpha: they trace
the outline (`BitMap.opaque_to_polygons`), grow it (`grow_mask`), cut it into Voronoi cells and clip each cell against the
outline (`Geometry2D.intersect_polygons`, `offset_polygon`). The engine had `Texture2D.DecodePixels()` and an ear-clipping
triangulator (`CanvasPrimitives.Triangulate`, ADR 0202), but neither the bit grid nor polygon booleans. The game carried
its own port of `BitMap` and pulled in Clipper2 itself.

## Decision

- **`BitMask`** is a line-by-line port of Godot 4.7.2's `scene/resources/bit_map.cpp` (MIT): `Create`,
  `CreateFromImageAlpha` (a `Texture2D`, a `Bitmap` or RGBA8 bytes), `Get/SetBit`, `SetBitRect`, `GetTrueBitCount`,
  `Resize`, `GrowMask`/`ShrinkMask`, `OpaqueToPolygons` (flood fill, marching squares with Godot's crossing splits,
  Ramer–Douglas–Peucker), `ConvertToImage` and `Blit`. It is named `BitMask`, not `BitMap`, because the engine's RGBA8
  `Bitmap` would differ from it only by case (ADR 0010). It is a `Resource` built at run time; its bits are not saved.
- **`Geometry2D`** is a static class with Godot's polygon API: `Merge/Clip/Intersect/ExcludePolygons`,
  `ClipPolylineWithPolygon`, `IntersectPolylineWithPolygon`, `OffsetPolygon`, `OffsetPolyline`, `TriangulatePolygon`
  (the existing ear clipper) and `IsPolygonClockwise`.
- **The booleans and offsets wrap Clipper2 1.5.4** (Angus Johnson, Boost Software License 1.0; NuGet `Clipper2`, managed,
  netstandard2.0), the library Godot's `Geometry2D` wraps, with Godot's settings: 5 decimal digits of precision, even-odd
  fill, collinear points removed, a miter limit of 2 and an arc tolerance of 0.25. Approved by Brogan on 2026-10-20 over
  writing booleans in-house (much more code, and edge cases would drift from Godot).

## Consequences

- `GodotGeometryParityTests` compares both against Godot 4.7.2 on the same inputs (`Imaging/Fixtures`: the GDScript that
  produced the JSON). The traced outlines match exactly; booleans and offsets match within 0.001.
- Clipper2 becomes a dependency of the `MainframeEngine` package (games get it transitively). Keep Clipper2 calls in
  `Math/Geometry2D.cs`.
- The game can delete its `AlphaBitmap` and `PolygonGeometry` ports and its own Clipper2 reference.
