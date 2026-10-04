# ADR 0087 — Editor tooltips: one overlay layer driven by hovered/focused elements

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (lane m10c-icons)
- **Spec:** docs/design/editor.md#tooltips

## Decisions

1. **Declarative**: any element (or its nearest ancestor in the same document) with
   `data-tooltip="Title (Shortcut) — description"` has a tooltip; extra lines after `\n`. RmlUi has no tooltip element
   and the `title` attribute does nothing, so the convention is ours and is lint-tested (every icon-only control).
2. **One `TooltipOverlay` document on its own UI layer (90)** above the dialogs (50) and below the splash (100); its body
   has `pointer-events: none`, so it never takes input. It reads `HoverElement` of the dialog layer, then the panel layer
   (the UI server makes lower layers lose hover when a higher one takes the mouse), and `FocusElement` with
   `:focus-visible` for keyboard focus.
3. **0.5 s delay**, measured with the frame delta; placement one frame after the content is set (RmlUi lays it out in
   the UI update), below the element and clamped inside the window, above it when there is no room below.
4. **Dismissal**: capture-phase `mousedown`/`keydown`/`mousescroll` listeners on every `EditorDocument`, plus key/mouse
   events the UI did not take (the 3D view), hide it until the pointer moves to another element.
5. **Zero allocation when idle**: per frame two native handle reads and comparisons; the ancestor walk (alloc-free
   `HasAttribute`) runs only when the hovered element changes; the attribute string is read only when the tooltip shows.
