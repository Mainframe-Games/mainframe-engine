# ADR 0137 — Scene changes wait for Godot's flush point, input included

- **Date:** 2026-10-06
- **Status:** accepted (Crash Site Defense port: the New Game → Start crash)
- **Amends:** the "deferred during a tick" rule of `SceneTree.ChangeScene`

## Context

Clicking Start on Crash Site Defense's New Game screen crashed the port about one time in three: a segfault in
`mfrmlui_context_process_mouse_button_up`, after RmlUi logged "Could not find data event callback ''". Sometimes it froze
instead. Input reaches the tree through `PushInput`, outside `Tick`, and `ChangeScene` only deferred during a tick. So
the click's data event swapped the scene at once. It freed the menu and built the whole world, with its UI layers,
documents and data models, while RmlUi was still dispatching the click. `RmlCore.DeferIfInCallback` already defers
destroying contexts and models, but building a new scene inside the dispatch still corrupted it.

The same path also differed from Godot in a way the player could see. The new world's first process ran in the click's
frame, so the crew member swung at spawn (use_item read as just pressed). Godot's `SceneTree::process` runs `_process`,
flushes the message queue, then `_flush_scene_change`, then timers. The new scene's first process is a frame later.

## Decision

1. `ChangeScene` is immediate only outside a tick and outside input dispatch (start-up, tools, tests). During either, it
   records a pending scene, Godot's `pending_new_scene`.
2. The pending scene is applied in `RunProcess` after the process callbacks: deferred calls flush, then the swap, then
   timers and tweens. That is Godot's order.
3. Only the last change of a frame wins. An earlier pending scene is freed without entering the tree, and an immediate
   change drops a pending one. `Shutdown` frees a pending scene.
4. Unlike Godot, the old scene is not removed from the tree at once. It stays until the flush, so nothing is removed
   inside the dispatch either.

## Consequences

- Clicks that change scene never build or free a scene inside RmlUi. Five mouse-driven runs after the fix had no crash,
  no warning and no swing at spawn.
- A scene change made during a tick now lands after that frame's process, not at its first deferred-call flush (a frame
  later at most, as in Godot). Changes made in timers, tweens or server callbacks land on the next frame's flush.
- Tests: `AClickThatChangesSceneSwapsItAtTheNextFrameNotInsideTheDispatch`, `OnlyTheLastSceneChangeOfAFrameIsApplied`
  (UiServerTests) and `ASceneStartedByAClickNeverSeesThatClickAsJustPressed` (InputMapTests).
