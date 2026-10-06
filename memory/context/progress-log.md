# Progress log — M0→M10 autonomous build

Plan: [m0-m10-plan.md](m0-m10-plan.md). Branch: `feature/m0-m10`. Final PR → `main` (rebase-merge).

## ▶ Resume here

- **Status:** M0–M10 merged to `main` (fast-forward to `ec6bd70`, 2026-10-05).
- **Next action:** publish v1.0.0 (`publish.yml`); public-release prep on `chore/public-readiness` (licence notices,
  README License section, CI LFS caching). Then M11 (backend abstraction), M12/M13 (mobile).
- **Open blockers:** none. User actions: Steamworks natives (partner login); macOS code signing/notarization.

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

### 2026-10-05 — Integrated: M10b, CI-final, fixed-scale render tests, M10a editor, mobile docs
- M10b: ILogSink, InputMap, ProjectSettings/project.mfproj (+ l10n/shadow-quality/audio/physics applied), EditorLink, GameAssemblyLoader (unload leaks fixed incl. Tr/UI bindings), GameHost, `mfgame` template + CI `template` job. ADRs 0090–0095.
- CI-final: missing goldens skip only the compare; CPU frame time (`Engine.LastFrameCpuMilliseconds`); all lavapipe goldens recorded.
- `EngineOptions.ContentScale` → render tests fixed pixel size on any display (host `--scale`, mac 2 / else 1).
- M10a editor (E1–E3) + C3 brand/icons (Dock icon ABGR fix, macOS icon grid), splash, README logo; editor Output = ILogSink; project.mfproj discovery; SubViewport shadows. ADRs 0080–0084. CI green run 37238375239.
- Gates: engine 1162 + editor 155 tests ×3, render 43 ×2, all checks clean; editor 8.3 ms avg frame.
- Mobile design: docs/design/future/mobile.md (M12), mobile-services.md (M13), ADR 0100 (amends 0011 for exports), plumbing checklist in lane brief.

### 2026-10-05 — v1.0.0 + lock step; M10c-icons integrated (local only)
- a631d0d: first release v1.0.0; engine/editor/generator share one version (verified 1.0.0+sha in all three); editor startup lock-step check.
- M10c-icons (c5940a0..b716e3e): [EditorIcon] + families + doc summaries via generator, Tabler atlas (164 icons, `just editor-icons`), tooltips, icons across tree/inspector/toolbar/menus/output/tabs/dialogs, Godot-style create dialog. ADRs 0085–0089. Gates: 1170 engine + 243 editor tests, render 43.
- Lavapipe goldens to record at the final push: editor_frame0051 (+ any from m10c-projects).
- Still running: m10c-projects (told to rebase onto local feature + label-column polish).

### 2026-10-05 — Final: .slnx, Linux in Docker, gates, benchmarks, docs (local only)
- Commits: `9b1b971` .slnx migration (sln deleted; CI/justfile/docs/tests/editor text updated) · `df7add7` Docker Linux
  test env (`build/linux/`, `just test-linux`, `just render-tests-linux`, testing.md) · `ccfaf21` Linux/CI-only test
  fixes · `3812cb8` lavapipe goldens (editor_frame0051, editor-project-manager_frame0040, editor-filesystem_frame0040;
  recorded in Docker, inspected vs moltenvk) · `49f119c` Node modes as bytes (Node 240→232 B) · `d330400` bench baseline ·
  `5f6d90d` docs sync.
- Docker (x86_64 ubuntu:24.04 under Rosetta, llvmpipe LLVM 20.1.2 / Mesa 25.2.8): unit 1169 (+2 skip), editor 433
  (+1 skip), l10n OK, render 45/45 with every frame compared. Rosetta gotchas: `DOTNET_EnableWriteXorExecute=0`;
  occasional whole-process test stall → blame + one retry in inside.sh.
- Docker found 4 bugs CI would have hit: editor Output source-link test vs CI path mapping (`/_/`; Tests props now
  `DeterministicSourcePaths=false`); ProjectCreation test building without glslc (`-p:CompileShaders=false`); Sandbox
  alloc gate 78.5 B/frame on x64 CI builds (RendererDebugWindow formatted enums per frame → cached line); editor idle
  gate 216 B/100 frames (tier-0 `AppendFormatted<int>` boxing → int.TryFormat).
