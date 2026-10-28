# ADR 0225 — Tile map layers: canvas sub-items, base64 chunk lines and per-chunk tile bodies

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/tile-maps.md (from the proposal docs/design/future/2d-content.md, G2b)

## Context

G2b adds Godot 4.3's `TileSet` and `TileMapLayer`. The proposal left open questions: the type of 2D collision results
(widen `Collider` to `Node2D` or add a second member), custom data storage, one chunk size for drawing and physics,
`UvClipping`, and how chunks reach the canvas batcher and Box2D without becoming nodes.

## Decision

- **`TileMapLayer` only**, no multi-layer `TileMap` (Godot 4.3's model). Cells live in `TileChunk`s of
  `RenderingQuadrantSize` (default 16) cells, keyed by chunk coordinates; a chunk whose last cell is erased is removed.
- **Canvas sub-items** (`CanvasSubItem`, internal; `CanvasItem.SubItems`): draw lists a canvas item owns besides its own.
  The culler attaches them after the owner's list and before its children, each on its own bounds and z offset, with the
  owner's modulate, material and texture state; under y-sort they are entries sorted with the children. A chunk is one
  sub-item per z, or — y-sorted — one per (row, tile y-sort origin, z), placed at the row's sort position. Only dirty
  chunks rebuild (in the layer's redraw). `CulledCanvasItem.DrawList` is what the frame builder reads.
- **Serialization:** `TileMapData` is `[Export(Storage = true)] string[]`, one base64 line per chunk (sorted y, x) of a
  `u16` format (1) and Godot 4.3's 12-byte records; format 0 (Godot's) decodes too. `ExportAttribute.Storage` hides a
  member from the inspector. `SceneJsonLayout` puts arrays with a string over 64 characters one element per line, so a
  map of small chunks may still share a line until one grows.
- **Collision results widen to `Node2D`** (the proposal's recommendation): `RayHit2D`, `ShapeCastHit2D`,
  `KinematicCollision2D.Collider` and the `IntersectShape`/`IntersectPoint` lists. Breaking but Godot-like
  (`get_collider()` is an `Object`); `is Player` checks keep compiling. No shape tag yet: `GetCoordsForCollision` uses
  Godot's half-pixel idiom.
- **Tile bodies** (`TileBodies2D`, `Src/Physics/2D`): one static Box2D body per chunk per physics layer with shapes,
  created only when the tile set has physics layers (a visual layer never makes a physics space). `BodyRecord2D` gains
  `Owner` (`Node2D`); tile records have no `CollisionObject2D`, are never in the space's object lists, and report the
  layer. Full-tile squares merge into greedy rectangles (row runs extended down); other polygons stay whole when convex
  with ≤ 8 points, else ear-clip + Hertel–Mehlhorn into ≤ 8-point convex pieces (slivers dropped). Rebuilds keep the
  body and replace its shapes (areas see no exit/enter flicker), in the space's flush (before a step, a query, or the
  debug draw), and wake dynamic bodies in the chunk's bounds. Areas and contact monitors report a layer once however
  many chunk bodies they touch. Moving the layer teleports its bodies; a scale change rebuilds them.
- **Custom data** stays invariant text (the recommendation): typed getters parse once per layer and kind, cached.
- **One chunk size** (`RenderingQuadrantSize`) for drawing and physics.
- **`UvClipping`** is stored for Godot compatibility; texture padding (on by default) is what prevents bleeding.
- **`CanvasItem.EditorModulate`**: a never-saved modulate the editor uses to dim layers (Highlight).

## Consequences

- A 256 × 256 layer and 400 y-sorted objects scroll with 0 bytes allocated (unit and render gates). The y-sort now sorts
  a span with a cached `Comparison` (`List.Sort(IComparer)` allocated a delegate per y-sorted subtree each frame).
  Three full-screen layers of a 512 × 512 map build a 1920 × 1080 frame in ≈ 1.8 ms (benchmark, loaded machine).
- A moving `Camera2D` in the full renderer allocated ≈ 2 KB per frame in the tile render test (the 3D path's
  orthographic camera); the render gate scrolls the layer instead. Follow-up outside tile maps.
- Navigation (G3a/G3.6) reads layers through `GetUsedCells`, `GetCellTileData`, collision polygons on a physics layer or
  a custom data bool; `TileSetNavigationLayer` is reserved, not added.
