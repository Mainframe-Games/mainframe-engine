# ADR 0118 — Canvas text with a managed TrueType rasteriser

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM4 / E4 (the game's `docs/porting.md`)

## Context

The game draws world-space `Label`s (gather and building prompts) in Exo 2 with an outline; they sit on the world
canvas, so the night tint and lights apply to them. The engine's only text was RmlUi's (FreeType inside `mfrmlui`,
not reachable from the canvas). Exposing FreeType would mean a new or changed native library for three platforms,
whose Windows and Linux binaries only CI builds.

## Decisions

1. **Managed TrueType, no new dependency:** `TrueTypeFont` reads `glyf` fonts (cmap 4/12, hmtx, simple + composite
   outlines, GPOS `kern` PairPos 1/2 incl. extension lookups, legacy `kern`); `GlyphRasterizer` fills them non-zero
   with exact signed-area coverage (font-rs's algorithm). CFF fonts are rejected with a message.
2. **FreeType-like metrics:** ascent/descent = `hhea` scaled and rounded; height = ascent + descent; unhinted greyscale
   glyphs at whole-pixel pen positions; advances and kerning in float.
3. **Outlines like Godot's:** the glyph grown by a disc of `outline_size / 4` px (Godot's `FT_Stroker` radius),
   rasterised at 4× and box-filtered; text is drawn over it.
4. **`Font : Resource`** (`.ttf` through `FontImporter`), glyphs cached per (size, outline) in 512² RGBA atlas pages
   (white, alpha = coverage) updated with `SetPixels`; `CanvasItem.DrawString` / `DrawStringOutline` with Godot's
   parameters (baseline position, alignment in a width, size, modulate). `HorizontalAlignment` is Godot's enum.

## Consequences

- Glyph shapes differ slightly from FreeType's (no light hinting, no subpixel positioning, no oversampling under a
  scaled canvas), within the port's "UI looks similar" bar.
- No shaping beyond pair kerning (ligatures, marks, RTL), no font fallback, single-line only.
