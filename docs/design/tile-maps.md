# Tile maps (`TileSet`, `TileMapLayer`)

**Status:** shipped (Gameplay toolkit G2b; [ADR 0225](../../memory/decisions/0225-tile-map-layers-sub-items-and-tile-bodies.md),
editor [ADR 0226](../../memory/decisions/0226-tile-map-editor.md)). Animated sprites (G2a) are in
[Animated sprites](animated-sprites.md).

Godot 4.3's tile maps for the [2D canvas](canvas.md): a `TileSet` resource cuts atlas textures into tiles with collision
polygons and custom data, and each `TileMapLayer` node paints cells with them. Layers store, draw and collide in chunks,
draw through the canvas batcher (about one batch per atlas), y-sort row by row with characters, get one static Box2D
body per chunk, and save one base64 line per chunk.

![The paint bar over the Demo's Tiles 2D scene and the TileMap palette in the bottom dock](../images/editor-tilemap.png)

## Types

| Type | File | Role |
|---|---|---|
| `TileSet` | `MainframeEngine/Src/Scene/Nodes2D/Tiles/TileSet.cs` | Resource (`.mres`): `TileSize`, `TileShape` (only `Square`), `PhysicsLayers`, `CustomDataLayers`, `Sources`; cached id and name lookups; `Version` |
| `TileSetPhysicsLayer`, `TileSetCustomDataLayer` | same | Collision layer, mask and `PhysicsMaterial`; a custom data layer's `Name` and `Type` |
| `TileSetAtlasSource` | `Tiles/TileSetAtlasSource.cs` | A texture cut by a `SpriteSheetGrid` (`Margins`, `Separation`, `TextureRegionSize`); created `Tiles`; `TileOverrides`; texture padding; `CreateTilesFromOpaqueRegions` |
| `TileData`, `TileCollisionPolygon` | `Tiles/TileData.cs` | Per tile or alternative: `TextureOrigin`, `Modulate`, `ZIndex`, `YSortOrigin`, `Probability`, `Collision`, `CustomData` (typed cached getters) |
| `TileMapLayer` | `Tiles/TileMapLayer.cs`, `.Rendering.cs` | The node: cells, chunks, drawing, bodies, coordinates |
| `TileCell` | `Tiles/TileTypes.cs` | Record struct: source id, atlas x/y, alternative (with the transform flags); `Empty` |
| `TileShape`, `TileCellNeighbor`, `DebugVisibilityMode`, `CustomDataType` | same | Godot's enums |
| `TileChunk`, `TileMapDataCodec` | `Tiles/` | Internal: a chunk's cells, sub-items and bodies; the saved format |
| `CanvasSubItem` | `Src/Scene/Canvas/CanvasSubItem.cs` | Internal: a draw list a canvas item owns besides its own |
| `TileBodies2D`, `TilePolygons` | `Src/Physics/2D/` | A layer's chunk bodies; flip/transpose, full-tile test, convex decomposition (all Box2D calls stay in `Src/Physics`) |

## `TileSet` and atlas sources

- **Tiles are created explicitly** (Godot): a cell may only show a created tile. `Tiles` lists atlas coordinates;
  `TileOverrides` holds a `TileData` only for tiles or alternatives with non-default values, so a 16 × 16 atlas with
  collision on a dozen tiles saves about a dozen `TileData` entries. `GetTileData(coords, alternative)` returns the
  override, else the base tile's, else a shared default (never edit it: `GetOrCreateTileData`).
- **Alternatives and transforms.** As in Godot 4.2+, an alternative id's high bits are flags:
  `TransformFlipH = 4096`, `TransformFlipV = 8192`, `TransformTranspose = 16384` (`AlternativeMask` = 4095). The renderer
  mirrors or transposes the quad, and collision polygons are transformed the same way (transpose, then flips: Godot's
  `get_transformed_vertices`). `CreateAlternativeTile` copies the base tile's data into the next free id (1…4095).
- **Texture padding** (`UseTexturePadding`, on by default): the source draws from a copy of the atlas in which every
  grid cell sits 1 px from its neighbours with its own edge pixels repeated around it (Godot's padding). Built once per
  texture or grid change, on the main thread when first drawn (`TryGetRuntimeRegion`). Linear filtering at a fractional
  zoom then never shows a neighbour (the `tiles-seam` render test). `TileSet.UvClipping` is kept for Godot compatibility.
- **Physics layers** hold a collision layer, mask and material; each polygon names its layer. Polygon points are in tile
  pixels with the origin at the tile's centre (Godot).
- **Custom data layers** (`Bool`, `Int`, `Float`, `String`, `Vector2`, `Color`): values are invariant text aligned to
  the layers (`TileData.CustomData`); `GetCustomInt(layer)` and friends parse a value once per kind and cache it, then
  allocate nothing. `SetCustomData(layer, value)` overloads format invariant text. Godot stores `Variant`s; the engine
  has none and an `object` export would box.
- **Change tracking.** Every setter raises `Changed`, and parents re-raise their children's (`TileData` → source →
  set), so a layer redraws and rebuilds its bodies when anything in its tile set changes. Lists edited in place need
  `NotifyChanged()`.

## `TileMapLayer`

One layer per node (Godot 4.3 deprecated the multi-layer `TileMap`): z index, modulate, material, visibility and y-sort
come from `CanvasItem`, and layers are ordered by the tree.

| Member | Notes |
|---|---|
| `Enabled`, `TileSet`, `YSortOrigin`, `XDrawOrderReversed`, `RenderingQuadrantSize` (1–64, default 16), `CollisionEnabled`, `CollisionVisibilityMode` | Godot's names and defaults; one chunk size for drawing and physics |
| `SetCell(coords, sourceId, atlasCoords, alternative)`, `EraseCell`, `Clear`, `SetCellData(coords, TileCell)`, `GetCell` | A source id of −1 or no atlas coordinates erase; coordinates are 16-bit (−32768…32767) |
| `GetCellSourceId`, `GetCellAtlasCoords`, `GetCellAlternativeTile`, `GetCellTileData`, `IsCellFlippedH/V`, `IsCellTransposed` | −1 / (−1, −1) / null for empty cells |
| `GetUsedCells(list)`, `GetUsedCellsById(list, source, atlas, alternative)`, `GetUsedRect()`, `UsedCellCount`, `ChunkCount` | Fill the caller's list (no allocation once it is sized) |
| `LocalToMap`, `MapToLocal` (cell centre), `GetNeighborCell` (square neighbours), `GetCoordsForCollision(globalPosition, normal, out coords)` | `Node2D.ToLocal`/`ToGlobal` turn global points into the layer's space |
| `UpdateInternals()` | Apply pending chunk rebuilds (drawing and bodies) now |
| `Changed` | Raised by every cell edit, property change and tile set change |
| `TileMapData` | `[Export(Storage = true)]`: the saved cells, never shown in the inspector |

**Chunks.** Cells live in `TileChunk`s keyed by `floor(cell / RenderingQuadrantSize)`: a flat cell array, a used count,
render and physics dirty flags, sub-items and bodies. `SetCell` writes the cell and queues the chunk once; a chunk is
created on its first cell and removed with its last. Painting 1 000 cells in a frame rebuilds each touched chunk once.
Changing `RenderingQuadrantSize` re-chunks every cell. Cells naming a missing source or tile are kept, not drawn, with
one warning (Godot's `fix_invalid_tiles`).

### Drawing: canvas sub-items

A chunk should not be a node (it would be saved, listed and returned by `GetChildren`), so canvas items gain
**sub-items**: draw lists they own besides their own (Godot's rendering quadrants). The culler attaches an item's
sub-items right after its own list and before its node children, each culled on its own bounds with its own z offset
and the owner's modulate, self modulate, material, light mask and texture filter; `CulledCanvasItem.DrawList` is the
list the frame builder reads. A layer's own list stays empty; its redraw rebuilds only dirty chunks.

- A chunk is one sub-item per tile `ZIndex` (z added to the layer's), with one textured quad per cell in Godot's order:
  rows top to bottom, x reversed with `XDrawOrderReversed`; the region centred on the cell minus `TextureOrigin`, flips
  as negative sizes, a transpose swapping axes (Godot's `draw_tile`), times `TileData.Modulate`.
- Chunks are ordered by (y, x). Consecutive quads with one atlas texture and state merge in `CanvasFrame`, so a layer
  using one atlas draws in one batch however many chunks are visible.
- **Y-sort.** With `YSortEnabled`, each chunk row is a sub-item per (row, tile `YSortOrigin`, z), placed at the row's
  sort position — cell centre + `TileData.YSortOrigin` + the layer's `YSortOrigin` — and sorted with the layer's
  children (and nested y-sorted layers'). A character inside the layer walks behind a tree whose canopy tile has a
  y-sort origin of one cell (it sorts at the trunk's base) and in front of it below that.
- **Cost.** `CanvasFrame` transforms vertices on the CPU each frame: three full-screen layers of 16 px tiles at
  1920 × 1080 (a 512 × 512 ground, a sparse layer, a y-sorted object layer) build in ≈ 1.8 ms
  (`TileMapBenchmarks.TileMapLayerFrameBuild`, 0 B). Off-screen chunks cost a bounds test; y-sorted rows are sorted
  each frame whether visible or not. Static GPU buffers per chunk remain an option that needs no API change.

### Physics: one static body per chunk

`TileBodies2D` (created when the layer enters the tree with `CollisionEnabled` and a tile set that has physics layers,
so a purely visual layer never creates a physics space) keeps one Box2D static body per chunk per physics layer with
shapes:

- Polygons are transformed by the cell's flags and placed at the cell's centre. A **full-tile square** is marked in a
  grid and merged greedily into rectangles (a row run, extended down while the rows below have the same run free), which
  cuts shape counts on solid ground and removes the seams characters snag on. Any other polygon stays one Box2D polygon
  when convex with ≤ 8 points, else it is ear-clipped (`CanvasPrimitives.Triangulate`) and merged across diagonals into
  convex pieces of ≤ 8 points (Hertel–Mehlhorn; slivers dropped).
- **Owner.** `BodyRecord2D` has an `Owner` (`Node2D`): the collision object, or the layer for tile bodies, which have no
  `CollisionObject2D` and are never in the space's object lists. **Breaking:** `RayHit2D.Collider`,
  `ShapeCastHit2D.Collider`, `KinematicCollision2D.Collider` and the `IntersectShape`/`IntersectPoint` result lists are
  `Node2D` (Godot's `get_collider()` returns an `Object`); `is Player` checks keep compiling, member access on a
  `CollisionObject2D` needs a cast. Areas' `BodyEntered`/`BodyExited` and contact monitors report a layer once however
  many of its chunk bodies they touch.
- `GetCoordsForCollision` returns the cell a hit touched, pushing the point half a pixel into the surface (Godot's
  idiom; hits carry no shape tag).
- Rebuilds run in the physics space's flush — before a step, before a query, before the debug draw — never inside a
  step. A rebuild keeps the body and replaces its shapes (overlapping areas see no exit/enter) and wakes dynamic bodies
  in the chunk's bounds; erasing a chunk's last shape destroys its body. Moving the layer teleports its bodies; a scale
  change rebuilds them.
- **Debug draw.** Tile shapes draw with physics debug drawing (always in the editor) unless `CollisionVisibilityMode`
  is `ForceHide`; `ForceShow` draws them at run time.

### Serialization: one base64 line per chunk

`TileMapData` is one string per non-empty chunk, sorted by chunk (y, then x): the base64 of a little-endian `u16` format
(1) followed by 12-byte records in cell order — `i16 x, i16 y, u16 sourceId, u16 atlasX, u16 atlasY, u16 alternative`,
Godot 4.3's `tile_map_data` record, so a Godot importer only has to split by chunk (format 0, Godot's, decodes too).
Same cells, same bytes. A full 16 × 16 chunk is about 4 KB of text; a diff shows which areas changed. `SceneJsonLayout`
writes an array one element per line when it holds a string longer than 64 characters (see
[Scene serialization](scene-serialization.md#file-format)). Loading decodes with `Convert.FromBase64String` (load-time
allocation, no reflection).

## Editor

ADR 0226. While a 2D tab's selection is a `TileMapLayer` with a `TileSet`:

- **Paint bar** over the top of the view (icon buttons with tooltips, every one a `tilemap.*` command):

| Icon | Tool | Key | Behaviour |
|---|---|---|---|
| `pointer` | Select | S | Drag a rectangle of cells; Cmd/Ctrl+C, X copy and cut, Cmd/Ctrl+V pastes (the Paint tool with the copied pattern), Delete erases, Esc deselects |
| `pencil` | Paint | D | Paint the palette's tiles (a single tile or a pattern); Shift+drag draws a line, Cmd/Ctrl+Shift+drag a rectangle |
| `line` | Line | L | Drag a line of cells (Bresenham), applied on release |
| `square` | Rect | R | Drag a filled rectangle (the pattern repeats from the first corner) |
| `bucket-droplet` | Bucket | B | Flood fill the 4-connected equal cells; **Contiguous** off: every equal cell in the used rect; empty space is bounded by the used rect grown by one chunk |
| `color-picker` | Picker | P | Click a cell to pick its tile (flips included); Cmd/Ctrl+click picks from any tool |
| `eraser` | Eraser | E | Erase with the tool's shape; a right drag erases from any tool |
| `dice-5` | Random tile | — | Paint one of the picked tiles per cell, weighted by `Probability`; **Scattering** (−/+, 0–1) leaves that fraction untouched |
| `flip-vertical`, `flip-horizontal`, `rotate`, `rotate-clockwise` | Transform | C, V, Z, X | Flip or turn the brush (cells' flags and the pattern's layout; Godot's rotation cycle) |
| `chevron-left`/`-right`, `stack-2` chip, `highlight` | Layer | — | The previous / next sibling layer; Highlight dims the others (`CanvasItem.EditorModulate`, never saved) |
| `grid-3x3` | Grid | — | Cell grid and chunk borders |
| `layout-grid`, `adjustments` | Palette, Edit TileSet | — | The bottom dock's TileMap / TileSet tab |

- **Strokes** apply live (line and rect on release), and each is one undo entry: "Paint Tiles", "Erase Tiles",
  "Fill Tiles", "Paste Tiles", "Cut Tiles" (`PaintTilesAction`: each cell before and after). Esc or a right click during
  a stroke puts every cell back. A ghost of the brush follows the mouse at 50 % alpha (`TilePaintPreview`: an unowned
  `TileMapLayer` beside the scene root in the tab's view; never saved, simulated or listed); erase strokes outline their
  cells in red.
- **Shortcuts.** Godot's tile keys; E and R (also the gizmo's Rotate/Scale) are the tile keys only while a paint tool
  other than Select is active, as in Godot's editor; Q leaves painting.
- **Picking.** With no layer edited, a click on a painted cell selects its layer; box selection uses the layer's used
  rectangle.
- **TileMap tab** (bottom dock, opens when a layer is selected): the source list, the atlas (created tiles bright,
  others dimmed; click picks, drag picks a rectangle pattern, Cmd/Ctrl+click toggles a tile of the random set; zoom
  buttons), the picked tile's alternatives, New Alternative and Edit TileSet.
- **TileSet tab** (the layer's tile set, or a `TileSet` `.mres` open in the inspector): add a source from an image (it
  asks whether to create tiles in the non-transparent regions), remove it, change its id; physics and custom data layers;
  **Setup** (margins, separation, region size, tile size; a click creates or removes a tile), **Select** (the tile's
  texture origin, modulate, z index, y-sort origin, probability and every custom data layer) and **Collision** (per
  physics layer: Full Tile, Add Polygon, Clear, Snap ½ / ¼ tile / 1 px; points placed and moved by clicking snap points,
  Delete removes the selected one). Every edit is one undo entry (`TileSetSnapshot`) in the resource's or the scene's
  history.

![The TileSet tab's collision editor on the bush tile](../images/editor-tileset.png)

## Navigation hook

[Navigation](future/navigation.md) (G3a, G3.6) reads a layer through public API: `GetUsedCells`, `GetCellTileData`,
collision polygons on a chosen physics layer (`TileData.Collision`, `TileCollisionPolygon.PhysicsLayer`) or a custom data
bool (`TileSet.GetCustomDataLayerByName`, `TileData.GetCustomBool`), and `Changed` to rebuild. Godot's per-tile
navigation polygons (`TileSetNavigationLayer`) are reserved for that milestone.

## Demo scene

`tiles_2d` ("Tiles 2D", icon `grid-pattern`) in `DemoScenes.All` ([Demo](demo.md)): a generated 4 × 2 atlas of 16 px tiles
(`TilesArt`, `Assets/Tiles2D/tiles.png`: grass, path, stone, water, a tree's canopy and trunk, flowers, a bush) and its
`tiles.mres` (one physics layer: full tiles on stone and water, polygons on trunks and bushes; custom data `surface`),
written by `--write-scenes`. Three layers from an ASCII level — "Ground", "Walls" and the y-sorted "Objects" — and the
Sprites 2D hero (`hero.mres`) as a `TileHero2D` inside the objects layer: it walks the paths (or the `move_*` actions:
WASD, arrows, left stick), plays idle or run, and reports the `surface` under its feet; a limited `Camera2D` follows.
The panel shows collision, toggles y-sort, sets the walk speed and autopilot, and a click on the map adds or removes a
wall (live chunk and body rebuild). The fourteenth tab: past the tenth, tabs are reached with the nav bar or
`scene_next`/`scene_prev`.

## Tests

- **Unit:** `Canvas/TileMapLayerTests` (chunks, negative coordinates, coordinates, used cells, re-chunking, sub-item
  order and culling, y-sorted rows with children, z sub-items, flips, invalid tiles, padding, opaque regions,
  alternatives, custom data, a 0-byte scrolling 256 × 256 layer with y-sorted objects); `Canvas/TileMapSerializationTests`
  (chunk lines, determinism, a Godot record array, bad data, the layout rule, the `Storage` hint, a `TileSet` `.mres`);
  `Physics/TileBodies2DTests` (merged rectangles, decomposition, rays and point queries reporting the layer,
  `GetCoordsForCollision`, flips, a character sliding along 20 tiles across two chunks, erasing, moving and scaling the
  layer, boxes resting and falling, an area seeing the layer once, no bodies without physics layers, 0 B stepping).
- **Editor:** `TilePaintTests` (stamp, Bresenham, rect patterns, bucket contiguous/global/bounds, seeded random scatter,
  Godot's transforms, the shortcut table) and `TileMapEditorTests` (the bar and tabs follow the selection, strokes as one
  undo entry with exact undo/redo, line/rect/bucket, picker, select/copy/paste/delete, shortcuts and Esc, layer switcher
  and highlight, the ghost, TileSet edits undone).
- **Render:** `tiles` golden (flips and transposes, y-sorted rows with sprites behind and in front), `tiles-seam`
  (no neighbour at zoom 1.37 with linear filtering and padding), `tiles --count 256` allocation gate.
- **Benchmark:** `TileMapBenchmarks.TileMapLayerFrameBuild` in `baseline.json`.
- **Demo:** `Tiles2DSceneTests`; `SceneFilesTests` covers `tiles_2d`. QA: `readme-screenshots.qa` (editor-tilemap,
  editor-tileset) and `editor-walkthrough.qa` steps.

## Changes from the proposal

- **Collision results** widened to `Node2D`, as recommended; no shape tag on hits (`GetCoordsForCollision` uses the
  half-pixel idiom). Areas and contact monitors de-duplicate a layer's chunk bodies.
- Tile bodies are only created with physics layers; rebuilds run in the space's flush (step, query, debug draw) rather
  than `BeforeFixedSteps`, which edit mode never calls.
- Rebuilt bodies keep their Box2D body and replace its shapes.
- `UvClipping` is stored but not applied (padding prevents bleeding).
- `TileData` has no flip/transpose members of its own (alternative flags only).
- Editor: the paint bar overlays the view instead of shrinking it; tile data is edited in the TileSet tab, not the
  inspector; collision points are placed and moved by clicking snap points (no drag, no edge double-click); the palette
  zooms with buttons; the SpriteFrames editor stays in the inspector (ADR 0224).
- `CanvasItem.EditorModulate` (editor-only dimming) is new.
- The scrolling-camera allocation gate scrolls the layer: a moving `Camera2D` in the full renderer allocates ≈ 2 KB per
  frame through the 3D path's orthographic camera (unrelated to tiles; a follow-up).

## Later (G2b.6)

Terrains (Godot's terrain sets and solver, a Terrain paint tab), atlas tile animation, big tiles (`size_in_atlas`),
isometric and hexagonal shapes, scene tiles, tile patterns as resources, occlusion and navigation layers, one-way
collision, runtime tile data overrides, a Godot `.tres`/`.tscn` importer.

## Related

[2D canvas](canvas.md) · [Animated sprites](animated-sprites.md) · [Physics](physics.md) · [Editor](editor.md) ·
[Scene serialization](scene-serialization.md) · [Demo](demo.md) · [Navigation](future/navigation.md) (G3) ·
[Milestones](../milestones.md#gameplay-toolkit-)
