# Editor

## Purpose

The Mainframe Editor (`MainframeEngine.Editor`, milestone M10) edits `.mscene` files the way Godot does: a scene tree,
an inspector driven by the generated node metadata, a 3D viewport with picking and gizmos, undo/redo, all in a UI built
with the engine's own game UI stack (RmlUi). This page describes what ships (phases **E1 shell**, **E2 scene tree +
inspector + undo**, **E3 viewport**). Projects, game-assembly loading, the file system browser and out-of-process play
(E4) and the polish phase (E5) are still proposals: [Future: editor](future/editor.md).

![The editor with the Sandbox scene](../images/editor.png)

```sh
just editor MainframeEngine.Sandbox/Content/Scenes/Sandbox.mscene   # or: dotnet run --project MainframeEngine.Editor -- <scene>
```

Decisions: [0080 code-driven editor, edit mode, a SubViewport per tab](../../memory/decisions/0080-editor-code-driven-edit-mode.md) ·
[0081 one document per panel, data binding, generated inspector](../../memory/decisions/0081-editor-ui-documents-and-binding.md) ·
[0082 undo/redo](../../memory/decisions/0082-editor-undo-redo.md) ·
[0083 files before projects, crash safety, close interception](../../memory/decisions/0083-editor-files-projects-and-safety.md) ·
[0084 brand and window icons](../../memory/decisions/0084-brand-and-window-icons.md).

## Structure

```mermaid
flowchart TB
    App["EditorApp : Engine<br/>Tree.EditMode = true · IEditorHost"] --> WS["EditorWorkspace [Tool]"]
    WS --> S["EditorSession<br/>open scenes, active tab, project folder"]
    S --> ES["EditedScene (per tab)<br/>root · file · UndoRedo · Selection · EditorCamera"]
    ES --> SV["SubViewport (own World3D)<br/>scene root + EditorGrid"]
    WS --> VC["ViewportController [Tool]<br/>camera, picking, gizmo, icons"]
    WS --> PL["UiLayer 0: panels<br/>menubar · toolbar · scene_tree · filesystem · viewport · inspector · output · splitters"]
    WS --> DL["UiLayer 50: dialogs<br/>popup menu · file picker · list picker · message box"]
    WS --> SP["UiLayer 100: splash"]
    WS --> C["EditorCommands (ids)<br/>menus · buttons · shortcuts · QA scripts"]
    VC -- "CameraOverride, OverlayLines" --> SV
    SV -- "ColorTarget → engine://editor-viewport" --> PL
```

| Type | File | Role |
|---|---|---|
| `EditorApp` | [EditorApp.cs](../../MainframeEngine.Editor/Src/EditorApp.cs) | `Engine` host: edit mode, window title/size, modifier keys, SDL close-request filter, automation hooks |
| `EditorWorkspace` | [EditorWorkspace.cs](../../MainframeEngine.Editor/Src/EditorWorkspace.cs) | Composition root node: builds the panels, dialogs, splash and viewport controller; layout sync; shortcuts; quit prompt; recovery copies |
| `EditorCommands` | [EditorCommands.cs](../../MainframeEngine.Editor/Src/EditorCommands.cs) | Every action by id (`file.save`, `edit.undo`, `gizmo.rotate`, …); failures are reported, never thrown |
| `EditorSession`, `EditedScene` | [Session/](../../MainframeEngine.Editor/Src/Session/) | Tabs, open/save/close, project folder; the per-scene edit operations |
| `UndoRedo`, actions | [Undo/](../../MainframeEngine.Editor/Src/Undo/) | History, merging, dirty tracking; set property, add, remove, reparent, rename, move, composite |
| `InspectorModel`, `InspectorProperty` | [Inspector/](../../MainframeEngine.Editor/Src/Inspector/) | Rows from `NodeTypeInfo`; editor kinds, parsing/formatting; `[CustomInspector]` |
| `SceneTreeModel` | [SceneTree/](../../MainframeEngine.Editor/Src/SceneTree/) | Flattened rows with expand state |
| `EditorCamera`, `TransformGizmo`, `ViewportController` | [Viewport/](../../MainframeEngine.Editor/Src/Viewport/) | Camera math, gizmo math, the viewport's per-frame work |
| `EditorLayout`, `FilePickerModel`, `OutputLog`, `AtomicFile` | [Layout/](../../MainframeEngine.Editor/Src/Layout/), [Files/](../../MainframeEngine.Editor/Src/Files/), [Output/](../../MainframeEngine.Editor/Src/Output/) | Pure models (unit-tested) |
| Panels and dialogs | [UI/](../../MainframeEngine.Editor/Src/UI/) | One `EditorDocument` (a `UiDocument`) per RML file in [Content/Editor](../../MainframeEngine.Editor/Content/Editor/) |

