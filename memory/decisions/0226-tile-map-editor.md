# ADR 0226 — Tile map editing: paint bar, contextual bottom tabs, TileSet tab

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/tile-maps.md#editor, docs/design/editor.md

## Context

The proposal (G2b.4) asks for paint tools in the 2D view, a TileMap palette and a TileSet editor in a tabbed bottom
panel, undo per stroke, and leaves the shortcut conflict (E/R are also the gizmo's Rotate/Scale) open. G10 has since
turned the bottom dock into tabs (Output | Profiler); ADR 0224 left the tabs to G2b.

## Decision

- **Paint bar**: an overlay bar over the top of a 2D view while the selection is a `TileMapLayer` with a `TileSet`
  (no layout rectangle changes; `viewport.rml` `#paint-bar`, built by `TileMapEditor.PaintBarRml`). Icon buttons with
  tooltips; every button is an `EditorCommands` id (`tilemap.*`), so QA scripts and shortcuts run the same code.
- **Strokes**: the left button paints with the tool, the right button erases with it, Shift = line, Cmd/Ctrl+Shift =
  rect, Cmd/Ctrl+click = picker; strokes apply live (line/rect on release) and commit one `PaintTilesAction`
  (`alreadyApplied`) on release; Esc or a right click cancels and restores every cell.
- **Shortcuts** (the proposal's recommendation): Godot's tile letters, but E and R win over the gizmo only while a paint
  tool other than Select is active; Q leaves painting. Cmd+C/X/V, Delete and Esc act on a cell selection.
- **Bottom dock**: contextual **TileMap** and **TileSet** tabs (one `TileMapPanel` document, `tilemap.rml`) beside
  Output | Profiler; they are never persisted. TileMap opens when a layer becomes the selection and both leave with it.
- **Ghost**: an unowned `TileMapLayer` at 50 % alpha beside the scene root in the tab's view (never saved, simulated or
  listed), following the layer's transform, tile set and filter; erase strokes draw red cell outlines.
- **TileSet edits** are snapshot actions (`TileSetSnapshot`, like ADR 0224's SpriteFrames): one undo entry per edit in
  the inspected `.mres` history or the scene's (an external tile set is then saved with the scene).
- **Deviations**: the selected tile's data is edited in the TileSet tab beside the atlas, not in the inspector (inline
  sub-resources have no inspector target); collision points are placed and moved by clicking snap points (½, ¼ tile or
  1 px), Delete removes the selected one — no drag or edge double-click; the palette zooms with buttons (the atlas view
  scrolls); Highlight dims sibling layers through `EditorModulate`.

## Consequences

- One more panel document (14 in the panel layer). Picking also hits painted cells of tile map layers, and box
  selection uses a layer's used rectangle.
- Not built: terrains, patterns as resources, atlas tile animation, big tiles, scene tiles (G2b.6 / later).
