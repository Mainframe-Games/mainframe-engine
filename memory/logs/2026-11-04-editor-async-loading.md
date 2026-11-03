---
summary: >-
  The editor opens projects and scenes behind a loading window without blocking: the Forest opens in ~16 s with no
  frame over ~1.1 s (Debug; 0.5 s Release) instead of ~43 s with 10 s frames, so GNOME no longer reports it hung.
---

# 2026-11-04: Editor loading window and asynchronous scene open

Branch `feature/editor-loading`. Plan: [Editor loading window and asynchronous scene open](../plans/2026-11-04-editor-async-loading.md).
Decision: [ADR 0238](../decisions/0238-editor-async-loading.md).

## What changed

- **Engine:** `SceneTree.InstantiateSceneAsync(path, enter, options)` runs ADR 0183's `SceneLoad` for a scene the
  caller places (the editor's tabs). A load in an edit-mode tree skips loadables the tree would not run and tells the
  others (`SceneLoadProgress.EditMode`). `SceneLoad.StageDetail` is a detail line. From the warm-up on, the load's
  `ImagePrefetch` takes late textures (`ImagePrefetch.Defer`, used by `TextureGpu.Update` and terrain splat packing),
  and `SceneLoadPacing` caps new GPU resources at 100 ms per frame in `MeshRenderer.Prepare`; the warm-up waits for both.
  Both only with `SceneLoadOptions.PaceWarmUp` (the editor's tabs).
- **Probes:** `ProbeBakeSceneBuilder.Capture` reads the nodes on the main thread; the background bake replays the
  inputs and builds its scene on its task (same hash).
- **Forest:** `ForestValley.LoadInBackground` builds the editor's variant when `progress.EditMode`.
- **Editor:** `EditorLoader` (`Workspace.Loader`) replaces `RunWithSplash`'s blocking call: jobs with `Hold`, `Track`,
  Cancel, a queue, the longest-frame log line; `EditorSession.OpenAsync`; `OpenSceneLoading` for the FileSystem panel,
  File › Open and start scenes; `OpenProjectNow` holds the job through the game build. Code reload waits while a job
  runs. The splash is also the loading window: title, step, detail, elapsed, Cancel; a panel over the editor after
  start-up (the start-up layout without a title is unchanged).
- **QA:** `Tests/QA/forest-open.qa`; `open-loading <scene>`; `click` finds the splash's elements; `state` logs the tabs
  and the last job.
- **Tests:** `SceneLoadTests` (instance loads, cancel, edit mode, pacing), `ImagePrefetchTests` (late textures),
  `LightProbeTests` (capture/replay), `EditorLoaderTests` (4), the Forest's `ValleyTests` (edit-mode worker build).
- **Docs:** editor.md (Loading window, Known issues), scene-serialization.md, lighting.md, forest.md, AGENTS.md.

## Decisions

- ADR 0238. The owner approved the plan without answering its open questions; the defaults were taken: the probe
  snapshot was made cheap, so the editor still auto-bakes stale probes (in the background); the start-up splash keeps its
  look and later opens get a panel; the class stays `SplashScreen`; the window shows the current step only.
- The edit-mode flag comes from the tree when the load starts, not from a new `SceneLoadOptions.EditMode` (the plan's
  design): the tree already knows, and a caller cannot get it wrong.
- Late textures and pacing went into the engine (the plan's step 6 only if needed): measurement showed they were. They
  are opt-in (`SceneLoadOptions.PaceWarmUp`), not on for every async load: see Dead ends.

## Dead ends

- With only the worker build and the load-flow prefetch, the first open still had a 3.9–5 s frame (mesh and multimesh
  creation 2 s plus shadows and offscreen), and a second open of the scene a 13.8 s frame: process-wide caches (model
  import templates, tree materials and cluster atlases) hand back textures the load's flow never created, so nothing
  prefetched them. Hooking each cache was rejected for the upload-time `Defer`, which catches every case.
- Pacing on for every async load failed two lavapipe goldens (`PhysicsJointsRenderCleanlyAndMatchGolden`,
  `PhysicsDebugDrawRendersCleanlyAndMatchesGolden`; they pass on `origin/main`): the render host is a game whose start
  scene loads asynchronously, the paced warm-up lasted more frames, physics stepped through them, and the counted
  frames showed a later moment. Opt-in keeps games' warm-up as it was.
- After that, one 2 s frame remained on a reopen: the terrain's splat packing decodes its layers on the main thread
  (`TerrainLayerPacker.Pack`); a new terrain chunk now waits while its layers decode.

## Verified

- Forest, `forest-open.qa`, this machine (RTX 3070 Ti, Linux): Debug open 16–17 s, longest frame 0.77–1.09 s (was
  ~43 s with 9.9 s); reopen ~14 s, longest 0.9 s; Cancel during the valley build ends in 2.9–3.7 s with no Forest tab,
  and the scene opens again. Release: open 13.8 s, longest 489 ms; reopen 10.2 s, longest 595 ms. Captures show the
  start-up window ("Opening Forest…", "Generating the valley", the bar, Cancel) and the in-editor panel.
- `just build`, the Release `-warnaserror` build (0 warnings), `just test` (engine 3 242, editor 1 809, video 460
  passed; engine rerun after the opt-in), Forest `ValleyTests` (13 passed), `just format-check`, `just shaders-check`,
  `just qa-editor` (Demo open 0.9 s, longest frame 331 ms), `check.sh`.
- `just render-tests-linux` (lavapipe, Docker): 222 passed with pacing opt-in (the splash golden unchanged).
- After making pacing opt-in, `forest-open.qa` again (Debug, Docker tests running beside it): open 17.9 s, longest
  989 ms; reopen 12.1 s, longest 762 ms.
- Measurements were taken with temporary `Stopwatch` logging in a scratch worktree and then in this branch; all of it
  was removed (`SlowLog` is gone).

## Not verified

- The GNOME "not responding" dialog by eye on the owner's desktop (the QA runs were `--hidden`).
- macOS (MoltenVK) and Windows; the `moltenvk` goldens.
- Benchmarks: `MeshRenderer.Prepare` gained a check per visible node (`SceneLoadPacing.IsActive`, a static read) — the
  allocation gate covers allocation, not time.

## Next

- Code reload re-creates scenes synchronously: reuse `InstantiateSceneAsync` there.
- The idle editor in the Forest draws every collision shape each frame (~120 ms in Debug, ~7 fps).
- Commit a fresh Forest probe bake (stale since #133) from the editor.