## Edit mode and edited worlds

- The editor's scene tree runs with **`SceneTree.EditMode`**: only nodes whose type is marked `[Tool]` get
  `OnProcess`/`OnPhysicsProcess`/`OnInput`/`OnUnhandledInput`, and physics does not step (bodies stay where they were
  authored; collision shapes are still drawn). Enter/ready/exit callbacks run, so visuals, lights and cameras register
  with their worlds as usual. The editor's own `EditorWorkspace` and `ViewportController` are `[Tool]` nodes.
- **One world per tab**: each open scene lives under its own `SubViewport` (its own `World3D`: lights, sky, physics
  space). Only the active tab renders. Its tonemapped colour target (`SubViewport.ColorTarget`) is published to the UI as
  `engine://editor-viewport` and shown by an `<img>` in the viewport panel; the view's pixel size follows the panel.
- **The editor camera** is `SceneViewport.CameraOverride` — the scene's own `Camera3D`s and their `Current` flags are
  never touched.
- Audio is disabled in the editor process. The edited scene is **shadowed**: its view sets `SubViewport.Shadows`, and
  since the editor's main world draws nothing, the shared shadow maps (Shadows v2: cascades, atlas, PCF) go to the
  active tab's view.
- **Projects (interim, until the E4 project UI):** opening (or first saving) a scene makes its project current: the
  nearest ancestor folder holding a `project.mfproj` (its `ProjectSettings` load into `EditorSession.Project`; an
  unreadable file is reported and ignored), else — a folder without one — the folder above the scene's `Content/`.
  `AssetDatabase.Current` and `ContentPaths.ProjectDirectory` point there, so the scene's textures, models, sky and Spine
  folders load from the project sources. Node types are the engine's (game types load as `MissingNode`, kept intact and
  written back — the inspector says so). Game assemblies arrive with E4.

## UI

| Panel | Document | What it does |
|---|---|---|
| Menu bar | `menubar.rml` | File (New, Open, Save, Save As, Close, Quit), Edit (Undo/Redo with the action names, Undo History, Add Node, Instance Scene, Rename, Duplicate, Delete), View (frame, axis views, reset, grid), Help (shortcuts, about); the scene name and an unsaved marker on the right |
| Toolbar | `toolbar.rml` | Select/Move/Rotate/Scale (Q/W/E/R), Global/Local (T), Snap (Y) + move step, Frame (F), Grid (G); Play/Pause/Stop disabled until E4; fps / frame-time readout |
| Scene tree | `scene_tree.rml` | The hierarchy (instanced sub-scenes are one row tagged "scene"; their insides are not listed), type icons by category, expand/collapse, click / Cmd+click / Shift+click, drag onto a row's middle to reparent or onto its top/bottom edge to reorder (global transform kept), double-click/F2 rename, right-click menu (add child, instance scene, rename, duplicate, move up/down, delete) |
| Viewport | `viewport.rml` | Scene tabs (title with `*` when dirty, close, +), the 3D view, the view's mode in the corner, an empty-state hint |
| Inspector | `inspector.rml` | Type, editable name, custom-inspector header, collapsible sections per declaring type / `[ExportGroup]`, one row per `[Export]` (below) |
| Output | `output.rml` | Engine `Log` messages (`OutputLog` is an `ILogSink`; the category shows as `[Category]`) (time, level colour), Debug/Info/Warnings/Errors filters with counts (persisted), Clear; follows the newest line |
| File system | `filesystem.rml` | Placeholder until E4 (shows the project folder) |
| Splitters | `splitters.rml` | Four drag handles (left dock, right dock, output, scene tree / file system) |
| Dialogs | `file_picker.rml`, `list_picker.rml`, `message.rml`, `popup_menu.rml` | Modal file picker (open/save/folder, filters, Home/Project/Content places, overwrite confirmation), searchable list (Add Node, node paths, resource types), message box / prompt, popup menus |
| Splash | `splash.rml` | Logo, "Mainframe Engine", "Editor vX.Y.Z", status line, progress bar on the brand navy; shown while the editor starts and while a scene loads, then fades. It never outlasts the loading, except a 1 s minimum on the very first launch |

