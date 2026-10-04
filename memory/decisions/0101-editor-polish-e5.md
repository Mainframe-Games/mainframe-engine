# ADR 0101 — Editor polish (E5): multi-select, signals, 2D mode, resource files, editor settings

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (E5; lane m10c-projects)

## Decisions

- **Multi-select editing:** the inspector shows the properties *every* selected node has (the same exported member —
  e.g. `Node3D.Position` on a light and a mesh); differing fields show "—" (empty with a placeholder); a component edit
  keeps each node's other components; an edit is one `CompositeAction` covering every node (unchanged ones included,
  so drags keep merging). Arrays, nested resource sub-inspectors, custom inspectors and signals stay single-node.
- **Signals tab:** lists the node's `[Signal]`s with the persisted connections *its scene owns* (no `OriginScene`, or
  the edited root — connections of instanced sub-scene files are not shown or saved here). A connect dialog lists the
  scene's nodes and the target's compatible methods (the `Node.Connect` rule) with deferred/one-shot flags;
  connect/disconnect are undoable actions. Connections are real delegates in the editor too (as before for loaded
  scenes).
- **2D editing:** scenes whose root is a `Node2D` open in an orthographic view of the z = 0 plane (pixels, y up;
  View › 2D/3D View switches any tab). `EditorCamera` gains the 2D state and projection helpers; `TransformGizmo2D`
  (arrows + free square, ring, scale handles) shares mode/space/snap with the 3D gizmo; moves snap to whole pixels
  (or the grid step); picking is CPU (rectangle/circle shapes, then node origins). No sprite rendering exists yet, so
  2D content is shapes, markers and Camera2D frames.
- **Resource files** open in the inspector with their own undo history and Save (`EditedResource`); custom inspectors
  act through `IInspectorContext` (scene or resource), with the old `EditedScene` overload kept. `AudioBusLayoutInspector`
  is the shipped example (a mixer strip editing the bus list).
- **Editor settings** (`~/.mainframe/editor_settings.json`): accent colour applied live by writing recoloured
  `theme.rcss`/`dialogs.rcss` into an overlay content folder checked before the editor's own and reloading style
  sheets; autosave interval (scenes with a file only); the external code editor command (`{file}`, `{line}`,
  `{column}`, `{project}`; presets; default: `MAINFRAME_CODE_EDITOR`, else VS Code when found, else the OS default);
  automatic code reload. The inspector's name column auto-sizes and has a persisted drag width.

## Consequences

- The accent recolours the shared sheets; inline document styles keep the default blue in a few places.
- `UnregisterTexture` now releases RmlUi's texture cache, so a name re-registered later is reloaded (the viewport went
  black after switching tabs).
