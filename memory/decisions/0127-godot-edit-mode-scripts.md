# ADR 0127 — Godot's editor rule for game scripts

- **Date:** 2026-10-06
- **Status:** accepted (Brogan, during the Crash Site Defense port)
- **Supersedes:** part of ADR 0080 (lifecycle callbacks ran for every node in edit mode)

## Context

ADR 0126 made 2D editor tabs render through the canvas, which exposed game code running in the editor: lifecycle
callbacks ran for every node in edit mode, so Crash Site Defense's intro handed off to its main menu inside an edited
tab. Godot runs no callbacks of non-tool scripts in its editor; native nodes keep their own behaviour.

## Decisions

1. **`SceneTree.EditModeScripts`** (a type predicate, null by default) names the script types. The editor sets it to
   "loaded in a collectible context", i.e. the game's code from `ProjectService`.
2. In edit mode a non-`[Tool]` script type's `OnEnterTree`, `OnReady` and `OnExitTree` do not run; the implementation
   of its nearest non-script base type runs instead, called non-virtually through a cached function pointer (a game
   `Sprite2D` subclass still queues its draw and registers with its servers). Its `OnDraw` and `Draw` signal are skipped
   (ADR 0126's draw rule now uses the same predicate).
3. Process and input callbacks keep the existing rule (`[Tool]` only, ADR 0080).

## Consequences

- With the predicate set, the editor's 2D tabs switch to canvas rendering (ADR 0126): the game's sprites, debris and
  wreck show in the editor and nothing of its runtime logic runs.
- Visuals that game code builds at run time (Building's sprite, the player's posed Spine rig) do not appear in the
  editor, as in Godot. Signal connections to script methods still fire if raised.
