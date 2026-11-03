# ADR 0238 — The editor opens scenes asynchronously behind a loading window

- **Date:** 2026-11-04
- **Status:** accepted
- **Supersedes:** the "Not done" items of [ADR 0183](0183-async-scene-loading-and-loading-screen.md) that read "async
  loading in the editor" and "spreading the first frame's GPU uploads over several frames"
- **Docs:** docs/design/editor.md#loading-window, docs/design/scene-serialization.md#asynchronous-loading-adr-0183
- **Plan:** memory/plans/2026-11-04-editor-async-loading.md

## Context

The owner, on Linux (GNOME): opening the Forest in the editor froze the window long enough for "Mainframe Editor is not
responding — Force Quit / Wait". Measured (Debug editor, RTX 3070 Ti, tree bakes cached): the project opens in 0.1 s,
then the scene opens in a few blocking frames — the valley built in `ForestValley.OnReady` (6.8 s; 18.8 s with an
empty bake cache), the first `PrepareFrame` (9.9 s, ~7 s of it decoding 223 textures), the stale probe bake's
`PrepareBake` (4.8 s) and nine ~1 s pre-warm frames: about 43 s with frames up to 9.9 s. Mutter asks after 5 s. The
editor's splash showed a status line, but ran the open as one call inside one frame, so it never moved.

## Decision

- **Engine: instance loads.** `SceneTree.InstantiateSceneAsync(path, enter, options)` runs ADR 0183's `SceneLoad`
  (worker load, instantiate, `ISceneLoadable.LoadInBackground`, image prefetch, warm-up) for a scene the caller places:
  `enter(root, packed)` runs on the main thread at the start of a tick and owns the `PackedScene`. Such loads never cancel
  or get cancelled by scene changes, and do not count for `IsLoading`.
- **Engine: edit mode.** A load started in an edit-mode tree (read from the tree on the main thread) skips loadables
  the tree would not run there (`EditModeScripts` types that are not `[Tool]`) and tells the others
  (`SceneLoadProgress.EditMode`); the Forest's valley builds its editor variant on the worker.
- **Engine: a paced warm-up, opt-in** (`SceneLoadOptions.PaceWarmUp`; the editor sets it, games keep the old warm-up).
  **Late textures:** when such a load starts warming up its `ImagePrefetch` accepts late textures: a file texture
  whose GPU upload comes before its pixels were prepared (process-wide caches — model imports, tree materials, a second
  open of the same scene — created it before the load) is queued on the pool by `ImagePrefetch.Defer`, and
  `TextureGpu.Update` keeps the material's fallback until a later frame. Terrain splat packing waits the same way
  (`TerrainLayerPacker.Defer`). The warm-up waits for those decodes.
- **Engine: pacing.** While such a load warms up (`SceneLoadPacing`), `MeshRenderer.Prepare` gives the creation of GPU
  resources for nodes it meets the first time a budget of 100 ms per frame; further new nodes are skipped that frame and
  counted. The warm-up waits for a frame with none left.
- **Engine: the probe bake's scene builds on its task.** `ProbeBakeSceneBuilder.Capture` reads the nodes on the main
  thread (transforms, placements, a terrain's cells, copied) into steps a builder replays anywhere; the background bake
  replays and builds on its task, with the same hash as before.
- **Editor: `EditorLoader`** replaces `RunWithSplash`'s single blocking call. A job runs its work two frames after the
  window shows, may stay open with `Hold()` (the game code's build) and `Track(SceneLoad)` (a scene through
  `EditorSession.OpenAsync`), shows title, step, detail and progress every frame, measures its longest frame, and can be
  cancelled. Jobs queue. Code reload waits while a job runs. Without the splash (tests) everything stays synchronous.
- **Editor: the loading window** is the splash: the full brand splash at start-up, a panel over the dimmed editor for
  later opens, with the job's title, the step, a detail line, the elapsed time and Cancel.

## Consequences

- The Forest opens in ~16 s with no frame over ~1.1 s (Debug, this machine); reopening the scene, ~14 s with frames under
  ~0.9 s. Cancel leaves the editor with no Forest tab.
- Games' async loads keep their warm-up as it was: pacing on by default made the warm-up longer on lavapipe, the game
  ran more physics steps before its counted frames, and the physics goldens' frames moved. A game can opt in.
- Nodes created after the Forest's own pre-warm frames compile their pipelines when first drawn (the pipeline cache
  makes that cheap); the warm-up still waits until every node has its GPU resources.
- Not done: code reload still re-creates scenes synchronously; the idle editor draws every collision shape each frame
  (~120 ms in the Forest); a node whose own creation exceeds the budget (the terrain) still costs one long frame.
