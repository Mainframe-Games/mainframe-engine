# ADR 0081 — Editor UI: one RmlUi document per panel, C# layout, data binding for lists, generated RML for the inspector

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 E1–E2 (lane m10a)
- **Spec:** docs/design/editor.md#ui-rmlui

## Decisions

1. **One document per panel** (`Content/Editor/*.rml`: menubar, toolbar, scene_tree, filesystem, viewport, inspector,
   output, splitters; dialogs in a second layer), each body absolutely positioned from `EditorLayout` rectangles (dp).
   The layout is pure C# (unit-tested, persisted to `~/.mainframe/editor_layout.json` atomically); splitters are a
   full-window overlay document whose handles use RCSS `drag: drag`. This keeps panels independent (own data model,
   own hot reload) and is the shape docking needs later. RmlUi has no include mechanism, so one big flex document would
   have mixed every panel's markup and models.
2. **Lists are data-bound** (`scene_tree`, `output`, `viewport_tabs`, `file_picker`, `list_picker`): flat rows of
   one-level structs, the shape the M8 binding supports. Idle frames call no `Dirty`, so they cost nothing.
3. **The inspector generates RML** per selection: rows differ in shape (enum options and flag sets are nested lists,
   which the binding does not support). Values that change afterwards (undo, gizmo drag) are written into the existing
   elements in place; the RML is regenerated only when the selection or structure (array length, resource) changes,
   so a slider being dragged is never destroyed. Text fields commit on Enter or focus loss; sliders merge into one
   history entry per drag.
4. **Shared editor widgets stay in the editor project** (`theme.rcss`, `dialogs.rcss`) to keep this lane's engine-core
   changes minimal; promoting `tree-view`/`property-*` to `Content/UI/widgets` for games is a follow-up.
5. **`[CustomInspector]`/`ICustomInspector` live in the editor assembly** (header RML, row filter, `data-action`
   callbacks; found by scanning loaded assemblies that reference the editor). Game assemblies reference them when E4
   loads game projects; a separate editor-API assembly can split them out later.
6. **Native dialogs are unavailable through SDL2**: an RmlUi file picker (`FilePickerModel` + modal document) handles
   open/save/folder, with overwrite confirmation.

## Consequences

- Panels can be tested headless (null UI renderer): the editor test suite drives the real documents.
- A closed dialog must release keyboard focus (`HideAndReleaseFocus`), or the UI keeps every key and shortcuts stop.
