# Plan: G1.18 editor reload of changed resources and preview of instanced clips

**Status:** done, see [log](../logs/2026-11-15-g1c-editor-reload.md)
**Docs:** [Animation authoring proposal](../../docs/design/future/animation-authoring.md) (Editor live loop, task list
G1.18), [Animation and skinning → Animation panel](../../docs/design/animation.md#animation-panel),
[Editor](../../docs/design/editor.md), [Asset pipeline → linked libraries](../../docs/design/asset-pipeline.md)
**Started:** 2026-11-15

## Goal

A clip baked or edited outside the editor (`mfe anim bake`, a person, a script) shows up in the open scene without
reopening it: a changed `.mfres` the editor has loaded is reloaded in place (references stay valid, players re-bind), a
model whose `.mfmeta` changed (a newly linked library) is re-imported and the clean scenes that instance it reopen, and
anything with unsaved edits in the editor asks before it is replaced. Selecting an instanced model shows its inner
`AnimationPlayer` in the Animation panel, read-only, so its clips (baked ones included) can be previewed.

## Not in scope

- The `animate` skill (G1.19). Editing an instanced model's clips in the panel (they belong to the model and its
  libraries: bake or edit them with `mfe`).

## Design notes (decided here)

- **Engine:** `ResourceLoader.ReloadInPlace(path)` → `ResourceReloadResult` (`NotCached`, `Unchanged`, `Reloaded`,
  `Failed`): reads the file fresh (bypassing the cache); when it equals the cached resource serialized (the editor's own
  save, a touch), nothing happens; else the exported values are copied into the cached instance through the type registry,
  inline resources in place (lists of named resources, such as a library's animations, matched by name), and changed
  `Animation`s get `NotifyChanged`. `ResourceLoader.Evict(path)` drops a cached resource so the next load reads it again.
- **Watcher:** `ProjectFileSystem` also queues every changed `.mfres` and `.mfmeta` path (meta files are hidden from the
  tree, so its relevance filter would drop them); `TakeChangedFiles()` hands them to the reloader on the main thread.
- **`ResourceReloader`** (editor, `Src/FileSystem/`): for a `.mfres` with unsaved edits (the inspected resource is dirty,
  or a scene holds it in `EditedResourceFiles` and is dirty) it asks through the message dialog ("Keep mine" / "Load from
  disk"; the editor has no toasts, and the dialog queues without blocking frames); otherwise it reloads in place and
  raises `Reloaded`. For a model's `.mfmeta` whose model is cached it evicts the model and reopens the clean scenes that
  instance it (`Suspend`/`Resume`, as code reload does); a dirty one gets a message to save and reopen.
- **Animation panel:** a selected instance root (a node with `SceneFilePath`, not the edited root) with an
  `AnimationPlayer` inside resolves to that player, read-only: no history (every edit already needs one), no key
  buttons, the edit controls hidden, a note naming the model. `Reloaded` restarts a running preview at its time.

## Steps

- [x] Engine reload in place and eviction. Verified by: unit tests (unchanged, reloaded in place with the same instances
  and a new clip, a removed clip, failed parse keeps the old content, not cached, evict).
- [x] Watcher path queue and `ResourceReloader`. Verified by: editor tests (a library rewritten on disk reloads in the
  open scene's player; an editor save is ignored; a dirty inspected resource asks and "Load from disk" replaces it; a
  model's meta change reopens a clean scene with the new library and asks for a dirty one).
- [x] Read-only instance preview in the Animation panel; preview restart on reload. Verified by: editor tests (instance
  root resolves the inner player read-only, edits do nothing, preview poses and restores, reload restarts preview).
- [x] Docs (animation.md, editor.md, proposal G1.18 built + changes, AGENTS.md if structure changes), log.
- [x] Gates: `just build`, Release `-warnaserror`, `just test`, `just format-check`, `just shaders-check`, `check.sh`;
  `just test-render` for the editor smoke run.

## Done when

- In an editor test, `mfe`-style rewriting of a linked library makes the open scene's player play the new clip with no
  reopen; a newly linked library on a model reopens the clean scene that instances it.
- Unsaved edits are never replaced without the dialog's "Load from disk".
- The gates pass; a PR (stacked on #215) is open with Auto-fix on.

## Open questions

- None for the owner.
