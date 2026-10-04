# ADR 0085 — Editor icons: a rasterized Tabler atlas, curated list, committed outputs

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (lane m10c-icons)
- **Spec:** docs/design/editor.md#icons

## Context

The editor should prefer icons over text (Godot-style). RmlUi renders raster images, not SVG; the build must not depend
on Inkscape; Retina needs 2x glyphs; icons need several tints (families, levels, hover/disabled).

## Decisions

1. **Tabler Icons** (MIT, 5 000+ consistent 24-unit stroke icons, npm `@tabler/icons` 3.48.0). Outline by default,
   `name-filled` for Tabler's filled set. Only the icons the editor uses are vendored
   (`MainframeEngine.Editor/Icons/tabler/{outline,filled}`, licence copied), listed in
   `MainframeEngine.Editor/Content/icons/icons.txt` — the single source of what exists.
2. **One sheet SVG rendered four times** by Inkscape (`build/editor-icons/make_atlas.py`, `just editor-icons`): 1x 16 px,
   1.5x 24 px (`icon-lg`), 2x 32 px and 3x 48 px. Cells are 16 units on a 20-unit pitch, so linear filtering never
   samples a neighbour. `icon-sm` (14 dp) samples the 16/32 px sprite (a mild downscale). Glyphs are **white**; colour
   comes from RCSS `image-color`, so one atlas serves every tint.
3. **HiDPI by media query**: `icons.rcss` defines the 1x sprite sheets globally and the 2x ones (same sprite names) inside
   `@media (min-resolution: 1.5x)`. RmlUi merges media-block sprite sheets over the global ones (verified in
   `Spritesheet.cpp`), and the editor's panel layer sets the dp ratio from the display.
4. **Generated outputs are committed** (`icons-{16,24,32,48}.png` in Git LFS, `icons.rcss`); `just editor-icons-fetch`
   vendors new names from npm. A unit test fails when the RCSS, the list, the sheet sizes or the vendored SVGs disagree,
   and when any icon the sources reference is missing.
5. **Markup**: `<span class="icon icon-NAME"/>`; the per-icon classes are generated, the base/size/family classes live in
   `theme.rcss`. Every document links `/Content/icons/icons.rcss`. Modifier names (`sm`, `lg`, `3d`, `2d`, `ui`, `audio`,
   `physics`, `net`, `logic`, `resource`, `dir`, `missing`, `muted`, `accent`, `info`, `warn`, `error`, `debug`) can never
   be icon names (the generator refuses them).

## Alternatives rejected

- SVG rendering in the RmlUi shim (lunasvg plugin): a new native dependency and per-frame rasterization cost.
- An icon font: no colour control per glyph beyond text colour, and FreeType hinting at 14–16 px blurs strokes.
- Material Symbols / Phosphor / Lucide: Tabler has the widest set of editor-relevant glyphs (3D shapes, audio, physics,
  file types) in one consistent stroke style.