- **Theme**: `theme.rcss` + `dialogs.rcss`, a dark palette (base `#15171c`, panels `#1f2128`, accent `#3b82f6`) over
  the shared widget library's controls; all sizes in dp (the panel layer uses `UiScaleMode.Dpi`).
- **Layout**: `EditorLayout` computes every panel rectangle from the window size and the persisted sizes (left/right
  dock widths, output height, scene tree share), clamped to minimums; each document body is positioned in dp. Sizes,
  the window size and the output filter persist in **`~/.mainframe/editor_layout.json`** (user profile on every OS,
  written atomically; unreadable, newer or nonsensical files fall back to defaults).
- **Lists are data-bound** (no work on idle frames); the inspector is generated RML updated in place (see below).
- **ImGui** remains the F12 developer overlay; F8 opens the RmlUi debugger.

### Keyboard shortcuts

Cmd (macOS) or Ctrl: N new · O open · S save · Shift+S save as · W close tab · Q quit · Z undo · Shift+Z / Y redo ·
D duplicate · A add node · Shift+A instance scene · Up/Down move in tree. Plain keys: Delete/Backspace delete, F2
rename, F frame, G grid, Q/W/E/R tool, T local/global, Y snap, 1/3/7 front/right/top. Shortcuts are unhandled input:
a focused text field keeps its keys, and closed dialogs release focus.

## Inspector

`InspectorModel.Build(target)` walks the generated `NodeTypeInfo.Properties` (base types first) of a node or resource.
Rows are grouped into sections by `[ExportGroup]`, else by declaring type ("Node", "Node3D", "Light3D"…). The widget
comes from the value type and the `[Export]` hints:

| Kind | From | Widget |
|---|---|---|
| Bool | `bool` | checkbox |
| Integer / float | integer and floating types | text field (invariant culture) |
| Range | `Range = "min,max[,step]"` | slider + field; values clamp |
| Text, multiline | `string`, `Multiline` | field, text area |
| File / directory path | `File = "*.png,…"`, `Directory` | field + browse (stored project-relative) |
| Enum, flags | enums; `[Flags]` or `Flags = true` | dropdown; a checkbox per single-bit flag |
| Vector2/3/4 | `Vector2/3/4` | one field per axis (coloured X/Y/Z/W) |
| Quaternion | `Quaternion` | Euler degrees (X→Y→Z, like `RotationDegrees`) |
| Color | `System.Drawing.Color`, or a `Vector3`/`Vector4` named `…Color` (lights) | swatch + hex field; the swatch opens RGBA sliders |
| NodePath | `NodePath` (`NodeType` hint filters) | field + pick from the scene's nodes (stored relative to the node) |
| Resource | `Resource` subclasses | label + Load (file), New (inline, a type assignable to the slot), clear; inline resources expand into nested rows (up to 3 levels) |
| Array | `T[]`, `List<T>` | count, + Add, per-element fields (scalars) and remove |
| Transform, unsupported | `Transform3D/2D`, others | read-only text |

