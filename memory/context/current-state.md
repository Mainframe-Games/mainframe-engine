# Current state — mainframe-engine

_Last updated: 2026-10-27 — M0–M10, Demo & polish and most of the 2D port work on `main`; latest release v1.1.5.
In flight: G1e root motion, G9 3D joints + ragdolls, G7 editor viewport tools (branches `feature/g1e-root-motion`,
`feature/g9-joints-ragdolls`, `feature/g7-viewport-tools`)._

## What exists

- **Engine** (`MainframeEngine`, .NET 10, Vulkan 1.2; MoltenVK bundled on macOS, SDL2 windowing via Silk.NET 2.22):
  Godot-style `SceneTree` (M2) with source-generated `[Export]`/`[Signal]`/`[Replicated]`/`[Rpc]`/`[EditorIcon]`
  registration (`MainframeEngine.Generators`), `.mscene`/`.mres` JSON with UIDs; meshes/materials/textures, Assimp
  import, instancing, picking, `SubViewport`, HDR + ACES, in-house GPU allocator (M3); CSM/PCF/atlas shadows (M4);
  replication over ENet/loopback/simulated transports (M5); Steam over Facepunch.Steamworks 2.4.1 with Valve's
  natives shipped (ADR 0189); Jitter2 3D + Box2D.NET 2D physics (M6); SoundFlow audio with buses and 3D voices (M7);
  RmlUi 6.3 game UI through the in-house `mfrmlui` shim (M8); gettext localization with `mf-l10n` (M9);
  `project.mfproj`/`ProjectSettings`, `GameHost`, `InputMap`, `ILogSink`, editor link, collectible game-assembly
  reload, `mfgame` template (M10 engine side).
