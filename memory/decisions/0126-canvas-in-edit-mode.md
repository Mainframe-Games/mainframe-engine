# ADR 0126 — Canvas pieces for the editor's 2D view

- **Date:** 2026-10-06
- **Status:** accepted (the editor switch itself is pending, see Consequences)
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM7 / E18 (the game's `docs/porting.md`)

## Context

The editor's 2D tabs are 3D sub-viewports: canvas items (sprites, Spine, custom draw, shaders) never show there. The
plan (E18, design b) renders 2D tabs as `Disable3D` sub-viewports through the canvas renderer, with the editor
camera as the canvas transform and the editor's lines (grid, markers, gizmo, shape outlines) on top.

## Decisions

1. **A 2D sub-viewport draws its `DebugLines` and `OverlayLines`** as unshaded line-list batches at the end of its
   canvas pass (through the stretch and root canvas transforms, z ignored), then clears them. The render server only
   consumes the lines of 3D views.
2. **Godot's edit-mode draw rule:** in `SceneTree.EditMode`, a canvas item runs its built-in drawing (Sprite2D, ...)
   but its `OnDraw` and `Draw` signal only when its type is `[Tool]` (Godot runs no script `_draw` in the editor).
3. **A current `Camera2D` does not write its viewport's canvas transform in edit mode** (the editor's view drives it,
   as in Godot's editor).
4. **Game libraries copy their NuGet dependencies** (`CopyLocalLockFileAssemblies` in `build/MainframeGame.props`): the
   editor's collectible load context resolves a game's own dependencies from its build folder (shared assemblies
   still come from the editor).

## Consequences

- With the editor switch tried (2D tabs `Disable3D`, camera → `CanvasTransform`, the canvas target published), the
  game's sprites, debris and wreck drew in the editor with the grid and gizmos on top. It was reverted: the engine runs
  lifecycle callbacks (`OnReady`, enter/exit) of every node in edit mode, so game code ran in the editor (the Crash Site
  Defense intro handed off to its main menu inside an edited tab). Godot runs no non-tool script callbacks in its
  editor. Adopting that rule for game-assembly types is an engine-wide decision to take first.
