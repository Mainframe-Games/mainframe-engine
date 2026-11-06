# ADR 0272 — Rivers are edited by the curve tool, with section handles and undoable carves

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8c (G8c.4)
- **Docs:** docs/design/water.md#editor, docs/design/paths.md#editor, memory/plans/2026-11-07-river-tool.md

## Context

The water proposal specified a `River3DTool` on the viewport-tool API (ADR 0270) with points, tangents, width and depth
handles, snap and undo, and Carve / Re-carve / Uncarve buttons. Since then the curve tool (G13, ADRs 0242/0243) has
grown into a full `Curve3D` editor in the 3D view: points, mirrored tangents, Add, Insert, Delete, ground snap, Fit to
Ground, snap to the move step, one `SetCurvePointsAction` per gesture. A `River3D`'s shape is a `Curve3D` with two extra
per-point values (width, depth). A carve writes heights, the terrain's carve record and the river's `CarveId`.

## Decision

- **Rivers join the curve tool.** `CurveEditor` edits the selected `River3D`'s curve like a `Path3D`'s
  (`Curve3DTarget` over any `Node3D` + `Curve3D`); `ICurveTarget` gains width and depth (`HasWidth`). Rivers have no tilt
  ring and keep their own ground snap (on, −0.3 m), remembered apart from paths'. New points continue the last section.
- **Width and depth are curve-tool parts** (`CurvePart.Width`, `CurvePart.Depth`) on the selected point, so their drags
  are curve gestures with the same undo and Esc; every point draws its cross-section.
- **Carving is undoable as a unit:** `River3D.LastCarveChange` (`RiverCarveChange`) holds the height tiles, the carve
  record and `CarveId` before and after; the inspector commits it as `RiverCarveAction`. Fit to ground is one curve
  change.

## Ruled out

- **A separate `River3DTool`.** It would duplicate the curve tool's point editing, snapping and undo for one node type.
- **G7 handle providers for width and depth.** They capture exported property values for undo; the values live inside
  the curve's point array, so undo would not restore them.
- **Undoing a carve with the terrain edit alone.** The carve record and `CarveId` would stay, and the next Re-carve
  would restore the wrong ground.

## Consequences

- One tool and one set of keys for every curve; game nodes with a `Curve3D` can join the same way.
- Section handles only on the selected point: wide rivers stay readable, and selecting a point is one click.

## Revisit when

- Rivers need per-point editing the curve bar cannot hold (pond joins, falls markers): give them their own bar buttons or
  a viewport tool then.
