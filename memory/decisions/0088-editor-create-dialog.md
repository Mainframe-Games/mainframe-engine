# ADR 0088 — A Godot-style tree create dialog for nodes, resources and scenes

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (lane m10c-icons)
- **Spec:** docs/design/editor.md#create-dialog

## Decisions

1. **One `TreePickerDialog`** over a pure `PickerTree` model serves Add Node, the inspector's New resource button and
   Instance Scene; `PickerSources` builds the entries (node types, resource types assignable to the slot, the project's
   `.mscene` files under `Content/` as a folder tree). The flat `ListPickerDialog` stays for NodePath picking.
2. **Inheritance tree from the registry**: each type hangs under its nearest *listed* base (`SceneViewport` and
   `MissingNode` are left out, so SubViewport sits under Node); abstract types are shown but cannot be created (italic,
   skipped by Up/Down, an error on Create). Game types appear under their engine base with inherited icons.
3. **Fuzzy search keeps ancestors**: every match plus its ancestor chain is shown expanded; non-matching ancestors are
   dimmed. Ranking: exact > prefix > word-start substring > substring > subsequence (runs and word starts score), shorter
   names first; the best *creatable* match is selected, so Enter creates it.
4. **Favourites and Recent persist per dialog kind** (`node`, `resource`, `scene`) in the editor settings file
   (`EditorLayoutSettings.PickerFavorites/PickerRecent`, recent capped at 8, sanitized on load) — no new file.
5. **Description pane** = icon, name, ancestor chain with icons and the doc summary the generator recorded (ADR 0086).
6. **Instance Scene** without a project falls back to the file picker; with one, Browse… opens the file picker for files
   outside `Content/`. The edited scene's own file is not offered.
