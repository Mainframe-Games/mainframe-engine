# ADR 0082 — Undo/redo: per-scene history of `IEditorAction`s, keyed merge windows, save point by action identity

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 E2 (lane m10a)
- **Spec:** docs/design/editor.md#undo--redo

## Decisions

1. **`UndoRedo` per scene tab** holding `IEditorAction { Name; Do(); Undo(); TryMerge(next) }`. Committing cuts the redo
   branch; beyond `Limit` (256) the oldest entries are dropped. Actions that keep detached nodes (a removed subtree, an
   undone addition) implement `IDiscardableAction.Discard(applied)` and free them when they leave the history.
2. **Merging by key until released.** `Commit(action, mergeKey)` merges into the top entry while the same key's window
   is open; `EndMerge()` (mouse up, field committed, undo, save) closes it. Gizmo drags apply live and commit once on
   release (`alreadyApplied`), so they never need merging.
3. **Dirty = the top entry differs from the one marked saved** (reference identity, a sentinel for "nothing applied").
   Cutting the branch that held the save point, or pruning past it, makes the saved state unreachable (dirty for good).
4. **Structural actions restore exactly**: remove/reparent snapshot owners (a detach clears owners that stop being
   ancestors) and restore them; reparent restores the exported local transform (position, rotation degrees, scale) on
   undo rather than recomputing it; removed subtrees keep their signal connections because they are detached, not
   freed.
5. **Duplicate packs the scene and instantiates it again**, then takes the copies out, so copies are exact for every
   serializable aspect (nested instances stay instances, missing types, inline resources copied, groups, connections
   inside the copy re-made as the edited scene's own).
6. **Selection is not undoable** (Godot); it is pruned when undo removes selected nodes.
