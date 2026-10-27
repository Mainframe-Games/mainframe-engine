# ADR 0214 — Box selection, the viewport lock and Simulate

- **Date:** 2026-10-27
- **Status:** accepted
- **Milestone:** G7.4–G7.5 (editor viewport tools)
- **Docs:** docs/design/editor.md (Box selection and lock, Simulate), docs/design/scene-graph-and-nodes.md (Simulation in
  edit mode), docs/design/physics.md, docs/design/materials-and-meshes.md (region picks)

## Context

The editor had no box selection, no node lock and no way to try physics without Play (save, build, launch a process).
`SceneTree.EditMode` runs only `[Tool]` callbacks and never steps physics.

## Decision

- **Box selection selects what is visible** (Blender with X-ray off): a region request on the object-ID target
  (`SubViewport.RequestPickRegion`, one transient readback buffer per box, one region per frame slot, read once the
  fence signals) plus icon nodes by origin; 2D encloses bounds on the CPU (Godot's rule). Select-through (Godot's
  frustum test) stays an open question — default: not offered. The scene root is never box-selected (selecting it with
  its children would move everything twice). Shift adds, Ctrl/Cmd toggles; tree order, so the active node is the last.
- **The lock is the reserved persistent group `_editor_locked`** (Godot's `_edit_lock_` is node metadata; groups are
  already saved, survive instancing and are shared through version control — the open question's default). Toggling
  is an undoable `SetGroupAction`. Picks, boxes, the gizmo and handles skip locked nodes; the tree and inspector do not.
- **Simulate runs a copy**, not the edited world: `PackedScene.Pack` → a twin `SubViewport` with
  `Simulation = Running` → Stop frees it. Simulating in place was rejected: game `OnReady` never ran on edited nodes,
  side effects would need reverting, and restoring by re-instantiating breaks undo history, selection and the inspector.
- **Engine:** `ViewportSimulation` resolved per node on enter tree (`ResolvedSimulation`; `RunsInEditMode` = tool or
  simulated); input never reaches a simulation; a paused one pauses its subtree; the fixed-step servers step in edit
  mode while one runs and the physics servers step and interpolate only its spaces; exceptions of simulated callbacks
  (and timers, tweens, deferred calls, physics signals while one exists) go to `SceneTree.SimulationFault`, logged with
  the throwing frame's file and line; `MultiplayerApi` refuses Host/Connect. Games are unaffected.
- **Editor:** the scene's history is locked (`UndoRedo.IsLocked`) — edits wait, structural ones are refused before they
  build anything; the inspector shows the counterparts live (10 Hz) and read-only; Keep Transforms copies selected
  positions and rotations back as one undo step; a fault, tab switch or code reload stops it; dirty scenes with game code
  get recovery copies first.
- **Open-question defaults:** simulation audio stays muted (edit mode is silent); changed shared resources are only
  listed on Stop (no reload in place); **step one physics frame while paused is not offered** — the tree's accumulator
  and process frame would make a "one step" button run zero or several steps plus a process frame, so exact stepping
  needs a tree API (a later G7 follow-up).

## Consequences

- Game code runs in the editor process: an infinite loop or native crash takes the editor down (recovery copies limit
  the loss); the toolbar tooltip says so.
- The copy shares tree groups with the edited scene (`GetNodesInGroup` sees both) and shared external resources.
- `Node` gained one byte (`_simulation`), placed next to the other byte fields.