- Local gates (final tree): build 0 warnings; Release -warnaserror 0; `just test` ×3: 1170 (+1 skip) + 433 (+1 skip);
  `just test-render` ×3: 45/45 (validation 0, alloc 0 B, all goldens compared); format/shaders (69)/l10n clean;
  template-smoke OK; `just qa` Sandbox 120 fps (8.3 ms); `qa-projects` create→play→reload OK (build+launch 7.1 s, reload
  0.81 s, editor 120 fps / 8.3 ms); `publish-local osx-arm64` → 57 MB tar.gz, packaged app opens the Project Manager
  (v0.0.0-local).
- Bench: old baseline flagged Node3DModelMatrix +14 %, TransformPropagation10k +12 %, RoundTrip1k +15.9 KB, SwitchLocale
  +312 B. Bisected the transform ones to M9's Node field (layout; M2 padded to 240 B reproduces; hooks not the cause);
  RoundTrip = Node size (now 232 B); SwitchLocale = listener isolation. Baseline re-recorded 2026-10-05.
- Docs: README (features, screenshots, getting started, testing/CI, release), CLAUDE.md, milestones (M0–M10 ✅), docs
  index, architecture overview, current-state.
- Follow-up chip: moltenvk/lavapipe editor goldens embed the machine temp path (/var/folders/... on this Mac).


### 2026-10-05 — Portable editor goldens
- Editor golden runs use `GoldenPaths.Root` = `/tmp/mainframe-golden` (was per-user macOS `/var/folders/…/T/`); ProjectService shows the path as opened (MSBuild still gets the real path). moltenvk editor goldens re-recorded; lavapipe unchanged.

### 2026-10-05 — Merged; public-release prep
- `feature/m0-m10` fast-forwarded into `main` (`ec6bd70`). The "no pushes" rule (Actions minutes nearly exhausted;
  commit locally, one final push) applied during the M0–M10 build and is lifted.
- `chore/public-readiness`: THIRD_PARTY_NOTICES for Spine example assets (not MIT), the Poly Haven sky (CC0), the
  Silk.NET/LearnOpenGL/Vulkan Tutorial origins of `Examples/SilkVulkanExamples`; README License section; unused
  `sky_16_2k.png` removed; CI jobs fetch only needed LFS files with an `.git/lfs` cache.

### 2026-10-05 — Scene format 2 (branch `scene-format-2`)
- ADR 0102: flat `"nodes"` list with `parent` paths; stable inline resource keys `Type_xxxxx` (`Resource.SceneLocalId`,
  new keys hashed from their first use site, so saves are deterministic); `SceneJsonLayout` keeps arrays of ≤16
  scalars and `{ "res": … }` on one line. Format 1 still loads (re-keyed on save). Editor `FilePeek`/`ReferenceFixer`
  and the `mf-l10n` scene extractor read both layouts. Sandbox, NetBox, template and EditorShowcase files re-saved
  (Sandbox.mscene 856 → 570 lines).
- Gates: build 0 warnings, Release -warnaserror, unit 1185 (+1 skip) + editor 443 (+1 skip), format-check,
  render 45/45, template-smoke OK.
