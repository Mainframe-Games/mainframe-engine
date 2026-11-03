# Plan: Editor loading window and asynchronous scene open

**Status:** done, see [log](../logs/2026-11-04-editor-async-loading.md) (owner approved 2026-11-04; open questions taken at their defaults)
**Docs:** [ADR 0183](../decisions/0183-async-scene-loading-and-loading-screen.md) (async scene loading; its
"Not done" list names async loading in the editor) · [Scene serialization → asynchronous loading](../../docs/design/scene-serialization.md#asynchronous-loading-adr-0183) ·
[Editor → Projects](../../docs/design/editor.md#projects) · [Forest](../../docs/design/forest.md) ·
[Probes](../../docs/design/lighting.md)
**Started:** 2026-11-04

## Goal

Opening a project or a scene in the editor (start-up, Project Manager, Open Project, a scene from the FileSystem panel)
runs as a loading job behind a loading window: a progress bar, the current step's name ("Loading game code", "Generating
the valley", "Decoding textures", "Uploading to the GPU", …) and its detail ("112 / 274 textures"). The heavy work runs
on a worker thread (ADR 0183's `SceneLoad`, reused for editor tabs) or is spread across frames, so the editor keeps
pumping window events and redrawing the window. Opening the Forest never shows GNOME's "Mainframe Editor is not
responding" dialog.

## Where the time goes today

Measured on 2026-11-04 on the owner's Linux box (CachyOS, RTX 3070 Ti), Debug editor (`just editor Examples/Forest`),
tree bakes cached, with temporary `Stopwatch` logging in a scratch worktree (not committed). The project opens in
~0.1 s (asset database 7 ms, game code 12 ms). Opening `forest.mscene` takes ~43 s to an idle editor, in a few frames
that each block the main thread:

| Frame | Main-thread time | What |
|---|---|---|
| 1 | **6.8 s** (18.8 s with an empty tree-bake cache) | `EditorSession.Open` → `Attach` → `ForestValley.OnReady` builds the valley: generate 2.4 s, carve and paint 1.4 s, trees 2.4 s (13.5 s uncached), props 0.7 s; `Terrain3D` and `TerrainFoliage3D` ready 0.8 s. `Load` + `Instantiate` are 48 ms. |
| 2 | **9.9 s** | The first `RenderServer.PrepareFrame`: ~7 s of it is decoding 223 PNG/JPEG textures on the main thread (StbImageSharp), then mip chains, GPU resources, pipelines. |
| 3 | **4.8 s** | `LightProbeVolume.OnProcess`: the committed probe bake is stale, so `BakeWhenStale` starts a background bake, but `PrepareBake` runs on the main thread first: `Collect` 3.8 s (walking the valley, 746 k world capsules), `Build` 0.8 s. The bake itself runs on a task. |
| 4–12 | ~1 s each | The Forest's pre-warm frames (every batch drawn: shadows 0.35–0.45 s, offscreen 0.25–1.1 s, `PrepareFrame` 1.4 s on the first). |
| after | ~0.15 s each | Idle editor at ~7 fps: the editor sets `DebugCollisionShapes = true`, so `PhysicsServer3D.Process` draws every collision shape of the Forest every frame (~120 ms). Not a hang; a follow-up. |

Mutter's "not responding" dialog appears when a window misses its ping for `check-alive-timeout` (5 s by default), so
frames 1 and 2 alone trigger it. The existing splash (`SplashScreen`, `RunWithSplash`) shows a status line and a bar,
but it runs the whole open as one call inside a single frame, so the bar never moves.

## Design

1. **Engine: `SceneLoad` for a scene that is not the tree's current scene.** Today `SceneLoad.Enter` calls
   `SceneTree.EnterLoadedScene` (a scene change; a newer change cancels it). Add
   `SceneTree.InstantiateSceneAsync(path, SceneLoadOptions, Action<Node, PackedScene> enter)`: the same worker stages
   (load, instantiate, `ISceneLoadable.LoadInBackground`, image prefetch) and the same warm-up (`PollLoaded`,
   `WarmUpFrames`), but entering calls `enter` (the editor attaches the root to a new tab's `SubViewport`). These loads sit
   in their own list advanced by `Tick`, never cancel or get cancelled by a scene change, and are waited for at shutdown
   like `_retiredLoads`. `ChangeSceneToFileAsync` becomes a caller of the same code.
2. **Engine: edit-mode loadables.** A loadable on the worker cannot ask `Tree.EditMode`. `SceneLoadOptions.EditMode`
   (the editor sets it from its tree) is passed to the loadables as `SceneLoadProgress.EditMode`, and in edit mode the
   worker skips loadables whose node would not run its own `OnReady` there (`SceneTree.EditModeScripts` says it is game
   code and the node is not `[Tool]`), exactly as `Node.SkipsScriptCallbacks` does in the tree. `ForestValley.LoadInBackground`
   passes `editing: progress.EditMode` to `Build` (today it hard-codes `false`).
3. **Engine: the probe bake leaves the main thread.** `LightProbeVolume.PrepareBake` splits into a cheap main-thread
   snapshot (the nodes, transforms and immutable mesh/terrain/variant references it reads) and the expansion into
   capsules plus `ProbeBakeSceneBuilder.Build`, which move into the background bake task. Measured first: if the
   snapshot itself is not cheap (well under 100 ms on the Forest), the fallback is that an editor tree only checks the
   hash and warns ("stale: Bake to update"), and never auto-bakes at load; the owner picks (open question 1).
4. **Editor: a loading job instead of `RunWithSplash`.** `EditorLoader` runs an ordered list of weighted steps, one per
   frame at most, each either quick (runs in one frame), a task or process it polls (the game build, already async),
   or a `SceneLoad` it polls. It exposes `Title`, `Step`, `Detail` and `Progress` (monotonic), and can be cancelled
   between steps and during a `SceneLoad` (the worker stops at its next check; the partial scene is freed; the editor
   keeps what it had, or shows the Project Manager when nothing is open). Opening a project is: project settings and
   asset database ("Scanning assets", the file count) → upgrade check → game code ("Loading game code", or "Building
   game code" with the build's last output line) → the start scene through `EditorSession.OpenAsync` (the `SceneLoad`
   stages: "Reading forest.mscene", "Building the scene", the loadables' own labels, "Decoding textures (n/m)",
   "Entering the scene", the warm-up labels "Uploading to the GPU" / "Compiling shaders"). While a job runs, project
   switching, code reload (the build-output watcher), Play and Simulate wait; a code reload or project close never
   starts while a worker still holds game objects (`WaitForWorker` before any unload, so the GC-verified unload holds).
   The synchronous `EditorSession.Open` stays for tests, reloads and recovery.
5. **Editor: the loading window.** The splash becomes the loading window (`splash.rml`, `SplashScreen` renamed
   `LoadingWindow` only if the owner wants; open question 2): at start-up it is today's full-window brand splash; for an
   open from inside the editor it is a centred modal panel over the dimmed editor. Both show the title ("Opening Forest
   Demo"), the step name, the detail line, the bar, the elapsed time and a Cancel button, bound through one RmlUi data
   model updated once per frame from `EditorLoader` (RmlUi stays on the main thread; the worker only writes
   `SceneLoadProgress`, which is already thread-safe).
6. **Spread the first frame's GPU work if still needed.** With images decoded on the pool, the first `PrepareFrame`
   should drop from 9.9 s to the GPU uploads, mip chains and pipelines. If any loading frame is still over 1 s in Debug
   on this machine, `RenderServer.PrepareFrame` gets a per-frame budget for creating new mesh GPU resources while the
   scene warms up (ADR 0183's other "Not done" item), so the uploads spread over the warm-up frames; otherwise this step
   is dropped with the measurement.

## Not in scope

- Code reload re-creating open scenes (`ProjectService.ReloadGameAssembly`): it still re-instantiates synchronously, so
  a Forest reload stalls the same way. A follow-up once this lands (it can reuse `InstantiateSceneAsync`).
- The idle editor's ~7 fps on the Forest (collision-shape debug drawing every frame): a separate follow-up.
- Building pipelines on worker threads.
- Committing a fresh Forest probe bake (it is stale since #133): an owner action in the editor (Bake, Save); it removes
  frame 3 for the Forest but not for other stale scenes, so step 3 is still done.
- Async opening of songs, videos and UI previews (they are not slow today).

## Steps

- [x] **Baseline QA.** `Tests/QA/forest-open.qa` (wait-for project, `state`, quit) and a loading-frame metric: the
  `EditorLoader` logs the job's duration and its longest frame ("Opened Forest Demo in 31.2 s; longest frame 9.9 s"),
  and the QA `state` line prints it. Run once on today's code path to record the baseline. Verified by: the logged
  numbers in the log of this work.
- [x] **`InstantiateSceneAsync` + edit-mode loadables** (design 1, 2), with `ChangeSceneToFileAsync` on the shared code.
  Verified by: new engine unit tests (a scene entered through the callback, not as the current scene; cancel before and
  after entering; failure frees the root; a loadable skipped in edit mode when it is game code and not `[Tool]`;
  `SceneLoadProgress.EditMode` seen by a loadable), and the existing `SceneLoad` tests and `just test-render` unchanged.
- [x] **Forest edit mode on the worker.** `ForestValley.LoadInBackground` honours `progress.EditMode`. Verified by: a
  Forest test loading `forest.mscene` with `EditMode` builds the valley without the gameplay parts (no walls, no player
  connection), as `OnReady` in edit mode does today.
- [x] **Probe bake off the main thread** (design 3). Verified by: the probe tests unchanged plus one asserting the bake
  scene built from a snapshot equals the one `PrepareBake` built before (same `ContentHash`); the Forest's
  `LightProbeVolume.OnProcess` frame under 100 ms (logged).
- [x] **`EditorLoader` + `EditorSession.OpenAsync`** (design 4), replacing `RunWithSplash` / `_pendingLoad` at its five
  callers. Verified by: editor unit tests with a test scene and a slow fake loadable (steps advance one per frame,
  progress is monotonic, cancel during the worker and during warm-up leaves no tab and frees the root, a code reload
  requested mid-load waits until the job ends, the sync `Open` unchanged), and the existing editor tests.
- [x] **Loading window** (design 5): `splash.rml` + `SplashScreen` (or `LoadingWindow`), the data model, Cancel, the
  start-up and in-editor looks. Verified by: `just qa-editor` still passing, and two captures from `forest-open.qa`
  (start-up, and opening the scene from the FileSystem panel) showing step name, detail and a partly filled bar.
- [x] **Measure and, if needed, budget GPU creation** (design 6). Verified by: `forest-open.qa`'s longest-frame line in
  Debug and Release; `just test-render` with no golden change and no validation warning; the allocation gate.
- [x] **Docs.** `docs/design/editor.md` (Projects: the loading job and window; Known issues: reload still sync),
  `scene-serialization.md` (`InstantiateSceneAsync`, edit mode), `forest.md`, the probes doc, ADR 0183's "Not done" list
  (via a new ADR that supersedes that part, written with `mf-decide`), `AGENTS.md` if a file moves. Verified by:
  `check.sh` (links), `just docs-build`.
- [x] **Gates.** `just build`, the Release `-warnaserror` build, `just test`, `just test-render`, `just format-check`,
  `just shaders-check`, `just qa-editor`, the Forest tests, `check.sh`. Verified by: each passing, noted in the log.

## Done when

- `forest-open.qa` (Debug, this machine) logs the open with a longest main-thread frame under 2 s (today 9.9 s), and
  under 1 s in Release.
- Opening the Forest with `just editor Examples/Forest` on GNOME shows the loading window with a moving bar and step
  names, and the "not responding" dialog does not appear (owner, by eye).
- Cancel during the Forest's valley build leaves the editor responsive with no Forest tab, and opening it again works.
- `just test`, `just test-render`, `just qa-editor` and the other gates pass.

## Open questions

1. If the probe snapshot cannot be made cheap: should an editor tree stop auto-baking stale probes at load (warn and
   leave the Bake button), or keep baking after a time-sliced collect? — owner.
2. One loading window for start-up and in-editor opens (today's splash look at start-up, a centred panel later), or the
   centred panel everywhere? Keep the name `SplashScreen` or rename it? — owner.
3. Should the loading window list the steps (a checklist with the current one highlighted) or show only the current
   step and detail? The plan assumes only the current step. — owner.
