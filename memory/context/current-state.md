# Current state — mainframe-engine

What is true and slow-changing. Only a state sync edits this file (`.agents/workflow.md#state-sync`, ADR 0280):
changes merged since then are in `just state-pending`.

- **What changed lately:** the newest files in `memory/logs/` (`ls memory/logs | tail`); older history in
  `progress-log.md`.
- **What is open:** `gh pr list`; branches are `feature/<slug>` and `fix/<slug>`.
- **What is done, per feature:** the status column of `docs/milestones.md` (✅/🚧/⬜, with what remains and what waits
  on the owner); how each works: `docs/design/`; why: `memory/decisions/`.

## What exists

- **Milestones:** M0–M10, Demo & polish and most of the gameplay toolkit are done (`docs/milestones.md`).
- **Engine** (`MainframeEngine`, .NET 10, Vulkan 1.2; MoltenVK bundled on macOS, SDL2 windowing via Silk.NET 2.22):
  the subsystem map is AGENTS.md's Project Structure.
- **Editor** (`MainframeEngine.Editor`, RmlUi): Game, Music and Video modes (`docs/design/editor.md`).
- **Showcases:** the Demo (one scene per feature), the Forest (ADR 0209) and Neon (ADR 0218).
- **Version:** git tags only (engine/editor/generator in lock step; `git describe --tags`). `publish.yml` releases
  editor builds and the `MainframeEngine` + `MainframeEngine.Templates` NuGet packages.
- **Solution:** `MainframeEngine.slnx` (no `.sln`). Everything local goes through `just` (see AGENTS.md).
- **Tests:** thousands of engine, editor, video and Demo unit tests; ~200 render tests (goldens `moltenvk` + `lavapipe`,
  validation gate, 0-B allocation gate); benchmarks with `baseline.json`; QA scripts (`just qa`, `qa-editor`,
  `qa-projects`, `qa-settings`). Lavapipe runs locally in Docker: `just render-tests-linux`.
- **CI:** `ci.yml` (build-test ×3 OS, format, shaders, render-tests on lavapipe, template smoke, a NativeAOT export
  per OS, `ci-success`),
  `publish.yml`, `natives.yml`. Jobs fetch only the LFS files they need, cached by object ID. GitHub Actions
  minutes are scarce: run things locally (Docker for Linux) first.
- **Workflow:** Mainframe workflow v1.0.0 (ADR 0233): `.agents/workflow.md`, `mf-*` skills, `check.sh` in CI.

## Known gotchas

- Inside `namespace MainframeEngine` the engine's `Animation` (ADR 0207) shadows `Spine.Animation`: qualify Spine's.
- Silk.NET's Assimp key structs do not match the bundled native library's size; read keys through `ModelImporter`'s
  detected stride.
- macOS: SDL and Silk.NET must bind the SAME Vulkan library — `VulkanLoaderBootstrap` enforces it; never call a bare
  `Vk.GetApi()`, take `Vk` from `IVulkanContext`.
