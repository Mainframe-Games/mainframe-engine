---
summary: >-
  G8c.4: River3D editing in the editor on the curve tool (points, tangents, its own ground snap at −0.3 m, bank and depth
  handles, cross-sections) and the River3D inspector's Carve, Re-carve, Uncarve and Fit to ground, every step undoable
  (RiverCarveChange for carves).
---

# 2026-11-07: River editing and carving in the editor (G8c.4)

Branch `feature/river-tool` (stacked on `feature/tree-inspector`). Plan: [River tool](../plans/2026-11-07-river-tool.md).
As built: [Water → Editor](../../docs/design/water.md#editor).

## What changed

- Engine: `River3D.LastCarveChange` / `RiverCarveChange` (heights, the terrain's carve record and `CarveId`, undo/redo).
- Editor: `ICurveTarget` width/depth; `Curve3DTarget` over any `Node3D`; `CurveEditor` takes `River3D` (`IsRiver`),
  per-kind ground snap, sections continue on Add, `CurveEditor.River.cs` (width/depth handles, labels, cross-sections);
  `River3DInspector` + `RiverCarveAction`.
- Tests: `RiverEditingTests` (7), `RiverCarveTests.ACarveChangeUndoesAndRedoesHeightsRecordAndId`.
- QA: `readme-screenshots.qa` 3g → `docs/images/editor-river.png`; README, landing page.

## Decisions

- [ADR 0272](../decisions/0272-river-editing-on-the-curve-tool.md): the curve tool instead of a `River3DTool`.

## Not done

- Pond joins, plunge pools, river audio. The river's generated `Ribbon` and `Falls` children show in the scene tree
  (as before this change), the empty `Falls` with a warning icon; and the default water reads faintly in the editor view.