- **Editor** (`MainframeEngine.Editor`, M10, UI in RmlUi): Project Manager, New Project wizard, project settings,
  FileSystem panel, scene tabs (one `SubViewport` world each), scene tree, generated inspector (multi-select, Signals
  tab, custom inspectors), undo/redo, 3D/2D viewport + gizmos, Play (separate process over the editor link), code
  reload, Tabler icon atlas, editor settings. macOS app name "Mainframe Editor" via a dev `.app` bundle (#118).
- **Since M10** (details in `progress-log.md`): 2D canvas renderer, `Camera2D`, canvas text, canvas shaders, SVG
  (ThorVG), 2D lights and canvas post, `Tween`, `GpuParticles2D`, `Polygon2D`/`Skeleton2D`, 2D joints (ADR 0201),
  `SceneTree.TimeScale`; headless host, game packaging + signing, NuGet packages (ADR 0188); saves and settings (G4);
  post-processing (ADR 0163) and the F12 dev overlay; gameplay recording (ADR 0190) and the editor's Video mode (ADR
  0191); Music mode, DAW, wavetable synth and adaptive music (ADRs 0192–0196); `Assets/` content folder (ADR 0197); 3D
  skeletal animation and skinned import (ADRs 0207/0208); spatial shaders (ADR 0205); 3D particles (ADR 0206); Spine
  shadows and billboards (ADR 0204); `SteamSocketsTransport` and Steam avatars (#127, #128); the Forest showcase
  (terrain, trees, water, baked GI; ADR 0209); the docs site at engine.mainframe.games; editor self-update; Linux
  launcher.
- **Version** v1.1.5 (git tags only; engine/editor/generator in lock step). `publish.yml` releases editor builds and
  the `MainframeEngine` + `MainframeEngine.Templates` NuGet packages.
- **Solution** `MainframeEngine.slnx` (no `.sln`). Everything local goes through `just` (see CLAUDE.md).
- **Tests:** ~2 100 engine, ~1 090 editor and ~370 video unit tests; ~190 render tests (goldens `moltenvk` +
  `lavapipe`, validation gate, 0-B allocation gate); benchmarks with `baseline.json`; QA scripts (`just qa`, `qa-editor`,
  `qa-projects`, `qa-settings`). Lavapipe can be run locally in Docker: `just render-tests-linux` (x86_64 ubuntu:24.04,
  same packages as CI).
- **CI:** `ci.yml` (build-test ×3 OS, format, shaders, render-tests on lavapipe, template smoke, `ci-success`),
  `publish.yml`, `natives.yml`. Jobs fetch only the LFS files they need, cached by object ID. GitHub Actions
  minutes are scarce: run things locally (Docker for Linux) first.

## Known gotchas

- Inside `namespace MainframeEngine` the engine's `Animation` (ADR 0207) shadows `Spine.Animation`: qualify Spine's. Silk.NET's
  Assimp key structs do not match the bundled native library's size; read keys through `ModelImporter`'s detected stride.
- macOS: SDL and Silk.NET must bind the SAME Vulkan library — `VulkanLoaderBootstrap` enforces it; never call a bare
  `Vk.GetApi()`, take `Vk` from `IVulkanContext`. HiDPI: use `Engine.FramebufferSize` (pixels), not window sizes
  (points). Render tests, `just qa*` and the packaged editor need the display awake (`caffeinate -u -t 3000 &`).
- Linux: Silk's resolver doesn't probe `runtimes/linux-x64/native` on distro RIDs — `SilkNativeResolver.Install()`
  (Engine ctor) fixes it; call it before Silk native use without an Engine.
- Per-frame GPU resources keyed by `IVulkanContext.FrameSlot`; GPU memory only via `GpuAllocator`/`GpuBuffer`/
  `GpuImage`/`GpuTexture`, uploads via `UploadQueue`, frees via `DeletionQueue` (no Queue/DeviceWaitIdle).
  End every `RenderTarget` pass with `RenderTarget.End` (MoltenVK ignores render-pass external dependencies).
  Keep the default UNORM swapchain (MoltenVK mutable-format sRGB views go stale).
- `ShadowSystem.RenderShadows` once per frame; comparison samplers are immutable (MoltenVK); shader limits only in
  `Assets/Shaders/limits.json`; after shader edits `just shaders` (commit `.spv` + `shaders.lock`).
- Scene tree: node constructors cheap and side-effect free; projects declaring node types need the generator analyzer.
  `MainframeEngine.Timer` shadows `System.Threading.Timer`.
- Physics: Jitter2/Box2D calls stay in `Src/Physics`; Box2D worlds are process-global (`SerialBox2D` test collection);
  render tests use `PhysicsSettings3D.Deterministic`. x64 vs arm64 floats differ after contact (lavapipe vs moltenvk).
- RmlUi and `Tr` are process-global (`SerialRmlUi`, `LocalizationState` collections). `Tr` takes literals only.
- Editor code reload: editor code must not keep game nodes/types in fields or statics past
  `ReleaseEditorReferences`; never keep game objects in locals of the unloading method (GC-verified unload).
- Allocation gates: render host runs `DOTNET_TieredCompilation=0`; unit gates use `AllocationGate.SmallestWindow`;
  in-process transports use a private packet pool (never `ArrayPool.Shared` in per-frame code that tests share).
- CI builds (`CI=true` → `ContinuousIntegrationBuild`) map `[CallerFilePath]` to `/_/…` in engine/editor assemblies
  (test projects opt out): features that open source files only work in local builds. Lavapipe goldens come from such
  builds (no Output source-link icons). Per-frame formatting: never interpolate enums, and prefer `int.TryFormat` over
  interpolated handlers in code that must not allocate in a busy process (tier-0 `AppendFormatted<T>` boxes).
- Docker Linux tests (`just test-linux` / `just render-tests-linux`) run under Rosetta x86_64 emulation: an
  occasional whole-process stall is retried once by `build/linux/inside.sh`; never seen on native x64.
- Benchmarks: compare only on the machine that recorded `baseline.json` (MacBook, Apple M5), and only when it is
  quiet (parallel builds/agents skew results by 10–15 %).
- Steam: Facepunch.Steamworks 2.4.1 (vendored, `Plugins/Facepunch.Steamworks`) behind the `MainframeEngine.Steamworks`
  shim (Win64/Posix builds, `SteamworksLoader` picks this OS's); Valve's `steam_api` natives ship in `runtimes/`, so
  Steam starts on win-x64, linux-x64, osx-x64 and osx-arm64 when the client runs. `SteamSocketsTransport` (relay
  sockets) and avatar textures are done.

## Next steps

1. Land the in-flight branches: G1e root motion, G9 3D joints + ragdolls (G9.7 limit handles follow G7.3), G7 editor
   viewport tools.
2. Gameplay toolkit (`docs/milestones.md`): G2a/G2b sprites and tile maps, G5 export from the editor, G10 profiler,
   then G3a navigation, G1c animation authoring, G11–G16.
3. Re-record `Tests/MainframeEngine.Benchmarks/baseline.json` on a quiet machine (the Forest cabin work added
   ≈ 1.6 ms p50 on a loaded machine).
4. M11 — backend-neutral GPU API + D3D12 + WebGPU (`docs/design/future/rendering-backend-abstraction.md`).
5. M12 — mobile core, Android + iOS (`docs/design/future/mobile.md`, ADR 0100; spikes M12.0 first); follow the
   mobile-ready plumbing checklist in `memory/context/lane-agent-brief.md` in any lane meanwhile.
6. M13 — mobile platform services (`docs/design/future/mobile-services.md`).
7. User actions: code signing/notarization for the macOS editor build.