- Edits go through the scene's undo history: text fields commit on Enter or focus loss (a field being edited commits
  to its own node before the inspector switches to another), checkboxes/dropdowns/buttons at once, slider drags as
  **one** merged entry. Invalid text is rejected and the field restored.
- After any change (undo, redo, gizmo drag — live while dragging) only values that changed are written into the
  existing elements; the RML is regenerated when the selection or a shape (array length, resource) changes.
- **`[CustomInspector(typeof(T))]`** classes implementing `ICustomInspector` (public parameterless constructor) can add
  header RML (elements with `data-action` call back), hide generated rows and act through the scene's history. The
  editor's own `MissingNodeInspector` explains missing types. They are found in loaded assemblies that reference the
  editor; game assemblies join with E4.

## Undo / redo

Each tab has an `UndoRedo` of `IEditorAction`s (`Do`, `Undo`, `TryMerge`): `SetPropertyAction`, `AddNodeAction` (new
nodes, duplicates, instanced scenes), `RemoveNodeAction`, `ReparentAction`, `RenameAction`, `MoveInTreeAction`,
`CompositeAction`.

- Committing after undo cuts the redo branch; beyond 256 entries the oldest are dropped. Actions holding detached nodes
  free them when they leave the history (`IDiscardableAction`).
- Continuous edits merge by key until released (`EndMerge` on mouse up / commit / undo / save). Gizmo drags apply live
  and commit once on release.
- Delete keeps the subtree detached (owners, signal connections and index restored on undo); reparent restores the exact
  local transform on undo; duplicate packs the scene and re-instantiates the copies (unique names Box → Box2, nested
  instances stay instances, connections inside the copy belong to the edited scene).
- **Dirty state**: the history position against the saved one. Tab and window titles show `*`; closing a dirty tab, or
  the window (close button, Cmd+Q — intercepted with an SDL event filter), asks Save / Don't Save / Cancel.
- Edit › Undo History lists every entry; choosing one undoes or redoes up to it.

## Viewport

| Input (over the view) | Action |
|---|---|
| Left click | select (Cmd/Shift: toggle): icons of lights/cameras/audio first, else the GPU object-ID pick (two frames later). A picked node inside an instanced sub-scene selects the instance |
| Left drag on a gizmo handle | move / rotate / scale along that axis (centre: view plane / uniform), snapped when Snap is on (move step, 15°, 0.1) |
| Alt + left drag, middle drag | orbit around the pivot |
| Shift + middle drag | pan |
| Wheel | zoom (while flying: fly speed) |
| Hold right button | fly: mouse look + W/A/S/D/Q/E (Shift faster); the view says "fly" |
| F / 1 / 3 / 7 | frame the selection / front / right / top |

- **Gizmos** (`TransformGizmo`): constant on-screen size (points × pixel scale), hit-tested by screen distance to the
  projected axes or rings, dragged by intersecting the mouse ray with the axis line (or the view plane), rotation from the
  accumulated screen angle around the pivot; local or global axes (scale is always local). Drawn into the view's
  `OverlayLines` (no depth test) with highlighted hover/active handles.
- **Editor-only visuals** (never saved): `Grid3D`, orange selection boxes (mesh bounds) or crosses, sun/omni/spot icons
  with direction, cone and (selected) range, camera frusta, audio markers and range spheres, collision shapes
  (physics debug draw).

## Files and safety

- Open/Save/Save As go through the RmlUi file picker; scenes are written by `SceneSaver` (temp file + rename) and keep
  their UID across saves. A failed save keeps the old file and the scene dirty and shows the error.
- Every command runs guarded: an exception becomes an Output error and a message box, never a crash.
- An unhandled exception writes recovery copies of unsaved scenes to `~/.mainframe/recovery/`.

## Brand

