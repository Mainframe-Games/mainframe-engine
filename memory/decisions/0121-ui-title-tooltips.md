# ADR 0121 — Title tooltips in RmlUi documents

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM5 / E16 (the game's `docs/porting.md`)

## Context

The game's Godot UI uses `tooltip_text` on tabs, skill nodes, recipes, bench slots, the forecast and the map legend.
RmlUi has no tooltips, and the managed binding cannot create elements (that would need new `mfrmlui` exports, whose
Windows and Linux binaries only CI builds).

## Decisions

1. **`title` is the tooltip text** (HTML's attribute; bindable with `data-attr-title`). The nearest titled ancestor of
   the hovered element wins, so a label inside a button shows the button's tooltip.
2. **The document provides the element:** `#tooltip`, styled by the document. The engine sets its text, position and
   `display`. A document without one shows no tooltips.
3. **Godot's behaviour and defaults:** 0.5 s rest delay restarted by motion, placed at mouse + (10, 10) px and shifted
   back inside the document, hidden on a press or when the hovered element changes. `UiServer.TooltipDelaySeconds`
   and `TooltipOffset` tune it.
4. **The hovered element comes from the context** (`RmlContext.HoverElement`), checked once per frame after hover
   events: RmlUi sends `mouseover` to every element entering hover, ancestors last.

## Consequences

- No native change. Each game document adds one `<div id="tooltip">`.
- Tooltips are mouse-only, as in Godot (no focus tooltips for the gamepad).
