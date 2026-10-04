# Progress log — M0→M10 autonomous build

Plan: [m0-m10-plan.md](m0-m10-plan.md). Branch: `feature/m0-m10`. Final PR → `main` (rebase-merge).

## ▶ Resume here

- **Current wave:** W3/W4 — integrated: M0,M1,M2,M3a,M3 (all), M5 (scaffold+replication),M6,M4,M7,M8,M9,sky-fix,natives,ci-fix; publish.yml + release/NuGet docs written. Running: `lane/m10a` (editor E1–E3), `lane/m10b` (GameHost/project.mfproj/ILogSink/EditorLink/GameAssemblyLoader/templates), `lane/sky-fix` (MoltenVK vs lavapipe sky-ground discrepancy + lavapipe goldens).
- **Next action:** integrate finished lanes (rebase onto feature, run gates); then M4 (after m3b), M9 (after m8), M10 (after all); publish.yml; distribution-nuget.md; final QA.
- **Open blockers:** none

## Log

### 2026-10-05 — Step 0 governance
- main rewritten linear: merge `7d68bc5` → squash `7b13dbe` (tree identical). GDW90 must `git fetch && git reset --hard origin/main`.
- Repo: merge commits disabled, squash+rebase allowed, auto-delete branches.
- Ruleset `main-protection` (id 24456416): PR required, `ci-success` required (strict), linear history, no force-push/delete, no bypass actors.
- Ruleset `release-tags-immutable` (id 24456418): `v*` tags cannot be updated/deleted.
- Environment `release`: deploy branch policy `main` only. **Required reviewers unavailable on Team plan for private repos** → publish gated by `github.actor == 'brogan89'` check in workflow instead.
- Branch `feature/m0-m10` created from `7b13dbe`.

### 2026-10-05 — Step 1 infrastructure (lane B)
- Commits: `adc0895` zero-warning build on shared props + CPM (+ engine test hooks, alloc fixes) ·
  `c3b3bae` shader script + `shaders.lock` (all .spv recompiled for vulkan1.2) · `fe7babf` unit/render
  tests + benchmarks · `704eea2` justfile + CI · `d44cc0d` docs.
- Warnings 427 → **0** (Debug and Release, also `-warnaserror`); `dotnet format --verify-no-changes` clean.
  Analyzer suppressions: CA1816 (no finalizers by design), CA1051 structs only, CA1515 in Tests/.
- Silk.NET unified on 2.22.0; Sandbox verified on MoltenVK (Apple M5) via `just qa` (122 fps, Release).
- Tests: 55 unit (pass) · 8 render on MoltenVK (pass): validation 0/0 incl. teardown, allocation gate
  **0 B / 300 frames** (no skipped assertion), determinism, `moltenvk` goldens recorded and inspected.
- Engine API added: `EngineOptions.{VSync, EnableValidation, EnableFrameCapture, WindowVisible, MaxFrames,
  FixedDeltaTime}`, `Engine.{CaptureFrame, OnFrameCaptured, RenderedFrameCount}`,
  `IRenderer.{RequestCapture, TryTakeCapture}`, `FrameCapture`, `Png`, `IVulkanContext.Validation`
  (`VulkanValidationLog`), `VulkanException`, `ShadowSystem.RenderShadows<TState>` (+ `ShadowDraw2D`/
  `ShadowDrawPoint` delegates), `LightEnvironment.UboSize/WriteUbo` (internal).
- Bugs fixed on the way: GLFW segfault centring a window with no monitor (sleeping display);
  Box3d/Quad destroyed in-use buffers (validation errors at shutdown); `NetBufferReader(span, length)`
  ignored `length`; `drawPoint` got 2D pipelines; SteamLobby nullable derefs.
- Render tests run the scene in `Tests/MainframeEngine.RenderTests.Host` (child process): Cocoa needs
  the main thread on macOS.
- **Risks / to verify on first CI run:** lavapipe goldens don't exist yet (tests skip the golden
  compare; download the `render-tests` artifact and commit frames to `Goldens/lavapipe/`); apt names
  (`glslc`, `spirv-tools`, `vulkan-validationlayers`, `mesa-vulkan-drivers`) and ICD path
  `/usr/share/vulkan/icd.d/lvp_icd.x86_64.json` unverified (Docker unavailable locally); lavapipe may
  raise validation warnings MoltenVK doesn't; checkouts need Git LFS (content + goldens).
