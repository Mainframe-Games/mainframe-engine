# ADR 0250 — Markers are rows of one host document, moved from C# between content refreshes

- **Date:** 2026-11-06
- **Status:** accepted
- **Milestone:** G11 (part: G11.4)
- **Docs:** docs/design/world-space-ui.md#markers, memory/plans/2026-11-06-world-space-ui-g11.md

## Context

The proposal's `UiMarker3D` draws every marker as a row of a `data-for` list in one `UiMarkerHost` document (no
per-marker documents, model-name clashes or element creation in the shim), placed with a data-bound `transform`. Open
question 3 asked whether that is cheaper than moving elements from C#, open question 4 whether shim element creation is
needed. The target was 100 markers in ≤ 0.3 ms and 0 B per frame.

## Decision

- **The row model** (open question 4's default: no shim rebuild). `UiMarkerHost : UiDocument` binds `markers` as a list of
  `MarkerRow` structs holding the markers' own (translated) strings; the default document
  `Assets/UI/markers/markers.rml` styles `nameplate`, `health`, `prompt` and `waypoint`.
- **Measured (open question 3):** dirtying the list every frame made RmlUi re-evaluate every view of every row: about
  1.1 ms for 100 four-kind rows, against 0.32 ms for a row with only the transform binding. So the host dirties the model
  only when content changes (markers added or removed, kind, text, value, visibility, edge state, an edge arrow turning
  more than 2°) and every `ContentRefreshSeconds` (0.1 s: re-sort, distance text). On other frames it sets `transform`
  and `opacity` on the generated row elements directly (`RmlElement.GetChild`; RmlUi keeps the `data-for` template after
  the rows it generates). Measured: about 0.55 ms per frame for 100 markers, 0 B, on a busy machine.
- `UiDocument.PrepareFrame` became an internal virtual; markers find hosts through tree groups; occlusion uses
  `PhysicsDirectSpaceState3D.RayCast` round robin, excluding the nearest `CollisionObject3D` ancestor.

## Consequences

- A custom marker document must keep each row a direct child of one parent, with the template after the rows (what
  `data-for` produces), or the host falls back to dirtying every frame.
- Distance text and draw order lag by up to `ContentRefreshSeconds`.
- 0.55 ms is above the proposal's 0.3 ms target: the remaining cost is RmlUi's update of 100 moved elements. Re-measure
  on the quiet baseline machine before tuning further.
- Tests: `UiMarkerTests` (projection, fade, scale, edge clamp, occlusion, sort, signals, element mode, the fast path,
  0 B), render test `ui-markers` (`#3366cc` exact), `WorldUiBenchmarks.Markers100Frame`.
