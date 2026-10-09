# ADR 0183 — Asynchronous scene loading and a loading screen

- **Date:** 2026-10-10
- **Status:** accepted
- **Milestone:** Forest vertical slice (playtest feedback)
- **Docs:** docs/design/project-and-gamehost.md#async-loading-and-the-loading-screen-adr-0183,
  docs/design/scene-serialization.md#asynchronous-loading-adr-0183, docs/design/game-ui.md#loading-screen-adr-0183,
  docs/design/forest.md#loading-screen-adr-0183

## Context

Brogan, after a playtest: "the app just freezes for a bit and feels like it's not working". Measured on an M5 with the
Forest (Release, cached tree bakes): the window appeared about 1 s after launch and then stayed black and unresponsive
(no event pump, the macOS beachball) until the first frame was presented at ~10 s; the game was playable at ~12.8 s.
The time went to:

- `GameSession.Start` on the first update (~3–4 s): `ChangeSceneToFile`, then `ForestValley.OnReady` — valley
  generation 0.4 s, carving and painting 0.5 s, trees 1.5 s (15 s on a first launch, which bakes the leaf clusters and
  impostors), props 0.1 s.
- The first frame's `RenderServer.PrepareFrame` (~5–6 s): GPU resources for every visual, dominated by decoding the
  PNG/JPEG textures on the main thread (~3.3 s, StbImageSharp) and building the coverage-preserving mip chains of the
  leaf-cluster and impostor atlases (~1.3 s); then pipelines (~0.5 s).
- The second frame (~1.4 s: the foliage's instance buffers) and the pre-warm frames (~0.1–0.15 s each).

## Decision

- **Engine: an asynchronous scene change.** `SceneTree.ChangeSceneToFileAsync(path, SceneLoadOptions)` returns a
  `SceneLoad`: a worker thread loads and instantiates the `PackedScene` outside the tree (node constructors are cheap and
  side-effect free; nothing enters a tree), runs the scene's `ISceneLoadable.LoadInBackground`, and waits for the image
  prefetch; the main thread then makes it current at the start of a tick and keeps it behind the loading screen for
  `WarmUpFrames` (2) and until every loadable's `PollLoaded` is true. The tree advances the load each tick; the current
  scene runs until the new one enters (Godot's `load_threaded_request` + `change_scene_to_packed`).
  `ResourceLoader.LoadThreaded<T>` is the single-resource form. Newer scene changes cancel a load in progress; failures
  leave the current scene. Synchronous `ChangeSceneToFile` is unchanged (the editor, headless, tests, reloads).
- **A cooperative loadable interface.** `ISceneLoadable` (default members): `LoadInBackground(SceneLoadProgress)` on the
  worker while outside the tree, `PollLoaded(SceneLoadProgress)` per frame after entering, `LoadWeight`. Nodes report a
  fraction and a stage label; `SceneLoad.Progress` weights the stages (load 6 %, instantiate 4 %, loadables 55 %,
  decoding 12 %, entering 5 %, warm-up 18 %) and never goes back. `Terrain3D.BuildNow()` and
  `TreeScatter.Rebuild(progress)` let a loadable build them off the tree; their ready then skips the build.
- **Decode ahead.** `ImagePrefetch` (internal): textures created on the load's execution flow (an `AsyncLocal`) queue
  their file's decode — once per file — and, for coverage-preserving mipmapped textures, their mip chains on the thread
  pool; `Texture2D.DecodePixels` (a copy) and the GPU upload / terrain layer packing (shared, read-only) find them. Held
  until the load completes. GPU resources and pipelines stay on the main thread (the warm-up frames).
- **The loading screen** is UI: `LoadingScreen : UiLayer` with an RmlUi document bound to a `loading` data model
  (`title`, `stage`, `progress`, `percent`), modal while loading, fading out when the load completes and freeing itself.
  The engine ships a branded default (`Content/UI/loading/loading.rml`); a project picks its own with the new
  `loading.screen` setting, and `loading.async` (default true) turns the async start off.
- **GameHost** starts the main scene asynchronously behind the screen by default. `SceneTree.IsLoading` (a load or a
  screen) decides, once per frame when its update begins, whether a frame is a loading frame; `--max-frames`,
  `--screenshot` and `--bake-lighting` count only the others, so captures and timings start once the game is on screen.
  QA flags: `--sync-load`, `--loading-hold <s>`, `--loading-screenshot <png>`. GameHost logs the first presented frame
  and the moment the start scene is on screen.
- **The Forest** makes `ForestValley` loadable (everything it built in ready is built on the worker), styles its screen
  after the pause menu over the R1 shot ("Forest Demo"), and starts its benchmark and autowalk after loading.

## Consequences

- The Forest's window shows the loading screen at ~1.3 s instead of a black window until ~10 s, and the game is playable
  at ~9 s instead of ~12.8 s. The worker phase animates at the display rate; the warm-up (~3.5 s of GPU uploads,
  pipelines and instance buffers behind the screen) still runs at a few frames per second.
- Every game gets the async start and the default screen (the Demo flashes it for ~0.3 s). With `--fixed-fps`, captures
  land warm-up + fade frames later in scene time than before; `--sync-load` reproduces the old timing.
- `LoadInBackground` code runs on a worker: it must not touch the tree, servers, Vulkan, RmlUi, physics or audio. Static
  caches it shares with the main thread need locks; the Forest's are only used by the loading thread while it runs.
- A game showing a `LoadingScreen` that never fades never counts frames for `--max-frames`.
- Not done: spreading the first frame's GPU uploads over several frames, building pipelines on worker threads, and
  async loading in the editor (scene tabs still open synchronously).
