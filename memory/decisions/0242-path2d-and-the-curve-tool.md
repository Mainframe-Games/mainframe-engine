# ADR 0242 — Path2D, PathFollow2D and the curve tool: the second G13 slice

- **Date:** 2026-11-05
- **Status:** accepted
- **Milestone:** G13 (part: G13.2, G13.4, G13.7)
- **Docs:** docs/design/paths.md, docs/design/future/paths.md, memory/plans/2026-11-05-paths-2d.md

## Context

ADR 0215 shipped `Path3D` and `PathFollow3D` for Neon. G13's proposal also designs 2D paths sharing `Curve3D`'s baker,
and an editor curve tool shared by 2D, 3D and rivers that builds on G8a.2's `IEditorViewportTool`, which is still a
proposal. 2D games need paths now, and paths could only be authored in code.

## Decision

- **One baker.** The adaptive subdivision, equal-arc resampling and tangents move out of `Curve3D` into an internal
  `CurveBaker`, fed with each segment's absolute control points (2D with z = 0). `Curve3D` keeps its tilt, width, depth
  and up vectors, computed from the baked parameters. The arithmetic and its order are unchanged:
  `Curve3DTests.FixedCurveBakesToACommittedHash` holds, and `Curve2D` has its own committed hash.
- **`Curve2D`** is Godot's API in pixels plus the 3D slice's extensions (`Closed`, `SampleBakedTangent`, `WrapOffset`,
  `SetSmoothPoints`, `Reverse`); `Curve3D` gains `Reverse` too (tilt negated).
- **`Path2D` / `PathFollow2D`** mirror the 3D nodes with Godot's 2D names (`Rotates` instead of a rotation mode;
  offsets on the path's axes when not rotating, as Godot). The followers share one `PathMotion` helper for wrap, clamp,
  loop and ping-pong, so 2D and 3D cannot drift apart.
- **The curve tool is built 2D first, on an `ICurveTarget`** (points and handles in local 3D coordinates plus the
  node's transform) so G13.6 adds a `Path3D` target instead of a second tool. It is wired like the tile map editor
  (workspace-owned `CurveEditor`, press/move/release from the viewport controller, a bar in the paint bar's slot), not
  as an `IEditorViewportTool`, which does not exist yet; it becomes one when G8a.2 lands.
- **Undo** is one `SetCurvePointsAction` per gesture: the whole point array before and after, handed to the curve as
  copies, and every gesture starts the curve on a fresh copy, so the history never shares the curve's live storage
  (in-place point writes would otherwise rewrite past entries).
- New `Path2D`/`Path3D` from Add Node get an inline curve (`NewNodeDefaults`), not from their constructors, which must
  stay cheap and free of side effects.

## Consequences

- 2D paths can be drawn and edited in the editor; 3D paths still only in code until G13.6.
- Left in G13: moving platforms (G13.5), the 3D tool with tilt and ground snap (G13.6), the closest-point grid and
  hinted query, the Forest rails (G13.8).
- Icons `vector-bezier` (`Curve2D`) and `arrows-exchange` (Reverse) joined the editor atlas.