The window icon, macOS bundle icon, Windows `ApplicationIcon` and the splash use the "C3 Circuit" logo
([docs/images/brand](../images/brand/README.md)): `logo-macos-512.png` on macOS (Apple's icon grid; it is the Dock
icon), `logo-48.png` elsewhere. Window icon pixels are reordered for Silk's SDL surface masks (`WindowIcon`).

## Engine changes made for the editor

| Change | Where |
|---|---|
| `SceneTree.EditMode`, `Node.IsTool` | [Scene graph & nodes](scene-graph-and-nodes.md) |
| `SceneViewport.CameraOverride`, `SceneViewport.OverlayLines`, `SubViewport.ColorTarget` | [Scene graph & nodes](scene-graph-and-nodes.md), [Materials & meshes](materials-and-meshes.md#offscreen-views-subviewport) |
| `SubViewport.Shadows` (a view of another world may own the shadow maps when the main world has nothing to shadow) | `Scene/SubViewport.cs`, `Servers/RenderServer.cs` |
| `ContentPaths.ProjectDirectory` | `Core/ContentPaths.cs` |
| List bindings tolerate rows past the end of a list that just shrank | [Game UI](game-ui.md#data-binding) |
| `WindowIcon` byte order for window icons | `Core/WindowIcon.cs` |

## Testing and QA

- **Unit tests** ([Tests/MainframeEngine.Editor.Tests](../../Tests/MainframeEngine.Editor.Tests/), `just test`, CI on all
  three OSes): undo/redo (do/undo/redo/merge/limits/dirty), the inspector model for every type and hint, scene
  operations (delete/undo restores subtree + owners + connections, reparent keeps the global transform, duplicate with
  unique names, instances and connections, atomic save + reload), layout and its persistence, the file picker, camera
  and gizmo math, the output log, and the **whole UI headless** (RmlUi with the null renderer): panels load without
  RmlUi warnings, tree clicks and drags, inspector edits for every kind, Add Node, menus, shortcuts, rename, Save As,
  the quit prompt, recovery copies, and **0 B per idle frame with 1 000 nodes**.
- **Render tests** ([EditorRenderTests](../../Tests/MainframeEngine.RenderTests/EditorRenderTests.cs)): the editor's
  `--smoke` run in a hidden window — open Sandbox.mscene, select the Column by GPU picking at its projected pixel, change
  its position through the inspector model, undo/redo, save to a temp file, reload and re-save byte-identically — plus
  the editor window golden, frame times on the scene, the allocation gate (0 B over 300 idle frames with 1 000 nodes,
  validation on) and the splash golden.
- **Scripted QA** (`just qa-editor`, [Tests/QA/editor-walkthrough.qa](../../Tests/QA/editor-walkthrough.qa)): the real
  editor driven through the UI input path (`--qa-script`: clicks by point or `#element-id`, drags, gizmo drags, keys,
  text, commands, dialog answers, `window-close`) with captures in `artifacts/qa-editor`.

## Performance

Apple M5, MoltenVK, hidden 1280×720-point window at content scale 2 (2560×1440 px), Sandbox.mscene open (sky, five
lights with Shadows v2 cascades and atlas, Spine, physics crates, a glTF model), Debug build with validation: **8.3 ms
average, 9.0 ms p95** per frame (the 120 Hz display rate). Idle frames allocate nothing.

## Known issues

- Game node types load as `MissingNode` until E4 loads game assemblies (the Sandbox's `FlyCamera`, `SpinningBox`).
- Single selection for the gizmo and the inspector (the last selected node); multi-object editing is E5.
- No orthographic camera, box selection or 2D editing yet (E5).
- Lines are one framebuffer pixel wide; gizmo handles are drawn as several parallel lines.

## Related docs

[Future: editor (E4/E5)](future/editor.md) · [Game UI](game-ui.md) · [Scene graph & nodes](scene-graph-and-nodes.md) ·
[Scene serialization](scene-serialization.md) · [Materials & meshes](materials-and-meshes.md) · [Release](release.md) ·
[Testing](testing.md)
