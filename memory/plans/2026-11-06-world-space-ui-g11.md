# Plan: G11 world-space UI — text layout, Label3D, markers

**Status:** done (G11.1–G11.4), see [log](../logs/2026-11-06-world-space-ui-g11.md)
**Docs:** [World-space UI proposal](../../docs/design/future/world-space-ui.md) (task list G11.1–G11.4),
[Game UI](../../docs/design/game-ui.md), [Canvas → Text](../../docs/design/canvas.md#text-adr-0118),
[ADR 0200](../decisions/0200-label3d-first-slice.md), [ADR 0216](../decisions/0216-viewport-textures-on-3d-materials.md)
**Started:** 2026-11-06

## Goal

The proposal's fallback scope (one PR): G11.1's frustum helpers, G11.2's shared multi-line text layout with fallback
fonts, G11.3's full `Label3D` option set, and G11.4's `UiMarker3D` markers on one host document. The weekly token budget
was at 80 % when this started (stop line 90 %), so G11.5–G11.8 are follow-ups unless budget remains.

## Not in scope

- G11.5 UI render targets and G11.6 `UiPanel3D` (`IPreSceneRenderer`, panel contexts, ray input), with them
  `Texture2D.FromGpuSource` (its only consumer is the panel; sub-viewports already borrow their image, ADR 0216).
- The batched `Text3DRenderer` and `text3d.slang`: labels stay on the mesh batcher (ADR 0249).
- G11.7's panel preview; G11.8's Demo tab (follow-up if budget remains); G11.9.

## Design notes (decided here)

- **ADR 0249: `Label3D` stays on the mesh batcher.** Multi-line, autowrap, `Width`, `LineSpacing`, `Uppercase` and
  fallbacks change only the mesh it builds; `FixedSize` is a CPU model-matrix scale next to the billboard (ADR 0199's
  approach), so picking, the prepass and casters see it; `AlphaCut` maps to `AlphaMode.Scissor`; `RenderPriority` of the
  text is `TextRenderPriority`. One draw per label and page instead of per page; the batched renderer stays open.
- **`TextLayout`** (internal, `Src/Text/`): Label2D's line breaking (newlines, `AutowrapMode`, greedy words, the
  arbitrary breaks) moves there unchanged so canvas goldens do not move; `Font.Layout` gains fallback faces.
- **Fallbacks.** `Font.Fallbacks` is searched when the primary face has no glyph; fallback glyphs share the primary
  font's atlas pages (cache key carries the face). `Label3D` with no explicit fallbacks uses the project's
  `FontFallbackTable` for the current locale.
- **ADR 0250: markers are rows of one host document** (the proposal's row model, open question 4's default), placed
  with a data-bound `transform` (open question 3's default; benchmark records the cost).

## Steps

- [x] **Frustum helpers.** `Camera3D.IsPositionInFrustum`, `GetFrustumPlanes(Span<Plane>)`. Verified by:
  `Camera3DProjectionTests` (known points in and out, planes face inward).
- [x] **Text layout.** `TextLayout.BreakLines`, `Font.Fallbacks`, `Label2D` on the shared code. Verified by:
  `TextLayoutTests` (newlines, wrap modes equal Label2D's old output, fallback glyphs), canvas goldens unchanged.
- [x] **Label3D.** Multi-line, `Width`, `Autowrap`, `LineSpacing`, `Uppercase`, `FixedSize`, `AlphaCut`,
  `TextRenderPriority`, locale fallbacks, `GetAabb`. Verified by: `Label3DTests`; `label3d` golden (both drivers).
- [x] **Markers.** `UiMarker3D`, `UiMarkerHost`, `Assets/UI/markers/markers.rml` (four kinds), distance fade/scale,
  edge clamp + angle, physics occlusion with fade, element mode, signals. Verified by: `UiMarkerTests`, `ui-markers`
  golden, allocation gate (100 markers, 0 B), `WorldUiBenchmarks`.
- [x] **Docs and close.** `docs/design/world-space-ui.md` (current state, "Changes from the proposal"; the proposal keeps
  G11.5–G11.9), game-ui, canvas, localization, testing, milestones, AGENTS.md, README, landing page. Verified by:
  `check.sh`, `just docs-build`.

## Done when

- The gates in `.agents/workflow.md` pass; the new goldens are inspected; the allocation gate is 0 B.
- The PR lists the open questions decided here and what of G11 is left.