- macOS HiDPI: use `Engine.FramebufferSize` (pixels), not window sizes (points).
- macOS: render tests, `just qa*` and the packaged editor need the display awake (`caffeinate -u -t 3000 &`).
- A rendering change re-records both golden sets (`moltenvk` needs a Mac: #157 and #164 left it stale), and scene
  self-checks must hold at the Mac's 640×480 as well as CI's 320×240.
- slangc depfiles name real paths (`/private/var`).
- Linux: Silk's resolver doesn't probe `runtimes/linux-x64/native` on distro RIDs — `SilkNativeResolver.Install()`
  (Engine ctor) fixes it; call it before Silk native use without an Engine.
- Per-frame GPU resources keyed by `IVulkanContext.FrameSlot`; GPU memory only via `GpuAllocator`/`GpuBuffer`/
  `GpuImage`/`GpuTexture`, uploads via `UploadQueue`, frees via `DeletionQueue` (no Queue/DeviceWaitIdle).
- End every `RenderTarget` pass with `RenderTarget.End` (MoltenVK ignores render-pass external dependencies).
- Keep the default UNORM swapchain (MoltenVK mutable-format sRGB views go stale).
- `ShadowSystem.RenderShadows` once per frame; comparison samplers are immutable (MoltenVK).
- Shader limits only in `Assets/Shaders/limits.json`; after shader edits `just shaders` (commit `.spv` + `shaders.lock`).
- Node constructors cheap and side-effect free; node-declaring projects need the generator; `Timer` shadows System's.
- Physics: Jitter2/Box2D calls stay in `Src/Physics`; the 6-DOF constraints copy Jitter2 2.9.0's constraint header and
  step conventions (ADR 0234: re-check on any upgrade).
- Box2D worlds are process-global (`SerialBox2D` test collection); render tests use `PhysicsSettings3D.Deterministic`.
- x64 vs arm64 floats differ after contact (lavapipe vs moltenvk).
- RmlUi and `Tr` are process-global (`SerialRmlUi`, `LocalizationState` collections). `Tr` takes literals only.
- A lambda capturing a local allocates at method entry, even on an early return: per-frame code uses method groups.
- Editor code reload: editor code must not keep game nodes/types in fields or statics past
  `ReleaseEditorReferences`; never keep game objects in locals of the unloading method (GC-verified unload).
- Allocation gates: render host runs `DOTNET_TieredCompilation=0`; unit gates use `AllocationGate.SmallestWindow`;
  in-process transports use a private packet pool (never `ArrayPool.Shared` in per-frame code that tests share).
- CI builds (`CI=true` → `ContinuousIntegrationBuild`) map `[CallerFilePath]` to `/_/…` in engine/editor assemblies
  (test projects opt out): features that open source files only work in local builds. Lavapipe goldens come from such
  builds (no Output source-link icons).
- Per-frame formatting: never interpolate enums, and prefer `int.TryFormat` over interpolated handlers in code that
  must not allocate in a busy process (tier-0 `AppendFormatted<T>` boxes).
- Docker Linux tests (`just test-linux` / `just render-tests-linux`): one shared build volume, one run at a time (a
  second waits), synced by content, testing the committed `.spv`; a Rosetta stall on a Mac is retried once.
- Game shaders (ADR 0237): after editing a `.slang` surface shader `just canvas-shaders <folder>`, commit its `.spv`,
  `.layout.json` and `.mfspvlock`; defaults are literals, matrices `mul(v, M)`; no `#line`, so no slangc `-obfuscate` crash.
- Benchmarks: compare only on the machine that recorded `baseline.json` (MacBook, Apple M5), and only when it is
  quiet (parallel builds/agents skew results by 10–15 %).
- Steam: Facepunch.Steamworks 2.4.1 (vendored) behind the `MainframeEngine.Steamworks` shim (Win64/Posix builds,
  `SteamworksLoader` picks this OS's); Valve's `steam_api` natives ship in `runtimes/`.

## Next steps

Priorities, not a feature list: what remains inside each milestone is its rows in `docs/milestones.md`.

1. The Forest frame budget and the owner's sign-offs (G8e): a quiet-machine `forest-bench` at 1440p and an RTX 3060
   run, then GPU culling for trees (`docs/design/forest.md#visual-quality-g8e`).
2. The gameplay toolkit's open rows (`docs/milestones.md#gameplay-toolkit-`), owner-pending ones first.
3. Re-record `Tests/MainframeEngine.Benchmarks/baseline.json` on a quiet machine (`just bench` prints `NEW` for each
   benchmark not in it yet).
4. M11 — backend-neutral GPU API + D3D12 + WebGPU (`docs/design/future/rendering-backend-abstraction.md`).
5. M12 — mobile core, Android + iOS (`docs/design/future/mobile.md`, ADR 0100; spikes M12.0 first; M12.6's AOT work
   builds on G19 phase 4's NativeAOT exports, ADR 0259); follow the
   mobile-ready plumbing checklist in `memory/context/lane-agent-brief.md` in any lane meanwhile.
6. M13 — mobile platform services (`docs/design/future/mobile-services.md`).
7. User actions: code signing/notarization for the macOS editor build.
