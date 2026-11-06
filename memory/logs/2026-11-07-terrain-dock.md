---
summary: >-
  G8a: the viewport-tool API (IEditorViewportTool, routing before the gizmo, the inspector's tool tab, a save hook;
  ADR 0270) and the Terrain dock on it: create terrain data, sculpt (raise, lower, smooth, flatten, set height, ramp),
  paint (Faceted surfaces, Realistic layers, user channels, rectangle fills), water, a draped brush ring and label, one
  undo per stroke.
---

# 2026-11-07: Terrain dock and the viewport-tool API (G8a)

Branch `feature/terrain-dock`. Plan: [Terrain dock](../plans/2026-11-07-terrain-dock.md). As built:
[Terrain → Editing](../../docs/design/terrain.md#editing), [Editor → Viewport tool API](../../docs/design/editor.md#viewport-tool-api).

## What changed

- Editor: `Viewport/Tools/` (`IEditorViewportTool`, `ViewportTools`), `ViewportController.Tools.cs` (`DragKind.Tool`),
  `InspectorPanel.Tool.cs` (`#tab-tool`, `#tool-body`), `EditorSession.Saving`; `Terrain/` (`TerrainBrush`,
  `TerrainStroke`, `TerrainBrushOps`, `TerrainEditAction`, `TerrainEditorTool`).
- Engine: a terrain's chunk collision shapes are hidden from Visible Collision Shapes (`Visible = false`; no effect on
  collision): the wireframe covered the ground and the brush.
- Icons: `plant-2`, `tree`, `trees`, `leaf`, `trending-up`, `square-dashed` (the last two used now; the rest for G8a
  Foliage and the G8b tree inspector).
- QA: `tool-action`, `tool-field`, `world-move`, `world-drag`, `save-scene`, `add-node … [parent|.]`; the walkthrough's
  Terrain steps; `editor-terrain.png` in `readme-screenshots.qa`, the README and the landing page.
- Tests: `ViewportToolTests` (probe tool: activation, dock tab, capture, cancel, navigation, keys, dock clicks, save
  order and failure), `TerrainBrushTests`, `TerrainDockTests` (create + undo, raise with bit-exact undo/redo, Shift
  smooth, flatten, pick, ramp, Faceted paint/erase/pick, Realistic layer blend, channel rectangle fill, water, hotkeys,
  save writes the layers).

## Decisions

- [ADR 0270](../decisions/0270-viewport-tool-api.md). ADR numbers 0270–0279 are this G8 session's (siblings were at 0261).

## Dead ends

- Spacing-only stamping made a slow drag barely raise the ground (≈ 1 m over a QA stroke); height tools now also stamp
  every 0.1 s while held.
- A QA `add-node` under a selected `DirectionalLight3D` parented the terrain to the rotated light: `add-node` takes a
  parent (`.` = root).

## Not done

- Foliage and Objects modes (need painted density layers and object scatter), the Faceted `TerrainMaterial3D` (the dock's
  Faceted swatches are a grey ramp until it lands), brush mask PNGs, axis lock, sub-rectangle uploads.
