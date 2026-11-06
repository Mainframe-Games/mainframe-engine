# Current state — mainframe-engine

_Last updated: 2026-11-06 — M0–M10, Demo & polish, most 2D port work and much of the gameplay toolkit; v1.1.5. In review: `feature/g19-encrypted-packs` (G19 phase 3, PR #183)._

## What exists

- **Engine** (`MainframeEngine`, .NET 10, Vulkan 1.2; MoltenVK bundled on macOS, SDL2 windowing via Silk.NET 2.22):
  Godot-style `SceneTree` (M2) with source-generated `[Export]`/`[Signal]`/`[Replicated]`/`[Rpc]`/`[EditorIcon]`
  registration (`MainframeEngine.Generators`), `.mfscene`/`.mfres` JSON with UIDs; meshes/materials/textures, Assimp
  import, instancing, picking, `SubViewport`, HDR + ACES, in-house GPU allocator (M3); CSM/PCF/atlas shadows (M4);
  replication over ENet/loopback/simulated transports (M5); Steam over Facepunch.Steamworks 2.4.1 with Valve's
  natives shipped (ADR 0189); Jitter2 3D + Box2D.NET 2D physics (M6); SoundFlow audio with buses and 3D voices (M7);
  RmlUi 6.3 game UI through the in-house `mfrmlui` shim (M8); gettext localization with `mf-l10n` (M9);
  `project.mfproj`/`ProjectSettings`, `GameHost`, `InputMap`, `ILogSink`, editor link, collectible game-assembly
  reload, `mfgame` template (M10 engine side).
- **Editor** (`MainframeEngine.Editor`, M10, UI in RmlUi): Project Manager, New Project wizard, project settings,
  FileSystem panel, scene tabs (one `SubViewport` world each), scene tree, generated inspector (multi-select, Signals
  tab, custom inspectors), undo/redo, 3D/2D viewport + gizmos, Play (separate process over the editor link), code
  reload, Tabler icon atlas, editor settings, a loading window for projects and scenes (ADR 0238). macOS app name
  "Mainframe Editor" via a dev `.app` bundle (#118).
- **Since M10** (details in `progress-log.md`): 2D canvas renderer, `Camera2D`, canvas text, canvas shaders, SVG
  (ThorVG), 2D lights and canvas post, `Tween`, `GpuParticles2D`, `Polygon2D`/`Skeleton2D`, 2D joints (ADR 0201),
  `SceneTree.TimeScale`; headless host, game packaging + signing, NuGet packages (ADR 0188); saves and settings (G4);
  post-processing (ADR 0163) and the F12 dev overlay; gameplay recording (ADR 0190) and the editor's Video mode, now a multi-track timeline editor in
  the project (ADR 0241: media pool, viewers, Premiere's keys, Cutback's tools per clip, titles, transitions, captions); Music mode, DAW, wavetable synth and adaptive music (ADRs 0192–0196; A1–A6: the editor's Target menu, the overlay's Music panel, the Demo's Music tab); `Assets/` content folder (ADR 0197); 3D
  skeletal animation and skinned import (ADRs 0207/0208); spatial shaders (ADR 0205), written since G18 as Slang surface shaders (`.slang`, `import mainframe.spatial;`, ADR 0237; Godot-syntax `.mfshader`/`.gdshader` no longer load); 3D particles (ADR 0206); Spine
  shadows and billboards (ADR 0204); `SteamSocketsTransport` and Steam avatars (#127, #128); the Forest showcase
  (terrain, trees, water, baked GI; ADR 0209); the Neon showcase (a cyberpunk apartment, ADR 0218) with paths
  (ADR 0215; 2D paths and the curve tool, ADR 0242; moving platforms and the 3D curve tool, ADR 0243: G13 complete), sub-viewport textures on meshes (ADR 0216) and nearest-light selection (ADR 0217); root motion (G1e), keyframe tracks for any property, method and audio keys and the editor's Animation panel (G1a, ADR 0244),
  editor viewport tools (G7), joints and ragdolls (G9, #136; G9.7 limit handles, #148; G9.10a `MotorJoint2D`; G9.10b `Generic6DofJoint3D` on the engine's own
  Jitter2 constraints, ADR 0234; G9.10c active ragdolls, ADR 0235: G9 complete); the frame profiler (ADRs 0219/0220), crash reporting
  (0221/0222), `AnimatedSprite2D` (0223/0224), tile maps (0225/0226; 2D collision hits are `Node2D`), 2D navigation
  (0227/0228; grids read live from a `TileMapLayer` since G3.6; ORCA avoidance and `NavigationObstacle2D`, G3.7, ADR 0250) and game export with `mf-export` (0229/0230;
  `project.mfproj` format 5; #138); editor link protocol 6 (adaptive music); the docs site at engine.mainframe.games; editor self-update; Linux launcher; anti-aliased gizmos, SVG scene icons, an analytic grid ([Gizmos](../../docs/design/gizmos.md)); every runtime content read through `ContentFiles` (G19 phase 1, ADR 0239), and exports packed into one AES-256-encrypted `Assets.mfpak` that `GameHost` mounts with the key the generator compiles into the game (phases 2–3, ADRs 0240, 0247; `project.mfproj` format 6 `packKey`); every engine file extension starts with `.mf` (`.mfscene`, `.mfres`, `.mfmeta`, …; ADR 0249: old projects still load, the editor migrates them on open, `FileExtensionRuleTests` keeps the rule).
- **G6** ([Rendering features](../../docs/design/rendering-features.md)): PBR + IBL, SSAO, 3D particles, mesh LODs (ADR 0245), forward decals (ADR 0246), `.hdr` skies; owner-pending: PBR default, LOD simplifier. **G11** ([World-space UI](../../docs/design/world-space-ui.md)): multi-line `Label3D` with fallback fonts and `FixedSize` on the mesh batcher (ADR 0251), `UiMarker3D` rows on a `UiMarkerHost` (ADR 0254), `UiPanel3D` panels rendered before the scene with mouse/crosshair/manual input (ADRs 0252/0253); the batched text renderer, panel stand-in data in the editor view and G11.9 remain.
- **Version** v1.1.5 (git tags only; engine/editor/generator in lock step). `publish.yml` releases editor builds and
  the `MainframeEngine` + `MainframeEngine.Templates` NuGet packages.
- **Solution** `MainframeEngine.slnx` (no `.sln`). Everything local goes through `just` (see AGENTS.md).
- **Tests:** ~3 280 engine, ~1 660 editor, ~470 video and ~120 Demo unit tests; ~190 render tests (goldens `moltenvk` +
  `lavapipe`, validation gate, 0-B allocation gate); benchmarks with `baseline.json`; QA scripts (`just qa`, `qa-editor`,
  `qa-projects`, `qa-settings`). Lavapipe can be run locally in Docker: `just render-tests-linux` (x86_64 ubuntu:24.04,
  same packages as CI; macOS or Linux hosts).
- **CI:** `ci.yml` (build-test ×3 OS, format, shaders, render-tests on lavapipe, template smoke, `ci-success`),
  `publish.yml`, `natives.yml`. Jobs fetch only the LFS files they need, cached by object ID. GitHub Actions
  minutes are scarce: run things locally (Docker for Linux) first.
- **Workflow:** Mainframe workflow v1.0.0 (ADR 0233): `.agents/workflow.md`, `mf-*` skills, `check.sh` in CI.

## Known gotchas

- Inside `namespace MainframeEngine` the engine's `Animation` (ADR 0207) shadows `Spine.Animation`: qualify Spine's. Silk.NET's
  Assimp key structs do not match the bundled native library's size; read keys through `ModelImporter`'s detected stride.
- macOS: SDL and Silk.NET must bind the SAME Vulkan library — `VulkanLoaderBootstrap` enforces it; never call a bare
  `Vk.GetApi()`, take `Vk` from `IVulkanContext`. HiDPI: use `Engine.FramebufferSize` (pixels), not window sizes
  (points). Render tests, `just qa*` and the packaged editor need the display awake (`caffeinate -u -t 3000 &`).
  A rendering change re-records both golden sets (`moltenvk` needs a Mac: #157 and #164 left it stale), and scene
  self-checks must hold at the Mac's 640×480 as well as CI's 320×240. slangc depfiles name real paths (`/private/var`).
- Linux: Silk's resolver doesn't probe `runtimes/linux-x64/native` on distro RIDs — `SilkNativeResolver.Install()`
  (Engine ctor) fixes it; call it before Silk native use without an Engine.
- Per-frame GPU resources keyed by `IVulkanContext.FrameSlot`; GPU memory only via `GpuAllocator`/`GpuBuffer`/
  `GpuImage`/`GpuTexture`, uploads via `UploadQueue`, frees via `DeletionQueue` (no Queue/DeviceWaitIdle).
  End every `RenderTarget` pass with `RenderTarget.End` (MoltenVK ignores render-pass external dependencies).
  Keep the default UNORM swapchain (MoltenVK mutable-format sRGB views go stale).
- `ShadowSystem.RenderShadows` once per frame; comparison samplers are immutable (MoltenVK); shader limits only in
  `Assets/Shaders/limits.json`; after shader edits `just shaders` (commit `.spv` + `shaders.lock`).
- Node constructors cheap and side-effect free; node-declaring projects need the generator; `Timer` shadows System's.
- Physics: Jitter2/Box2D calls stay in `Src/Physics`; the 6-DOF constraints copy Jitter2 2.9.0's constraint header and
  step conventions (ADR 0234: re-check on any upgrade); Box2D worlds are process-global (`SerialBox2D` test collection);
  render tests use `PhysicsSettings3D.Deterministic`. x64 vs arm64 floats differ after contact (lavapipe vs moltenvk).
- RmlUi and `Tr` are process-global (`SerialRmlUi`, `LocalizationState` collections). `Tr` takes literals only.
- A lambda capturing a local allocates at method entry, even on an early return: per-frame code uses method groups.
- Editor code reload: editor code must not keep game nodes/types in fields or statics past
  `ReleaseEditorReferences`; never keep game objects in locals of the unloading method (GC-verified unload).
- Allocation gates: render host runs `DOTNET_TieredCompilation=0`; unit gates use `AllocationGate.SmallestWindow`;
  in-process transports use a private packet pool (never `ArrayPool.Shared` in per-frame code that tests share).
- CI builds (`CI=true` → `ContinuousIntegrationBuild`) map `[CallerFilePath]` to `/_/…` in engine/editor assemblies
  (test projects opt out): features that open source files only work in local builds. Lavapipe goldens come from such
  builds (no Output source-link icons). Per-frame formatting: never interpolate enums, and prefer `int.TryFormat` over
  interpolated handlers in code that must not allocate in a busy process (tier-0 `AppendFormatted<T>` boxes).
- Docker Linux tests (`just test-linux` / `just render-tests-linux`): one shared build volume, one run at a time (a
  second waits), synced by content, testing the committed `.spv`; a Rosetta stall on a Mac is retried once.
- Game shaders (ADR 0237): after editing a `.slang` surface shader `just canvas-shaders <folder>`, commit its `.spv`,
  `.layout.json` and `.mfspvlock`; defaults are literals, matrices `mul(v, M)`; no `#line`, so no slangc `-obfuscate` crash.
- Benchmarks: compare only on the machine that recorded `baseline.json` (MacBook, Apple M5), and only when it is
  quiet (parallel builds/agents skew results by 10–15 %).
- Steam: Facepunch.Steamworks 2.4.1 (vendored) behind the `MainframeEngine.Steamworks` shim (Win64/Posix builds,
  `SteamworksLoader` picks this OS's); Valve's `steam_api` natives ship in `runtimes/`, so Steam starts on win-x64,
  linux-x64, osx-x64 and osx-arm64 when the client runs. `SteamSocketsTransport` and avatar textures are done.

## Next steps

1. G19 protected exports (`docs/design/future/protected-exports.md`): phase 4 NativeAOT (owner: here or M12.6).
2. Gameplay toolkit (`docs/milestones.md`): G6's PBR default (owner); G8a–e rest (Faceted look, the Terrain dock's Foliage and
   Objects modes, tree inspector, river tool, forest goldens and frame budget); G20.3 video; G1c animation authoring (would let active
   ragdolls follow any clip); G12 (proposal awaits owner review); G11 rest (batched text, panel stand-ins in the view, G11.9; owner: open questions 2, 5, 6, 7); G14, G16, G17; G3b's rest (G3.8–G3.12).
3. Re-record `Tests/MainframeEngine.Benchmarks/baseline.json` on a quiet machine (Forest cabin; G6's two; G3.7's two; G11's `WorldUiBenchmarks`).
4. M11 — backend-neutral GPU API + D3D12 + WebGPU (`docs/design/future/rendering-backend-abstraction.md`).
5. M12 — mobile core, Android + iOS (`docs/design/future/mobile.md`, ADR 0100; spikes M12.0 first); follow the
   mobile-ready plumbing checklist in `memory/context/lane-agent-brief.md` in any lane meanwhile.
6. M13 — mobile platform services (`docs/design/future/mobile-services.md`).
7. User actions: code signing/notarization for the macOS editor build.
