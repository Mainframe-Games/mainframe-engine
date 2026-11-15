---
summary: >-
  G1.18: the editor follows files changed outside it — ResourceLoader.ReloadInPlace/Evict, the watcher's queue of .mfres
  and .mfmeta paths, ResourceReloader (reload in place, ask on unsaved edits, reopen clean scenes whose model changed) —
  and the Animation panel previews an instanced model's player read-only.
---

# 2026-11-15: G1c editor reload and instanced preview (G1.18)

Branch `feature/g1c-editor-reload`, stacked on `feature/g1c-contact-sheet` (#215). Plan:
[G1c editor reload](../plans/2026-11-15-g1c-editor-reload.md). As built:
[Editor → Files changed on disk](../../docs/design/editor.md#files-changed-on-disk),
[Animation and skinning → Animation panel](../../docs/design/animation.md#animation-panel).

## What changed

- **Engine** (`Src/Resources/ResourceLoader.Reload.cs`): `ReloadInPlace(path)` → `ResourceReloadResult` (copies the
  file's exported values into the cached instance through the type registry, inline resources in place, named lists such
  as a library's animations matched by name, `Animation.NotifyChanged`), `Evict(path)`, and file stamps (size and time)
  recorded at load and save so the loader's own reads and saves are `Unchanged`. `ResourceLoader` is partial.
- **Editor:** `ProjectFileSystem.TakeChangedFiles` (every changed `.mfres`/`.mfmeta`, meta files hidden or not);
  `ResourceReloader` (`EditorWorkspace.Reloader`, fed by the FileSystem panel's tick: reload in place and `Reloaded`;
  "Keep mine" / "Load from disk" through the message dialog for unsaved edits; evict a model whose sidecar changed and
  reopen the clean scenes that instance it, a message for dirty ones); `EditedScene.ForgetEditedResourceFile`;
  `AnimationEditor` resolves a selected instance's inner player read-only (`ReadOnly`, `ReadOnlySource`, no history, no key
  buttons) and restarts a running preview on `OnResourceReloaded`; `AnimationPanel` hides the edit controls and shows the
  model's name when read-only.
- **Tests:** `ResourceReloadTests` (4, engine), `EditorReloadTests` (6), `ProjectFileSystemTests` gains the watcher feed.
- **Docs:** editor.md (Files changed on disk), animation.md (instanced models in the panel, Testing), asset-pipeline.md,
  the proposal (G1.18 built and its changes), AGENTS.md.

## Decisions

- None needing an ADR: the plan's design notes and the proposal's "Changes" entry for G1.18.

## Dead ends

- Telling the editor's own saves apart by serializing the cached resource and comparing it with the file does not work:
  a saved resource is external, and the writer writes an external resource as a reference. File stamps at load and save
  do the job.
- `UndoRedo.Clear` keeps a dirty history dirty (the save point is lost); "Load from disk" also marks it saved.
- The first gate run failed the editor's allocation gates (32 B a frame in every mode): `TakeChangedFiles`, called every
  frame by the FileSystem panel, built a list even with nothing queued. It returns the shared empty array now.

## Verified

- `just build`, Release `-warnaserror`, `just test`, `just test-render` (the editor smoke run), `just format-check`,
  `just shaders-check`, `just state-guard`, `check.sh` (numbers in the PR).
- Not run by hand in the editor: rewriting a library with `mfe` while the editor shows the scene (the editor tests drive
  the same reloader, watcher feed and panel model).

## Not done

- G1.19 (the `animate` skill and an end-to-end run).
- A QA script: an editor QA script cannot write files from outside the editor mid-run.

## State changes

- milestones G1c: "G1.17 contact sheets ✅; G1.18–G1.19 ⬜" → "G1.17 contact sheets ✅; G1.18 editor reload and instanced
  preview ✅; G1.19 ⬜" (after the G1.17 entry from 2026-11-15-g1c-contact-sheet).
