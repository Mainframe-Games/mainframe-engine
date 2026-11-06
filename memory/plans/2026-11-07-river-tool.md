# Plan: River editing and carving in the editor (G8c.4)

**Status:** done, see [log](../logs/2026-11-07-river-tool.md)
**Docs:** [Water proposal → Editor](../../docs/design/future/water.md#editor), [Water](../../docs/design/water.md),
[Paths → Editor](../../docs/design/paths.md#editor), [ADR 0149](../decisions/0149-terrain-trees-water-engine-features.md)
**Started:** 2026-11-07

## Goal

A `River3D` selected alone is edited in the 3D view like a `Path3D` (points, tangents, add, insert, delete, ground
snap), plus width handles at both banks and a depth handle below the selected point, cross-sections drawn for every
point, and Carve / Re-carve / Uncarve / Fit to ground buttons in its inspector — every gesture and carve one undo entry.

## Not in scope

- Pond joins and plunge pools (G8c.3 extras), river audio (G8c.5).

## Steps

- [x] Engine: `River3D.LastCarveChange` (`RiverCarveChange`: the height tiles, the carve record and `CarveId` before and
      after, `Undo()` / `Redo()`). Verified by: unit tests, carve + undo + redo bit-exact heights, record and id.
- [x] Curve tool: `ICurveTarget` width/depth; `Curve3DTarget` over any `Node3D` + `Curve3D`; `River3D` as a 3D target
      (ground snap on at −0.3 m for rivers, remembered apart from paths); new points copy the previous point's width
      and depth (first: 3 m, 0.6 m). Verified by: editor tests.
- [x] Width and depth handles on the selected point, cross-sections for all points, "Width 3.20 m" / "Depth 0.60 m"
      labels, snap to the move step, one undo per drag (Esc restores). Verified by: editor tests through the input path.
- [x] `River3DInspector`: Carve, Re-carve, Uncarve, Fit to ground icon buttons with tooltips and a status line; undoable.
      Verified by: editor tests.
- [x] README/landing screenshot `editor-river`, QA steps. Verified by: the capture inspected.
- [x] Docs: water.md (Editor, changes from the proposal), paths.md pointer, milestones G8c, ADR, log. Verified by: check.sh.

## Done when

- Gates pass; in the editor a river's point, width and depth drags and a carve each undo in one step, shown by tests.

## Open questions

- None that change what gets built.
