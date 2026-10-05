# Current state — mainframe-engine

_Last updated: 2026-10-05 — M0–M10 complete on `feature/m0-m10` (local; final push + PR to `main` pending)._

## What exists

- **Engine** (`MainframeEngine`, .NET 10, Vulkan 1.2; MoltenVK bundled on macOS, SDL2 windowing via Silk.NET 2.22):
  Godot-style `SceneTree` (M2) with source-generated `[Export]`/`[Signal]`/`[Replicated]`/`[Rpc]`/`[EditorIcon]`
  registration (`MainframeEngine.Generators`), `.mscene`/`.mres` JSON with UIDs; meshes/materials/textures, Assimp
  import, instancing, picking, `SubViewport`, HDR + ACES, in-house GPU allocator (M3); CSM/PCF/atlas shadows (M4);
  replication over ENet/loopback/simulated transports (M5; Steam inert — no natives); Jitter2 3D + Box2D.NET 2D physics
  (M6); SoundFlow audio with buses and 3D voices (M7); RmlUi 6.3 game UI through the in-house `mfrmlui` shim (M8);
  gettext localization with `mf-l10n` (M9); `project.mfproj`/`ProjectSettings`, `GameHost`, `InputMap`, `ILogSink`,
  editor link, collectible game-assembly reload, `mfgame` template (M10 engine side).
- **Editor** (`MainframeEngine.Editor`, M10, UI in RmlUi): Project Manager, New Project wizard, project settings,
  FileSystem panel, scene tabs (one `SubViewport` world each), scene tree, generated inspector (multi-select, Signals
  tab, custom inspectors), undo/redo, 3D/2D viewport + gizmos, Play (separate process over the editor link), code
  reload, Tabler icon atlas, editor settings. macOS app name "Mainframe Engine" via a dev `.app` bundle.
- **Version** v1.0.0 (git tags only; engine/editor/generator in lock step). `publish.yml` releases editor builds.
- **Solution** `MainframeEngine.slnx` (no `.sln`). Everything local goes through `just` (see CLAUDE.md).
- **Tests:** ~1 170 engine + ~430 editor unit tests; 43+ render tests (goldens `moltenvk` + `lavapipe`, validation
  gate, 0-B allocation gate); benchmarks with `baseline.json`; QA scripts (`just qa`, `qa-editor`, `qa-projects`).
  Lavapipe can be run locally in Docker: `just render-tests-linux` (x86_64 ubuntu:24.04, same packages as CI).
- **CI:** `ci.yml` (build-test ×3 OS, format, shaders, render-tests on lavapipe, template smoke, `ci-success`),
  `publish.yml`, `natives.yml`. GitHub Actions minutes are scarce: run things locally (Docker for Linux) first.

## Known gotchas

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
  `Content/Shaders/limits.json`; after shader edits `just shaders` (commit `.spv` + `shaders.lock`).
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
- Steam: no `steam_api` natives (partner SDK login needed; Steamworks.NET 2024.8.0 is x64-only) → Steam never starts.

## Next steps

1. Final push of `feature/m0-m10`, CI green (the lavapipe goldens were recorded locally in Docker — CI should show
   no "No golden" warnings), PR → `main` (rebase merge), then `publish.yml` for the first release tag.
2. M11 — backend-neutral GPU API + WebGPU (`docs/design/future/rendering-backend-abstraction.md`).
3. M12 — mobile core, Android + iOS (`docs/design/future/mobile.md`, ADR 0100; spikes M12.0 first); follow the
   mobile-ready plumbing checklist in `memory/context/lane-agent-brief.md` in any lane meanwhile.
4. M13 — mobile platform services (`docs/design/future/mobile-services.md`).
5. Editor after M10 (`docs/design/future/editor.md`): remote scene tree, simulate mode, box selection, docking.
6. User actions: Steamworks SDK natives (partner login); code signing/notarization for the macOS editor build.
