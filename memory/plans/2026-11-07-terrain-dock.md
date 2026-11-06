# Plan: Terrain dock and the viewport-tools API (G8a.2, G8a.5, G8a.6, G8a.10's Water mode)

**Status:** done, see [log](../logs/2026-11-07-terrain-dock.md)
**Docs:** [Terrain proposal](../../docs/design/future/terrain.md) (Editor: `IEditorViewportTool`, The Terrain dock),
[Terrain](../../docs/design/terrain.md) (what exists), [Editor](../../docs/design/editor.md),
[ADR 0149](../decisions/0149-terrain-trees-water-engine-features.md)
**Started:** 2026-11-07

## Goal

A `Terrain3D` selected in a 3D tab gets a **Terrain** tab in the right dock (an icon tab beside Properties and Signals)
that creates terrain data and sculpts, paints and lays water with a brush ring on the ground, per-stroke undo/redo and a
save that writes the changed layer PNGs. The tool runs on a general `IEditorViewportTool` API that the river tool (G8c.4)
reuses.

## Not in scope

- Foliage density painting and object scatter (`ScatterObjectType`, `objects.mfres`, `ITerrainObject`): the next PR.
- The Faceted `TerrainMaterial3D` (its own PR); `Texture2D.SetPixelsRect` region uploads (G8a.1) unless painting
  Realistic splats proves too slow to use (then it moves into this PR).
- Custom brush mask PNGs (built-in circle and square only; TerraBrush-style masks later).

## Steps

- [x] `IEditorViewportTool`, `[EditorViewportTool]`, discovery (reset on code reload), `ViewportTools` host per tab,
      `DragKind.Tool` routing in `ViewportController` (reserved navigation first, capture, `Cancel`), overlay drawing,
      cursor label. Verified by: editor tests with a `MarkerPlacerTool` (discovery, routing, capture/cancel, lifecycle,
      undo after deselect).
- [x] Dock: an icon tab in the inspector header shown while a tool is active; the tool renders generated RML (the
      TileMap panel's pattern, not an RmlUi data model). Verified by: editor test that selecting a `Terrain3D` shows the
      tab and deselecting hides it.
- [x] Save hook: `EditorSession.Save` calls each tool's `SaveExternalData` before `SceneSaver.Save`; a throw fails the
      save and keeps the tab dirty. Verified by: editor tests (order, failure).
- [x] `TerrainEditorTool`: Create terrain data (profile, size, spacing; undoable `Data` set), brush (size, strength,
      falloff, spacing, slope limits), stamp spacing independent of the event rate, ring + falloff ring + label,
      hotkeys (`[` `]` `-` `=` Esc). Verified by: editor tests (brush weights, spacing at 1 and 50 events per stamp).
- [x] Sculpt: Raise, Lower, Smooth, Flatten, Set height (Ctrl+click picks), Ramp; Shift → Smooth;
      `TerrainEditAction` over `TerrainEdit` records (Deflate tiles), one entry per stroke. Verified by: tests per op and
      bit-exact undo/redo.
- [x] Paint: Faceted surface ids, Realistic layer weights (renormalised), user channels; Shift eraser, Ctrl pick,
      rectangle fill. Verified by: tests per rule.
- [x] Water: depth brush with Level. Verified by: test that `WaterDepthAt` follows the stroke and undo restores it.
- [x] QA: `Tests/QA/editor-walkthrough.qa` creates data, sculpts, paints, undoes, saves; a README/landing screenshot
      through the readme-screenshots QA script. Verified by: `just qa-editor` passes, PNG inspected.
- [x] Docs: `docs/design/editor.md` (viewport tools, Terrain dock), `docs/design/terrain.md` (editing, known gaps),
      terrain proposal task list ticks, milestones row, ADR for the viewport-tools API, log. Verified by: `check.sh`.

## Done when

- Gates pass: `just build`, Release `-warnaserror`, `just test`, `just test-render`, `just format-check`,
  `just shaders-check`, `check.sh`.
- In the editor, a new `Terrain3D` gets data from the dock, a sculpt stroke and a paint stroke each undo in one Cmd+Z,
  and Cmd+S writes only the changed PNGs (byte-identical after reopening) — shown by `just qa-editor`.

## Open questions

- None that change what gets built.
