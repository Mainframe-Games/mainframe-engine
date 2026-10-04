# ADR 0080 — Editor host: code-driven `EditorApp`, edit mode in the engine's tree, one SubViewport world per tab

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 E1–E3 (lane m10a)
- **Spec:** docs/design/editor.md

## Context

editor.md left open whether the editor is itself a `GameHost` project (scene-driven UI) or a code-driven `EditorApp`,
and how edited scenes are kept from running game code. The plan's default: code-driven, no simulate mode, single window.

## Decisions

1. **`EditorApp : Engine`, code-driven.** The UI is built in C# by an `EditorWorkspace` node (RML documents for
   markup); no editor scene file. `GameHost` (lane m10b) stays the game-side host.
2. **Edit mode is a `SceneTree` flag.** `SceneTree.EditMode` skips `OnProcess`/`OnPhysicsProcess`/`OnInput`/
   `OnUnhandledInput` of every node whose type is not `[Tool]` (`Node.IsTool`, cached per type, not inherited — Godot's
   per-script semantics), and does not step fixed-step servers. Lifecycle, deferred calls, frees, transform sync and
   frame servers (UI, physics debug draw) run. The editor's own per-frame nodes (`EditorWorkspace`,
   `ViewportController`) are `[Tool]`; `UiLayer`/`UiDocument` have no process callbacks, so they need nothing.
3. **One edited world per tab = one `SubViewport` per scene** under the workspace, the scene root its child. Only the
   active tab's viewport renders (`UpdateMode`). Its LDR target is published to RmlUi as `engine://editor-viewport`
   (new `SubViewport.ColorTarget`). Shadowed since integration with M4: the view sets `SubViewport.Shadows`, which
   hands it the shared shadow maps because the editor's main world draws nothing.
4. **Editor camera via `SceneViewport.CameraOverride`** (an `ICamera`; a `PerspectiveCamera` gets the view's aspect),
   so the scene's `Camera3D`s and their `Current` flags are never touched (no false dirty state, nothing to restore).
5. **Gizmos and handles draw in `SceneViewport.OverlayLines`**: the debug-line renderer's second pipeline without depth
   test, drawn after the depth-tested `DebugLines`.
6. **Audio is off in the editor process** (`AudioOptions.Enabled = false`): edit mode is silent until previews exist.

## Consequences

- Game nodes in edited scenes never run; `[Tool]` nodes do (the documented contract for game authors).
- Editor nodes are not registered (the editor project has no generator analyzer), so they never appear in Add Node.
- Shadows are missing in the editor view until sub-viewports get shadow maps.