### 2026-10-05 — Crash Site Defense port: 2D is Y-down (ADR 0110)
- Branch `mainframe-engine-port` (the game repo's `engine/` submodule; Brogan: push directly, no PRs). E1 of the port
  (`crash-site-defense/docs/porting.md`): `PhysicsSettings2D.Gravity` +980, `CharacterBody2D.UpDirection` −Y,
  `Camera2D`/editor 2D view look along +Z with up −Y (rotation, not mirror), editor screen↔world, grid (z = +1),
  gizmo axes. 2D physics/editor tests mirrored (y → −y). Gates: build, Release, unit 1170 + editor 443, render 45/45,
  format.

### 2026-10-05 — 2D canvas renderer (ADR 0111, port E2/E3)
- `CanvasItem` (base of `Node2D`), `Sprite2D`, `CanvasLayer`, `CanvasModulate`, `Canvas` (viewport root canvas / layer),
  `Rect2` (serializable), `CanvasDrawList` + `CanvasPrimitives` (Godot's tessellation and `Triangulate`),
  `CanvasCuller` (port of `_cull_canvas_item`), `CanvasServer` (frame server: redraws, cull, `CanvasFrame` batches),
  `VulkanCanvasRenderer` (RGBA8 gamma-space layer, Godot blend states, composite after the tonemap below the UI),
  `CanvasItemMaterial`, `Shader`/`ShaderMaterial` stubs (E5). Shaders `Canvas/Canvas.vk.{vert,frag}`, `include/canvas.glsl`.
- Tests: `Canvas/CanvasTests` (order, z, y-sort, modulate, culling, top level, redraws, frame), `CanvasPrimitivesTests`;
  render test `canvas` (+ moltenvk golden; lavapipe golden still to record). Gates: build, Release, unit 1184 + editor
  443, render 46/46, format, shaders.
- Icons: used existing atlas names (`stack-2`, `contrast`, `brush`) — new Tabler names need `just editor-icons-fetch`.

### 2026-10-05 — Camera2D + content scale (port E9)
- `Camera2D` rewritten as a port of Godot 4.7's (anchor, Godot zoom, offset, limits, drag, smoothing incl. the zoom/offset
  re-scroll that keeps the smoothed position); it writes `SceneViewport.CanvasTransform`. 2D cameras become active only
  when made current (`Enabled` cameras make themselves current on entering a viewport without one).
- `ContentScale.Compute` = `Window::_update_viewport_size`; root `SceneViewport.SetSize` each frame (engine + canvas
  server), `GetVisibleRect`, `StretchTransform`; project `window.stretchMode/Aspect/Scale/ScaleMode` (Godot spellings
  accepted) and `rendering.canvasClearColor` applied by `GameSession.Start`. Canvas golden re-recorded with a 480×270
  canvas_items stretch. Tests: Camera2DTests, CanvasProjectSettingsTests. Gates green (unit 1190, editor 443, render 46).

### 2026-10-05 — SVG via Godot's ThorVG: mfsvg (ADR 0112, port E8)
- `Native/Svg`: ThorVG 1.0.3 copied from Godot 4.7.2 `thirdparty/thorvg` (PNG loader off), C ABI `mfsvg_*` mirroring
  `ImageLoaderSVG`; smoke test. macOS universal built locally; Linux (Ubuntu 22.04 GCC) and Windows (mingw-w64, static,
  interim until natives.yml/MSVC) built in Docker (amd64 emulation). `natives-lock.sh`/`build.sh`/`natives.yml` know mfsvg.
- **Pending:** `natives-lock.sh verify` now fails for enet/mfrmlui (the shared `Native/CMakeLists.txt` input changed); run
  natives.yml on the branch to rebuild every native (MSVC mfsvg.dll) and refresh the lock.
- C#: `Svg.Rasterize/Size/PixelSize` (LibraryImport `mfsvg`), `ImageOps.FixAlphaEdges` (Godot's `fix_alpha_edges`), `.svg`
  in `TextureImporter`, `TextureImportSettings.SvgScale/FixAlphaBorder` (meta `svgScale`, `fixAlphaBorder`).
- Verified: every Crash Site Defense SVG rasterised + fixed is byte-identical to Godot's imported `.ctex` (lossless WebP),
  the emblem 13 bytes off by 1. Tests: Imaging/SvgTests. Gates green (unit 1193, editor 443, render 46).

### 2026-10-05 — Canvas shaders in Godot's shading language (ADR 0113, port E5)
- `CanvasShaderCompiler` (Godot canvas_item → GLSL: std140 block + sampler bindings, defaults, hints, render modes,
  varyings, vertex()/fragment(), built-ins), `Shader` resource (`.gdshader` importer, `Shader.Load/FromProgram`,
  `WriteUniformBlock`), `ShaderMaterial` (parameters by name), `CanvasShaderBuild` + tool `mf-shaders`
  (`Tools/MainframeEngine.ShaderBuild`; `just canvas-shaders[-check]`): SPIR-V + `.spvlock` committed next to the source.
- Renderer: per-shader set/pipeline layouts and pipelines, a per-frame material UBO ring (dynamic offsets, sized up front),
  material sets rebuilt on texture change, sampler-uniform textures uploaded in the frame step. `Texture2D.SetPixels`.
- All seven Crash Site Defense shaders translate and compile unchanged. Render test `canvas` gained a shaded sprite
  (Content/Shaders/wave.gdshader). Tests: CanvasShaderCompilerTests, Texture2DPixelsTests. Gates green (unit 1198,
  editor 443, render 46, shaders, canvas shaders).

### 2026-10-05 — Game value types in scenes/resources ([SerializableValue])
- `[SerializableValue]` structs may be `[Export]` types; the generator emits `Codecs.Deferred<T>()` (resolved at use, so
  the game can register codecs in any module initializer); `Codecs.FloatArray<T>` builds array codecs. `.svg` assets get
  `tex_` UIDs. Test: Scene/SerializableValueTests. Gates green (unit 1199, editor 443; no render change).

### 2026-10-05 — Sound files through ResourceLoader (port G3)
- `AudioImporter` (`.wav/.ogg/.mp3/.flac` → `AudioStream.Load` with the `.meta` settings) registered in `AssetImporters`,
  so `.mres` resources can reference sound files as imported assets (Crash Site Defense's SoundDefs). Test: the
  AudioDecoderTests import case loads through `ResourceLoader`. Gates green (unit 1199, editor 443; no render change).

### 2026-10-05 — Godot runtime rules for the port's first world render (ADR 0114)
- Process delta minus dropped physics time (Godot's rule; first-frame stalls no longer reach Camera2D smoothing);
  offline `IsServer`; `++` user args (`GameHost.UserArgs`); `SceneTree.Quit` / `SceneTree.CaptureFrame` +
  `--frame-capture`; `ProjectSettings.Version` + `GameHost.Project`; `Node2D.Skew` / `Transform2D.Skew` with exact
  transform storage; shader build outputs skipped by the asset scan. Tests: SceneTreeHostTests, skew, tick delta,
  offline server, user args, version, scan skip. Gates green (unit 1203, editor 443, render 46).

### 2026-10-05 — window.contentScale (port E17)
- Project setting `window.contentScale` → `EngineOptions.ContentScale` (1 = window size in pixels, Godot's rule). With
  it Crash Site Defense's seed-4242 frame matches Godot's within 1/255 per pixel outside the not-yet-ported crew member.

### 2026-10-06 — D2 Remove ImGui (ADR 0115)
- RmlUi `DevOverlay` (F12, built-in panels, `AddPanel`) and Vulkan `ScreenGizmos` (light + axis gizmos) replace ImGui;
  ImGui.NET, `cimgui`, `VulkanImGuiController`, `OnImGui` and the ImGui texture registry are gone. Overlay hidden by
  default (render tests opt in); goldens re-recorded. Docs: `docs/design/dev-overlay.md` (replaces imgui-and-debug-tools).

### 2026-10-06 — Spine 4.3, canvas SpineSprite, 2D sub-viewports (ADR 0116, port E11/E7)
- `Plugins/Spine` → fork branch `4.3` (upstream spine-runtimes 4.3 @ 9e09846); SpineBoy test/Demo assets re-exported 4.3.
- `SpineGeometry` (shared walker), `SpineSprite` + `SpineSkeletonDataResource` + `SpineAnimationMix`; `SpineRenderer`
  on the walker (4.3 API: applied poses, sequences, `Spine.Physics`).
- `SubViewport.Disable3D/TransparentBg/GetTexture()`, `Texture2D.FromViewport`; `CanvasFrame` passes; the renderer
  draws sub-viewport passes into their own targets before the main layer. Canvas render test gained SpineBoy in a 2D
  sub-viewport; canvas + Spine goldens re-recorded (moltenvk, lavapipe). Tests: SpineSpriteTests.

### 2026-10-06 — CharacterBody2D floating motion mode (port E12)
- `CharacterBody2D.MotionMode` Grounded/Floating + `WallMinSlideAngleDegrees` (15°): Godot's `_move_and_slide_floating`
  (move_and_collide per iteration with recovery as collision, every hit a wall, first slide keeps the remaining length,
  velocity untouched). Test: FloatingCharacterSlidesAlongWallsKeepsItsVelocityAndStopsHeadOn.

### 2026-10-06 — Scene-node replication: MultiplayerApi.Bind + MultiplayerSynchronizer (port E13)
- `MultiplayerApi.Bind(node)` (server) networks a node every peer already has (scene index −1 in the spawn message;
  clients resolve parent path + name instead of instantiating; late joiners and despawn as for spawns).
  `MultiplayerSynchronizer` (Godot's name, `RootPath`) binds its root when the server starts or at once; inside a spawned
  scene it does nothing. Tests: BindTests.

### 2026-10-06 — Spawn configure callback, SendToServer (port G5)
- `MultiplayerApi.Spawn(scene, parent, authority, configure)` / `Spawn<T>(…, configure)`: the callback runs before the
  instance enters the tree (Godot's spawn function), so `OnReady` sees the spawn data. `SendToServer<T>` sends a game
  message from a client (the server's transport peer is private). Tests: SpawnConfigureTests, SendToServerTests.

### 2026-10-06 — Tween (port E10)
- `Tween` + Property/Interval/Callback/Method tweeners, a port of Godot 4.7's `Tween::step` and easing_equations.h;
  `Node.CreateTween` (bound) / `SceneTree.CreateTween`, stepped after timers. Tests: TweenTests.

### 2026-10-06 — MultiplayerSpawner with spawn data; array RPC parameters (port E13)
- `MultiplayerSpawner` + `SpawnData` (Godot's spawn function + data dictionary; payload in the spawn message, late joiners
  included); RPC parameters `byte[]/int[]/float[]/string[]` (generator + `NetCodec`). Tests: MultiplayerSpawnerTests.

### 2026-10-06 — GpuParticles2D (CPU) for ported weather effects
- `GpuParticles2D` + `ParticleProcessMaterial` subset, simulated on the CPU and drawn through the canvas. Tests:
  GpuParticles2DTests.

### 2026-10-06 — 2D point lights + gradient textures (port E6, ADR 0117)
- `PointLight2D` with Godot's canvas light formula after the CanvasModulate; ≤ 8 lights per frame in a per-frame-slot
  UBO + sampler array (set 1 default / set 2 shader layouts), a light bitmask per batch (push block 112 bytes; canvas
  shaders rebuilt). `Gradient` + `GradientTexture2D` (Texture2D unsealed for generated textures). The canvas render
  scene gained a light (golden re-recorded on moltenvk). Tests: PointLight2DTests, GradientTests.

### 2026-10-06 — Canvas text (port E4, ADR 0118)
- `Font` (.ttf importer) + `CanvasItem.DrawString/DrawStringOutline`; managed `TrueTypeFont` (cmap, glyf incl.
  composites, GPOS/kern kerning) and `GlyphRasterizer` (font-rs coverage; outlines = 4× supersampled disc growth by
  size/4 px). Glyph atlas pages per (size, outline). Canvas render scene gained an outlined caption (golden re-recorded).
  Tests: FontTests.

### 2026-10-06 — Mouse position and joy axes (port E15)
- `InputState.MousePosition` (window points), `Input.MousePosition`, `Input.GetJoyAxis`; `SceneViewport.PointScale` (set by
  the engine) + `GetMousePosition()` through the inverse stretch; `CanvasItem.GetGlobalMousePosition/GetLocalMousePosition`.
  Test: InputMapTests.TheMouseMapsThroughTheStretchAndCanvasTransformsAndAxesReadRaw.

### 2026-10-06 — Godot runtime rules, part 2 (port G4/E15/E17; ADR 0114 amendment)
- Deferred calls flush before process and after each physics step's callbacks (Godot's message queue); `InputEventAction`
  + `Input.ParseInputEvent`; `SceneTree.Window` (`IWindowControl`: title, fullscreen, size, position, screen bounds) and
  `CloseRequested` (user close only); `SceneTree.ProcessSeconds/PhysicsProcessSeconds`. Tests: SceneTreeTests,
  InputMapTests.

### 2026-10-06 — SVG images in the game UI (port G8)
- `VulkanUiRenderer.DecodeImage`: `.svg` sources rasterised by ThorVG at their own size with the import's alpha-edge fix
  (ADR 0112), others through StbImageSharp; the game's hotbar and forecast icons are SVG. Test: SvgTests.

### 2026-10-06 — GpuParticles2D matches Godot's process-material quirks
- The angle turns a particle in the plane only with `ParticleFlagDisableZ` (Godot's default off rotates about the 3D Y
  axis: in 2D the particle just narrows by cos(angle)); `ParticleFlagAlignY`; the colour is linearised like Godot's
  `source_color` uniform (shown as is by the 2D canvas); the phase-0 particle emits at once. Tests: GpuParticles2DTests.

### 2026-10-06 — 2D sub-viewports in the UI; material sets follow texture re-uploads (port E16)
- `UiServer.RegisterTexture(name, SubViewport)`: a 2D sub-viewport's canvas target as `engine://name` (the canvas-view
  element; the game's minimap). Fix: a canvas material's descriptor set now rebinds when a sampler texture re-uploads
  (`SetPixels`), not only when the texture object changes; it kept pointing at the old, deletion-queued view.

### 2026-10-06 — Canvas clip children (port E2, ADR 0119)
- `ClipChildren` (`AndDraw`): the culler tags a group (owner + subtree) and adds a composite entry; `CanvasServer`
  culls every canvas first (pooled `CanvasEntry` lists), emits one transparent group pass per owner before the
  viewport's pass (main or 2D sub-viewport), members other than the owner blend `Atop` (new `CanvasBlendMode.Atop`,
  premultiplied output via `CANVAS_FLAG_PREMULTIPLY`), and the composite is a premultiplied unshaded quad over the
  owner's bounds sampling `Texture2D.ForClipGroup`. `Only` draws like `AndDraw` (deviation). Tests: ClipChildrenTests
  (5), canvas render scene gains a clipped disc (moltenvk golden re-recorded).

### 2026-10-06 — Headless host (port E13, ADR 0120)
- `--headless` → `HeadlessHost`: no window/Vulkan/canvas/UI; `MultiplayerApi` + physics + null-device audio, the same
  `GameSession`, a paced loop (maxFps or the physics rate), canvas draws flushed, Ctrl+C/SIGTERM = close request.
  `GameHost.IsHeadless`. The game's `--headless ++ --server` log equals Godot's; two joiners play on it. Tests:
  HeadlessHostTests (4).

### 2026-10-06 — Title tooltips (port E16, ADR 0121)
- `UiTooltips` per document: `title` attributes shown in the document's `#tooltip` after 0.5 s at mouse + (10, 10),
  kept inside, hidden on press/leave; hover read from `RmlContext.HoverElement`. `UiServer.TooltipDelaySeconds` /
  `TooltipOffset`, `UiDocument.ShownTooltip`. Tests: UiTooltipsTests (4).

### 2026-10-06 — GameHost starts the session on the first update
- `GameSession.Start` (autoloads, main scene) moved from `GameHost.OnLoad` to the first `OnUpdate`, whose delta is
  discarded (`Engine.DiscardFrameDelta`): SDL shows the window ~0.3 s after `OnLoad`, and that gap reached the first
  scene's first frame (cut to 133 ms by the dropped-physics-steps rule), so the game's intro ran its first beat ~0.35–0.8 s
  long. Now within ~0.1 s of Godot's.

### 2026-10-06 — Godot's 2D audio panning (port E14, ADR 0122)
- `AudioPlayer2D` hears from the view centre (`CanvasTransform⁻¹ · visible/2`) and pans linearly like Godot
  (`2d_panning_strength` 0.5 → 0.5 per channel in the centre; was equal-power, 6 dB louder). `PanDistance2D` removed;
  `PanningStrength2D`, `CanvasTransform2D`, `ScreenSize2D` added. Test: AudioMathTests.

### 2026-10-06 — Godot's shadow opacity (Driving Range port E5, ADR 0123)
- `Light.ShadowOpacity` / `Light3D.ShadowOpacity` (0..1, default 1): `lights.glsl` applies `mix(1, shadow, opacity)`
  after `dirShadow`/`spotShadow` (directional `color.w`, spot `outerPad.y`; point lights ignore it). Defaults leave
  every golden unchanged. Tests: LightEnvironmentTests (packing), LightShadowSettingsTests (clamp, scene round trip),
  render `ShadowTests.ShadowOpacityLightensTheUmbraLikeGodot` + golden `shadow-opacity_frame0008` (moltenvk).

### 2026-10-06 — Game packaging (port E19)
- `build/package-game.sh` / `just package-game`: self-contained Release publish per RID, apphost renamed, foreign
  natives pruned; macOS `.app` (Info.plist from project.mfproj, `.icns` via sips/iconutil, ad-hoc codesign) zipped;
  Windows zip with the exe icon (`magick` → ApplicationIcon); Linux tar.gz (doubles as the headless server).
  Crash Site Defense: osx-arm64 109 MB, win-x64 103 MB, linux-x64 113 MB; the `.app` boots and plays seed 4242.

### 2026-10-06 — Godot 4.7's tonemap and glow (Driving Range port E4, ADR 0124)
- `PostProcessSettings` on `WorldEnvironment` (Tonemap + Glow groups, Godot 4.7 defaults) → `IVulkanContext.PostProcess`
  each frame from the root world. Default settings keep `Tonemap.vk.frag` (goldens unchanged); otherwise `GlowEffect`
  (7 level RenderTargets, raster H+V passes, `GlowBlur.vk.frag`) and `TonemapPost.vk.frag` (exposure → glow → engine or
  Godot ACES → sRGB). Tests: PostProcessSettingsTests, render `GlowTests` + golden `glow_frame0006` (moltenvk).
  SubViewports keep the engine curve (known issue). Supersedes ADR 0006's "no bloom".
