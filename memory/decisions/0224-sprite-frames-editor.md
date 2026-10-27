# ADR 0224 — The SpriteFrames editor lives in the inspector; Select Frames is a dialog

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/animated-sprites.md#editor, docs/design/editor.md

## Context

The proposal (docs/design/future/2d-content.md, G2a.3) puts a SpriteFrames tab in a new tabbed bottom panel (Output +
contextual TileMap, TileSet and SpriteFrames tabs). The bottom panel is a single `OutputPanel` today; turning it into
a tab host is a layout change shared with G2b's TileMap palette and TileSet editor, and is not needed to edit
animations. The editor already has custom inspectors with header markup, actions, history and a workspace binding.

## Decision

- **The SpriteFrames editor is a custom inspector header** on both `SpriteFrames` (a `.mres` opened from the
  FileSystem panel or a nested slot) and `AnimatedSprite2D` (`SpriteFramesHeader`, shared): the animation list (add,
  duplicate, rename, delete, loop, autoplay, speed) and the frame strip (thumbnails cut from the sheet with RmlUi's
  `img rect`, play / play backwards, add from an image, add from a sheet, copy, paste, move, delete, duration). The
  bottom-panel tabs are left to G2b, which needs them for the palette.
- **Every SpriteFrames edit is one undo entry** recorded as before/after snapshots (`SpriteFramesSnapshot`: the
  animation list by reference and each animation's values), applied live and committed `alreadyApplied`. A `.mres`
  edited through a scene is marked so the scene's Save writes it (`EditedScene.MarkResourceFileEdited`).
- **On a sprite, picking an animation sets `AnimatedSprite2D.Animation`** (an undoable property change, as Godot's
  panel does). Play previews by stepping `Frame` from the editor's tick (`SpriteFramesPreview`): no undo entry, not
  dirty, the frame restored on stop.
- **Custom inspector headers may hold text fields** (`data-header-input="name"`): Enter or focus loss sends
  `name=text` to the inspector's `OnAction`.
- **Select Frames** (`SheetSlicerDialog`, `sheet_slicer.rml`) is a modal dialog with Godot's fields (counts or cell
  size as the dominant parameter, separation, offset), picks numbered in click order, Select All / Clear and "Add N
  Frames"; the last grid per sheet is remembered in `<project>/.mainframe/cache/sheet_grids.json` (not committed). The
  FileSystem panel's "New SpriteFrames from Sheet…" on an image writes `<image>.mres` with one animation per picked
  row.
- **2D picking** also hits sprites by their drawn rect (`Sprite2D`, `AnimatedSprite2D`), after shapes and origins.

## Consequences

- No layout work was needed; when G2b adds bottom-panel tabs, `SpriteFramesHeader.Build/Run` can move into a tab
  unchanged (it takes the resource, an optional sprite and an `IInspectorContext`).
- Not built yet: drag-reordering frames, frame zoom, auto-slice by opaque bounds.