- Local macOS caveat: with the display asleep GLFW sees no monitor and presentation stalls — run
  render tests/QA with the display awake (`caffeinate -u`).
- Known, left for M1: ImGui window clipped on HiDPI (`DisplayFramebufferScale`), shadow VP UBO overwrite.
- xUnit v3 4.x defaults to Microsoft.Testing.Platform; test projects opt out
  (`UseMicrosoftTestingPlatformRunner`/`IsTestingPlatformApplication` = false) to keep VSTest + coverlet.


### 2026-10-05 — Natives lane (integrated)
- `0a3…`→ cherry-picked as `Natives:` commits (ENet 2.4.8 universal dylib from source, `mfrmlui` flat C ABI shim over RmlUi 6.3 + FreeType 2.14.3 — 121 exports, in-house; PourrezJ shim MIT but unsuitable ABI). ADRs 0001/0002. `natives.yml` builds win/linux/macos.
- TODO: download `natives-all` artifact from first natives.yml run, commit win-x64/linux-x64 binaries + updated `Native/natives.lock`.
- TODO (lane C): exclude ENet-CSharp package's own x86_64 natives so ours from `runtimes/` load on osx-arm64.

### 2026-10-05 — Integration
- CI checkout now inits only `Plugins/Spine` (native submodules are natives.yml's job).
- Pushed `feature/m0-m10`; draft PR https://github.com/Mainframe-Games/mainframe-engine/pull/4 (Closes #1, #2). Auto-fix CI monitor enabled.

### 2026-10-05 — Lane C M5 scaffold (integrated)
- 4 `M5:` commits: our ENet natives replace package's x86_64 (`ExcludeAssets`), flat-copied from `runtimes/`; message layer (7-byte header, `MessageRegistry` fingerprint, `MessageBus`, `ITransport`: Enet/Loopback/SteamSockets stub), pooled span buffers (0 alloc); `Steam` service (TryInitialize/RunCallbacks/Shutdown, never throws).
- Tests 107/107. Benchmarks added to baseline.json.
- **Known limitation:** Steamworks.NET 2024.8.0 ships no steam_api natives and x64-only managed assemblies → Steam disabled on arm64 (`UnsupportedPlatform`) and everywhere until SDK natives added (needs partner login — user action).
- Follow-ups for orchestrator: ADR 0003 (flat native copy for project refs), ADR 0004 (Steam unsupported arm64); milestones.md M5 rows; build-and-platforms matrix; Engine hook for Steam.TryInitialize/RunCallbacks/Shutdown (do in M2 when servers land); README networking claims.
- First CI run: render-tests failed (ICD is `lvp_icd.json`), natives linux failed (GCC -Wshadow) → fixed in f829d62.

### 2026-10-05 — Lane A M0+M1 (integrated)
- `M0:` SDL2 switch — needed `Silk.NET.Windowing.Sdl` + `Silk.NET.Input.Sdl` 2.22.0 (SDL backends of existing Silk family; design assumed transitive — not true on net10). ADR 0003. **Flag to user in final report** (dependency rule).
- `M1:` frame slots, per-pass shadow matrices (fixes #2 multi-light), fallback shadow set (Spine w/o ShadowSystem), depth remap removed, Quit deferred, capture chaining, depth aspects, Spine fixes, ImGui HiDPI. ADR 0004 (reversed-Z deferred, validation defaults).
- Gates locally after integration: unit 130/130, render 12/12 (MoltenVK), 0 validation, 0 B/frame, ~121 fps Release with 5 shadowed lights.
- natives: win/linux binaries committed from natives.yml run 37210952838 (5d58ded).
- Next ADR number: 0005 (M5 follow-ups: flat native copy for project refs; Steam unsupported on arm64).

### 2026-10-05 — Lane M2 (integrated, fast-forward)
- `M2:` generator project `MainframeEngine.Generators`, node tree/SceneTree/servers/signals/transforms, `.mscene`/`.mres`, `PackedScene`, UID cache, `AssetDatabase`; Sandbox loads `Content/Scenes/Sandbox.mscene`. ADRs 0010–0012 (Godot names, JSON scenes, generator registry). 257 unit tests, 12 render tests, 0 B/tick @10k nodes.
- API: `Node.Initialize` gone → `Tree.Servers`/`Engine.Servers`; `Camera3D`/`Camera2D` are nodes (math cams → `PerspectiveCamera`/`OrthographicCamera`); GPU init in `InitializeRenderResources(RenderServer)`; lifecycle methods protected; `SteamServer` when `EngineOptions.SteamAppId` set.
- Next free ADR: 0005 (0005–0009 free; M2 used 0010–0012) → then 0013.

### 2026-10-05 — Lane M3a (integrated via integrate/m3a, ff to d80c237)
- `M3:` ContentPaths, in-house GpuAllocator/UploadQueue/DeletionQueue (GpuBuffer/GpuImage/GpuTexture), build-time shader compile (falls back to committed .spv; CI uses -p:CompileShaders=false), limits.json single source, PipelineCache (disk) + ShaderModuleCache, shared set 0/1, HDR RGBA16F + ACES (exposure 1.3), linear lighting, Spine PMA fix, RenderTarget abstraction. ADRs 0005–0007.
- Deviation: swapchain UNORM by default (MoltenVK sRGB swapchain lost ImGui layer intermittently); sRGB opt-in via MAINFRAME_SWAPCHAIN_ENCODING.
- Gates after integration: unit 344, render 14, 0 validation, 0 B/frame, 121 fps.
- Left for m3b: ShapeBase/Box3d/Quad raw buffers + own sets → Material/Mesh/MeshInstance3D; state-hash pipeline cache; AssetDatabase via ContentPaths; Assimp import; object-ID target.
- Free ADR numbers: 0008, 0009, 0013–0019 (m6 0020s, m7 0030s, m5-repl 0040s).

### 2026-10-05 — CI fix lane (integrated)
- Root cause Linux SDL: Silk 2.22 DefaultPathResolver uses distro RID `ubuntu.24.04-x64`, no fallback → never probes runtimes/linux-x64/native. Fix `SilkNativeResolver` (Engine ctor; call `Install()` before any Silk native use without an Engine). Scene/resource JSON forced LF. `workflow_dispatch` on ci.yml (run CI on any branch: `gh workflow run ci.yml --ref <branch>`).
- CI run 37218611763 (8d7996f): all jobs green except lavapipe goldens (pre-HDR) → sky-fix lane found MoltenVK renders sky-below-horizon near-black vs lavapipe grey → investigating before recording goldens.
- Weekly usage 23% at this point.

### 2026-10-05 — Lane M5 replication (integrated, 6ca4570)
- `[Replicated]`/`[Rpc]` generator (ReplicationEmitter, MFG007–009), `MultiplayerApi` (30 Hz, spawn/despawn by PackedScene UID, per-node-acked delta snapshots, interpolation, RPC authority, hostile-client containment, kick/timeouts), `SimulatedTransport`, `TransportSelector` fallback, Sandbox `--server`/`--client`. ADRs 0040–0044. Review: 10 findings fixed.
- Gates after integration: unit 442 (+1 skipped: explicit IPv4 bind on macOS), render 14, 0 B alloc.
- Known: SteamSocketsTransport stub, Steam avatars not implemented (no Steam natives — user action); no client prediction.

### 2026-10-05 — publish workflow (47c3413)
- `publish.yml` (guard main+brogan89 → reuse ci.yml via workflow_call → matrix publish editor osx-arm64/win-x64/linux-x64 → `gh release create vX.Y.Z`), `build/next-version.sh`, `build/package-editor.sh`, `just publish-local`/`next-version`, `docs/design/release.md`, `docs/design/future/distribution-nuget.md`. Editor project path assumed `MainframeEngine.Editor/MainframeEngine.Editor.csproj` (M10 must create it with that name, exe `MainframeEngine.Editor`).

### 2026-10-05 — Lane M7 audio (integrated)
- AudioServer (SoundFlow 1.4.1, null-device fallback), buses (.mres layout), lock-free command ring, AudioPlayer/2D/3D/Listener3D, WAV/OGG(NVorbis)/MP3/FLAC, engine panning (no SurroundPlayer — ADR 0031), own allocation-free reverb, `--qa-audio` verified on MacBook speakers. ADRs 0030–0035. Review findings fixed.
- Gates after integration: unit 539 (+1 skip), render 14, format clean.

### 2026-10-05 — Lane M6 physics (integrated via integrate/m6)
- Jitter2 3D + Box2D.NET 2D servers, bodies/shapes, layers (Godot rule, ADR 0021), queued signals, queries, MoveAndSlide, interpolation (ADR 0024), debug lines (rebuilt on shared set 0/allocator during integration), opt-in `Deterministic`. ADRs 0020–0025.
- Flakes fixed at root: in-process transports used shared ArrayPool (cross-test theft) → private pool; sandbox alloc gate JIT tiering → host runs `DOTNET_TieredCompilation=0`.
- Gates: unit 615 (+1 skip) ×8 runs, render 17 ×3, 0 validation, 0 B, 121 fps.
- **TODO:** `just bench` prints "No regressions" when nothing ran (duplicate projects under .claude/worktrees confuse BenchmarkDotNet) → must fail when 0 benchmarks ran + exclude worktrees. Physics bodies not replicated yet (documented).
- Lanes done awaiting integration: sky-fix (grid line clipping in shader — lavapipe mis-rasterized huge off-screen lines; ground colour recalibrated; lavapipe+moltenvk goldens re-recorded; CI run 37222465099 green), m3b (meshes/materials/textures/MeshInstance3D, Box3d/Quad removed with RemovedNodeTypes upgrade, Assimp, picking, SubViewport; ADRs 0013–0019), m8 (RmlUi binding/renderer/UiServer/widgets/HUD; ADRs 0050–0053).

### 2026-10-05 — W4 integration (sky-fix + M3b + M8)
- 35 commits via integrate/w4: Box3d/Quad users (NetBox, physics crates, test scenes) migrated to MeshInstance3D; UiServer registered last (shuts down first); ImGui = F12 dev overlay (network/physics/audio panels), HUD has physics debug toggle. Flaky unit alloc gates → min of 3 windows. Bench harness fixed (builds known project, fails on 0 results).
- Gates: unit 790 ×3, render 32 (MoltenVK), 0 validation, 0 B, 124–127 fps.
- Bench: 4 benchmarks 10–14% slower but equally slow pre-integration under machine load (parallel agents) → **re-record baseline on quiet machine at the end**; SceneSaveLoadRoundTrip1k +7.9 KB alloc also pre-existing → investigate at end.
- TODO: lavapipe goldens for new scenes (physics, materials, gltf, instances, picking, ui-*, sky-grid) from CI.

### 2026-10-05 — M9 localization (integrated via integrate/m9)
- Tr API (zero-alloc), .mo catalogs + fallback chain, `[Export(Translatable)]`, `mf-l10n` (extract/pseudo/compile; .mo byte-identical to msgfmt), Localization.targets, RmlUi wiring (Translator → Tr.TranslateMarkup, no-tr prepass, font fallback + reload on LocaleChanged, `{{ }}` templates translated once). ADRs 0060–0066. Sandbox es/qps. Gates: unit 906 ×3, render 32, 124 fps.
- M4 done on lane/m4 (CSM, PCF, atlas 15→6 samplers, per-light settings, cutout shadows, scene→tonemap barrier fix). ADRs 0070–0074.

### 2026-10-05 — M4 Shadows v2 (integrated via integrate/m4)
- CSM, PCF, atlas, per-light settings, culled + cutout casters; explicit `RenderTarget.End` barriers for every RT pass (MoltenVK ignores render-pass deps). Gates: unit 968 ×3, render 39 ×2, 0 validation (42 scenes), 0 B, 124 fps.
- Issue #2 fully addressed (multi-light shadows) → PR says Closes #2.
- Logo: user chose C3 "Circuit" (see Claude memory brand-logo-c3); m10a lane commits assets to docs/images/brand/, editor splash, window/app icons, README title.

### User request: migrate to .slnx (do after m10a/m10b/ci-final land, before final PR)
- `dotnet sln MainframeEngine.sln migrate` → `MainframeEngine.slnx`, delete .sln; update refs: .github/workflows/ci.yml, justfile (`solution`), CLAUDE.md, README.md, docs/design/build-and-platforms.md, Tests ShaderLimitsTests.cs + ModelImportTests.cs (repo-root probe), plus anything new (publish.yml, template CI job, m10 docs). Verify `dotnet build/test/format` on .slnx, CI green.

### CI follow-ups (do in final CI pass)
- 10k-instance 60 fps assertion fails on lavapipe (51 ms CPU raster) → on CPU-type devices assert CPU frame-build time only.
- Tests without lavapipe goldens skip ENTIRELY ([1 ms]) → must still run validation/alloc gates; only golden compare skipped. Then record lavapipe goldens for all scenes from CI and inspect.
- Re-record benchmark baseline on a quiet machine; investigate SceneSaveLoadRoundTrip1k +7.9 KB.
