# ADR 0213 — Viewport tools: multi-node gizmo, resource actions, range handles and the handle API

- **Date:** 2026-10-27
- **Status:** accepted
- **Milestone:** G7.1–G7.3 (editor viewport tools)
- **Docs:** docs/design/editor.md (Viewport tools: Multi-node gizmo, Resource actions, Range handles and the handle API),
  docs/design/scene-serialization.md (`Handle` hint)

## Context

The M10 editor's gizmo moved the last selected node only (a known issue), inline resources could be saved to a `.mres`
(ADR 0169's minimal path) but not made unique, opened or shown, and light and audio ranges could only be typed. Plugins
and game nodes had no way to declare viewport handles; the G9 joints work needs one for limit handles.

## Decision

- **Multi-node gizmo.** `TransformGizmo` stays a single-pose tool. The controller feeds it the pivot's pose (the active
  node's rotation for the axes, scale one) and the pure `MultiGizmo.Apply` maps the dragged pose to each target: the
  pivot's (snapped) delta for translate — along each node's own axis in Local space, as Godot — and rotate/scale about
  the pivot (or each origin). Pivot modes Median (default), Active, Individual Origins (Blender's pivot point; Godot has
  median only) on a toolbar button that cycles; session-lasting like mode, space and snap. Targets drop nested selected
  nodes, hidden and locked ones, and are cached by scene and `Selection.Version`. One `CompositeAction` per drag.
- **Cancel.** Esc or a right click during a gizmo, handle or box drag puts everything back (new for the gizmo too).
- **Resource actions.** The row's Save as .mres button moves into a **More** popup (Save as .mres, Make Unique, Make Unique
  (Deep), Open, Show in FileSystem) whose disabled items explain themselves (`MenuItem.Tooltip`). Make Unique duplicates
  into one slot (`EditedScene.MakeUnique`, one undo step); imported assets cannot be made unique (their data is in the
  source file). The label shows a `link` icon for files and "inline ×N" for shared inline resources.
- **Handle API** in the editor (`EditorHandle`, `HandleDrag`, `IHandleProvider`, `[HandleProvider(typeof(T))]`), the
  handle half of Godot's `EditorNode3DGizmoPlugin`; commit and cancel are the editor's. `HandleDrag` carries the node
  origin, the handle's start and axis, `Along` (relative to the grab: no jump), the dragged point, the mouse ray (for
  angle handles that intersect their own planes), snap steps for distances and angles, the camera and `Is2D`. An optional
  `Draw` default method draws what the handles edit. Providers are found in every assembly referencing the editor —
  the same scan as custom inspectors, now shared (`EditorPluginTypes`) — reset on code reload; nearest base type wins.
- **Hints for game nodes.** `ExportAttribute.Handle` (`HandleHint.Radius|Length`, recorded by the generator in
  `ExportHints.Handle`) gives game floats stock handles through `HintHandleProvider`; explicit providers win.
  `ExportPropertyInfo.TryGetFloat`/`TrySetFloat` read and write floats without boxing, so idle frames with hinted
  handles allocate nothing (the doc expected one boxed float per move; this is better).
- Built-ins: omni range, spot range and outer/inner angles (outer pushes inner; inner ≤ outer), audio 3D unit size and
  max distance (hollow while unlimited; unit size ≤ max distance), audio 2D max distance, point light 2D texture scale.

## Consequences

- G9 joint limits add `IHandleProvider`s with angle handles using `HandleDrag.RayOrigin/RayDirection`; no editor change.
- The spot's rim handles face the camera's right and up, so they move as the view orbits (they stay visible).
- The editor golden (toolbar) changed; lavapipe goldens need recording in CI.
