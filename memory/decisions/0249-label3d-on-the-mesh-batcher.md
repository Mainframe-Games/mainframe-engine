# ADR 0249 — Label3D stays on the mesh batcher; multi-line layout and fallbacks in shared code

- **Date:** 2026-11-06
- **Status:** accepted
- **Milestone:** G11 (parts: G11.1, G11.2, G11.3)
- **Docs:** docs/design/world-space-ui.md, memory/plans/2026-11-06-world-space-ui-g11.md

## Context

The G11 proposal designs a batched `Text3DRenderer` with its own Slang shader and eight pipelines (one draw per atlas
page for every label of a world, fixed size and billboards in the vertex stage). ADR 0200 shipped `Label3D` as an
`ArrayMesh` of glyph quads drawn by the ordinary mesh batcher (one draw per label and page). This change had to fit a
nearly spent weekly budget, and the renderer is the largest, riskiest part of G11.3 (new pipelines, object IDs, a fog
path, goldens on both drivers).

## Decision

- **No `Text3DRenderer` yet.** `Label3D` keeps building its mesh and gains the proposal's options on it: lines at
  `\n`, `Width`, `Autowrap` (Godot's modes), `LineSpacing`, `Uppercase`, `AlphaCut` (cutout material),
  `TextRenderPriority`, `GetAabb`, `GetLines`. The mesh is still rebuilt only on change.
- **`FixedSize` on the CPU**, next to ADR 0199's billboard: `GeometryInstance3D.RenderFixedPixelSize` and
  `Billboard.FixedSize` scale the model matrix per view by `depth / LodSelection.PixelScale / PixelSize`, so picking,
  the prepass, motion vectors and casters see the same quad and no shader changes.
- **Shared layout.** `TextLayout.BreakLines` (internal) is `Label2D`'s greedy breaking moved unchanged, so canvas goldens
  do not move; both labels call it. It returns lines (strings) rather than the proposal's `GlyphQuad` span, because
  layout runs only on change.
- **Fallback fonts.** `Font.Fallbacks` (Godot's `fallbacks`) and an extra list per call are searched when the face lacks
  a character; their glyphs go into the primary font's atlas pages (the cache key carries a per-font face id), kerning
  applies only within one face, and lines keep the primary font's metrics. `Label3D` passes the current locale's faces
  from the project's `FontFallbackTable` (`LocaleFallbackFonts`), the table the game UI already uses.
- **No `Fog` toggle:** labels fog like every unshaded surface (a toggle needs a material flag and shader branch).

## Consequences

- 1 000 labels are up to 2 000 draws (text and outline per page) instead of one per page; acceptable for signs and tags,
  not for damage-number swarms. The batched renderer remains G11's open item and slots in behind the same node.
- `Label3D` text is tonemapped scene geometry (open question 2 unchanged).
- Render test `label3d-layout` (multi-line, autowrap, a Cyrillic fallback through Roboto Mono, alpha cut, two
  `FixedSize` tags equal on screen); the ADR 0200 `label3d` golden is unchanged.
