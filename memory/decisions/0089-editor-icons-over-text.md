# ADR 0089 — Icons over text in the editor UI

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (lane m10c-icons)
- **Spec:** docs/design/editor.md#icons (user directive 2026-10-05: "Icons should always be preferred over text for UI in the editor")

## Decisions

1. **Rule**: a control shows an icon instead of a label wherever the icon is unambiguous; every icon-only control has a
   `data-tooltip` naming it, its shortcut and what it does (`Move (W) — translate selected nodes`). Text stays for menu
   item labels (with a leading icon), dialog primary actions, inputs and values. New panels (the parallel E4 lane:
   FileSystem, play controls, project dialogs, signals) follow the same markup contract and `EditorIcons` helpers.
2. **Applied everywhere that exists**: menu bar (C3 logo mark instead of "MAINFRAME", item icons + shortcuts), toolbar,
   scene tree (type icons replace the coloured dots; the type name moves to the tooltip; warning/instance/script badges;
   visibility eye), inspector (header, section and per-property icons, icon row actions), Output (level/category icons,
   icon toolbar), tabs, panel headers, dialogs (title icons, file-type icons), undo history (action icons). The splash
   keeps its logo and wordmark (branding, not controls).
3. **Scene tree rows drop the type text**: the icon and its tint carry the type; the full "Name — Type" is the row icon's
   tooltip (Godot shows no type text either).
4. **Enforced by tests**: a headless lint walks every panel, dialog and menu after exercising them and fails on an
   icon-only control without a tooltip or an icon element that does not name exactly one atlas icon; menus must have an
   icon per item.
5. **QA scripts**: `#element-id` arguments are no longer taken as comments (`move #tool-translate` captures a tooltip),
   and scripted modifier keys count even when the host reads the physical keyboard (`TrackKeyModifiers`; before, every
   Cmd+key step of `editor-walkthrough.qa` silently did nothing in the real window).
