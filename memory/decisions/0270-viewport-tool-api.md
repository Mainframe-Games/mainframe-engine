# ADR 0270 — Viewport tools: one API for tools that own the 3D view, their dock as an inspector tab

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8a (G8a.2, G8a.5, G8a.6)
- **Docs:** docs/design/editor.md#viewport-tool-api, docs/design/terrain.md#editing, docs/design/future/terrain.md,
  memory/plans/2026-11-07-terrain-dock.md

## Context

The terrain brush, the river tool (G8c.4) and game plugins need to take over the 3D view while one node is selected:
see clicks before the gizmo and picking, draw rings and lines, show a dock of settings and get a save hook. The editor
had two hard-wired tools (the tile paint bar, the curve tool), each with its own branch in `ViewportController`. The
proposal specified `IEditorViewportTool` with an RmlUi data model (`viewport_tool`) for the dock, a separate tab strip
"Inspector | Terrain" and Deflate-compressed undo tiles.

## Decision

- `IEditorViewportTool` + `[EditorViewportTool(nodeType, Title, Icon)]`, found by the editor plugin scan
  (`EditorPluginTypes`, reset on code reload with the custom inspectors and handle providers). `ViewportTools` keeps one
  instance per tab and tool type until the tab closes, activates the tool of the single selected node in a 3D tab (none
  while simulating or in Music/Video mode), and calls `Process(dt)` each frame.
- Routing in `ViewportController`: navigation first (Alt+left, middle, right, the plain wheel, keys with Command), then
  the tool (a consumed left press captures the mouse as `DragKind.Tool` until release; Esc, a right click, a tab switch or
  deactivation send `Cancel`), then handles, gizmo and picking. Shift/Alt + wheel and keys over the view reach the tool;
  modifier keys are never swallowed (the workspace tracks Shift/Alt/Command from them). The node's transform gizmo is
  hidden while its tool is active.
- **The dock is the inspector's third tab** (an icon button beside Properties and Signals, shown only while a tool is
  active, opened when one activates). Its body is RML the tool generates (`BuildDockRml`, rebuilt when `DockVersion`
  changes) with `data-tool-action` clicks and `data-tool-field` inputs — the TileMap panel's pattern, not a data model.
  A slider edit does not rebuild the body (the drag survives).
- **Save hook:** `EditorSession.Saving` runs before the scene file is written; `ViewportTools` calls every instance's
  `SaveExternalData`; a throw fails the save. The terrain itself needs none: `Terrain3D` already writes its layers in its
  scene save hook.
- **Terrain undo** keeps `TerrainEdit`'s chunk tiles uncompressed (`TerrainEditAction`): a 64 m chunk at 1 m is ≈ 34 KB
  before + after; the 256-entry history stays in the tens of MB for normal strokes.
- Height tools stamp along the drag every `Spacing` × size *and* at least every 0.1 s while the button is held (a slow
  drag or a still mouse keeps raising); paint and water converge in one pass and do not repeat.

## Ruled out

- **An RmlUi data model for the dock.** Binding would need a model per tool type with every field declared in C#; the
  generated-RML pattern is already the editor's way for dynamic panels (TileMap, Signals) and tests can read the markup.
- **A separate dock tab strip above the inspector.** The inspector header already has icon tabs; a third one costs no
  layout and follows the icons-over-text rule.
- **Deflate-compressed undo tiles.** Measured sizes are small; compression can be added inside `TerrainEditAction` if a
  2 km terrain's strokes show memory pressure.
- **Porting the curve and tile tools onto the API now.** They work and are tested; moving them is churn without a user.

## Consequences

- The river tool and game plugins get a viewport tool by implementing one interface; `MarkerPlacer`-style tools are
  testable headless (`Tests/MainframeEngine.Editor.Tests/ViewportTools/`).
- The curve and tile tools still have their own branches in `ViewportController` until someone ports them.

## Revisit when

- A tool needs a dock that generated RML cannot express (live previews, drag lists): consider a data-model mode.
- Terrain undo memory shows up in a profile (2 km maps, long sessions): compress the tiles.
