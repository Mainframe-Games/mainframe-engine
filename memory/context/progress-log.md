# Progress log — M0→M10 autonomous build

Plan: [m0-m10-plan.md](m0-m10-plan.md). Branch: `feature/m0-m10`. Final PR → `main` (rebase-merge).

## ▶ Resume here

- **Neon showcase (2026-10-27/28).** Branch `neon-demo` (one PR): `Examples/Neon`, a cyberpunk loft 20 floors up
  (ADR 0218, `docs/design/neon.md`), plus the three engine features it needed: `Path3D`/`PathFollow3D` and closed
  `Curve3D` (ADR 0215, `docs/design/paths.md`), sub-viewport textures on 3D materials (ADR 0216), nearest-light
  selection (ADR 0217). Built in phases N1–N6 with parallel sessions and worktree agents (N1 skeleton, N3 city, N5 audio)
  cherry-picked on. Reference shots N1–N8 in `docs/images/neon`; 187 Neon tests. Not in CI (like the Forest).

- **Gameplay toolkit run (2026-10-27).** Housekeeping on `chore/housekeeping` (`current-state.md` brought up to
  v1.1.5); three branches built in parallel worktrees from `main` 3963719: `feature/g1e-root-motion` (G1.20, ADR 0210),
  `feature/g9-joints-ragdolls` (G9.1–G9.6, G9.8–G9.9; ADRs 0211–0212; G9.7 waits for G7.3),
  `feature/g7-viewport-tools` (G7.1–G7.5, ADRs 0213–0215). Budget stop raised to 90 % for this run. Benchmark
  baseline to re-record on a quiet machine once they land.

- **SpineNode alpha-tested shadows + Billboard (#122, 2026-10-25).** ADR 0204, `spine.md` Shadows/Billboards; render
  tests `spine-shadow[-quads|-spot|-omni]`, `spine-billboard[-yaw]` (moltenvk goldens; lavapipe to record), `spine`
  golden re-recorded.
- **3D particles (2026-10-25, issue #120).** `GpuParticles3D` on the shared `ParticleProcessMaterial` (ADR 0206,
  `docs/design/particles-3d.md`): CPU `ParticleSimulation3D`, its own `ParticleRenderer` (not `MeshRenderer`), campfire
  and rain render tests (moltenvk goldens; lavapipe goldens still to record), allocation and frame-rate gates,
  `ParticleBenchmarks` (not in `baseline.json` yet).

- **Music mode (2026-10-17).** Branch `music-mode`: the music editor is a third `EditorMode` (Game | Music | Video) over
  the whole window, the transport in the toolbar, own menus and shortcuts, an empty state, songs closed with the
  project; ADR 0192, `editor.md` modes and song tab sections, `MusicModeTests.cs`, goldens `editor-music-empty` and
  `editor-music-song` (with an allocation gate, which removed per-frame allocations in the song tab).

- **Video editing mode: implemented, local only (2026-10-14).** Branch `feature/video-editor` (worktree
  `.claude/worktrees/video-editor`), based on `main`; not pushed, no PR yet (it targets `main` when Brogan says so).
  Plan `docs/superpowers/plans/2026-10-13-video-editor.md` (17 tasks), as-built doc `docs/design/video-editor.md` (moved
  from `future/`, with a "Changes from the proposal" list), ADR 0191, the modes section of `editor.md`.
  - What exists: a Game|Video `EditorMode`; `MainframeEngine.Video` (Cutback's Core, Media and Analysis copied in, MIT,
    editor only, Whisper.net 1.9.1); the video tab (preview with gapless ffmpeg decoders and an audio-voice clock,
    timeline with Cutback's gestures, transcript panel), Detect Silence / Idle Gameplay, Transcribe, Remove Filler Words,
    Precise/Fast export, Export Transcript, `.mfcut` projects, Video Settings, and Open in Video Editor in the recording
    dialog (Open in Cutback kept).
  - Goldens: `editor-video-empty` and `editor-video-timeline` (moltenvk + lavapipe). The timeline golden's allocation
    window found 198 KB per frame from constant index spans in `CanvasPrimitives` (Debug JIT allocates a
    `RuntimeFieldInfoStub` per `CreateSpan`); fixed with static arrays and a unit test.
  - QA: `Tests/QA/video.qa` over `Tests/QA/Fixtures/video/sample.mp4` (`build/make-video-fixture.sh`); a partial run up to
    `video-02-opened` showed the test pattern, the waveform's 4–6 s dropout and the 10 s marker. The full run (detect,
    transcribe with tiny.en, undo, play, mode switch) is the controller's to run and inspect. Its first run showed that
    `wait N` counts frames (~120 fps), so the script now waits on `wait-for video-jobs` and `wait-for video-position`.
  - Gates (Task 17): `just build` 0 warnings; Release `-warnaserror` build clean; `just test` engine 2496 (1 skipped),
    editor 1222 (5 skipped), video 456; `just format-check` and `just l10n-check` clean; `just test-render` 177/177
    (MinimiseAndRestoreIsCleanAndResumesRendering passed in the full run this time).
  - Parked: dragging files in from the OS; the per-frame texture re-upload (G16's `VideoTexture` later); the DAW mode (since built: music mode, ADR 0192);
    crash recovery for video tabs (`WriteRecoveryCopies` covers scenes only).

- **Editor recording hooks (Phase 3 of the Cutback integration): implemented, local only (2026-10-13).** Branch
  `feature/editor-recording` (worktree `.claude/worktrees/editor-recording`), **stacked on PR #62**
  (`feature/gameplay-recorder`, Phase 1), not pushed, no PR of its own. Docs: `docs/design/recording.md` ("Recording from
  the editor"), `editor.md` (Play table, F10), `project-and-gamehost.md` (editor link v4). Plan and spec live in the
  Cutback repo (`docs/superpowers/plans/2026-10-13-phase3-editor-hooks.md`, the integration spec amended with the as-built
  deviations).
  - What exists: editor link protocol v4 (`StartRecording`/`StopRecording` commands, a `Recording` message, queued up to
    64 and never coalesced); `GameSession.AttachRecorder`; `PlayService`/`PlayInstance` recording state (a recording is
    failed by the editor only when the game's link is gone); every editor play launch passes `--frame-capture`;
    **F10** / Run › Play and Record / the toolbar's red `point-filled` `#record` button with a red-dot `REC m:ss` readout
    (an icon: the UI font has no ● glyph);
    `RecordingPaths` (`{project user data}/recordings/{scene}-{yyyyMMdd-HHmmss}.mp4`), `CutbackLocator` (Editor Settings
    › Cutback, PATH, OS location), `RecordingNotifier` (a modal "Recording saved" / "Recording failed" dialog: Open in
    Cutback, Show in Folder). Cutback is started as a separate process with the recording path as its argument.
  - QA: `Tests/QA/record.qa` (`just qa-editor Tests/QA/record.qa`) needs a display and ffmpeg, so it is not in CI; no
    verb waits for the save, a fixed wait stands in. The controller ran it end to end: both flows saved an MP4 and its
    sidecar. It found two defects, both fixed: the missing ● glyph in the REC readout and long paths overflowing the
    message dialog. A third flow (Stop while recording) was added afterwards.
  - Final-review fixes: a stopped game that is recording or saving gets 2 min 15 s to finalise (was killed after 3 s,
    taking ffmpeg with it), and quitting the editor waits for it too; Run Another Instance drops `--record`; a Start that
    reaches a game already recording is answered `Started`; a failed `--record` start is reported to the editor before
    the game quits; protocol-3 games are refused with a "Rebuild it against this engine" message; an exited recording
    instance stays reachable until its Disconnected; macOS opens a Cutback `.app` with `open -a`; an abandoned
    recording offers Show in Folder when its `.partial` folder exists; Play and Record names the file after the main
    scene.
  - Residual fixes after the re-review: a game that reports its recording saved after Stop gets a fresh 3 s to exit
    (it was killed at once when the save took over 3 s, losing its save-on-quit); a `Started` after Stop gets the
    recording deadline; macOS opens Cutback with `open -n -a`, so a second Open in Cutback works while one is running.
  - Gates green: build (0 warnings), format-check, engine 2488 (1 skipped), editor 859 (5 skipped) before the final
    review's fixes; see the final fix report for the gates after them.
  - Next: Brogan's review and the push (one push and a PR, based on #62, when he says so); then **Phase 4**, the editor's
    Clips tab (the Cutback `Core` logic ported into the editor, "Open in Clips Tab" added to the finished dialog).

- **Gameplay recorder (Phase 1 of the Cutback integration): implemented, local only (2026-10-13).** Branch
  `feature/gameplay-recorder` (worktree `.claude/worktrees/gameplay-recorder`), not pushed, no PR. ADR 0190; docs
  `docs/design/recording.md` (the renderer and audio halves are in `vulkan-renderer.md` and `audio.md`). Plan and spec live
  in the Cutback repo (`docs/superpowers/specs/2026-10-12-mainframe-engine-integration-design.md`, amended with the
  as-built deviations).
  - What exists: `--record <file.mp4>` (live; offline with `--fixed-fps`), `--record-scale`, `--ffmpeg`; `SceneTree.Recorder`
    (`Start`/`Stop`/`Mark`/`Keep`/`Skip`); an ffmpeg child process for the encode and mux; a streaming float WAV fixed to the
    video's length; the `.mfcap.json` sidecar pinned by a fixture shared with Cutback; the renderer's frame stream (two
    readback buffers, no stall) and the audio capture tap (after the fault guard, whole blocks).
  - Smoke (`build/forest-record.sh`, run locally, needs ffmpeg and a display): offline flyover 4039 frames at 1920x1080
    (7.317 s loading + exactly 60.000 s), audio equal to frames / 60; live autowalk 22.2 s with 2 dropped frames.
  - Gates green: build (0 warnings), format-check, engine 2447 (1 skipped), editor 808 (5 skipped).
  - Known issues (all in the doc): `--record-scale` needs a display big enough for the scaled window; any window resize ends
    a recording; live audio runs on the device clock and drift is not corrected; `--headless --record` records nothing.
  - Next: the final review's fixes; then Phase 3 (editor hooks: `StartRecording` needs the game launched with
    `--frame-capture`) and Phase 4 (the editor's Clips tab). One push and a PR when Brogan says so (Actions minutes).

- **G4 Save games and settings: implemented, local only (2026-10-11).** Branch `claude/save-games-settings-35b203`
  (worktree `.claude/worktrees/save-games-settings-35b203`), not pushed, no PR. Proposal
  `docs/design/future/save-and-settings.md` is marked shipped with an "As built" section listing the deviations.
  - G4.1 (shared migrations, `DurableFile`, `userDataName`) was done in the main session. G4.2+G4.7 (`SaveServer`,
    cloud seam), G4.3 (`ConfigFile`, `UserSettings`, `UserVolume`, window modes, `UserScale`) and G4.4 (rebinding,
    `InputRebinder`) each ran as their own lane. G4.5 (`SettingsMenu`, `.tabs`, `settings.pot`, golden) was done in the
    main session; for G4.6 the editor Saves page, Open User Data Folder and the template `SaveGame` ran as a lane, and
    the Demo, QA and docs were done in the main session.
  - Gates green: engine 2345, editor 792, Demo 71, render 173 (moltenvk); Release `-warnaserror`, format, shaders;
    `just qa-settings` passes. Lavapipe golden `ui-settings-menu` recorded in Docker (`just render-tests-linux`).
  - Next: one push and a PR when Brogan says so (Actions minutes).

- **Forest showcase slice: playable, waiting for Brogan's notes (2026-10-08).** Branch `feature/forest-slice`, pushed,
  no PR yet. Worktree `.claude/worktrees/forest-slice`. Plan: `docs/superpowers/plans/2026-10-08-forest-vertical-slice.md`.
  - Every lane is merged:
    - wave 1: ADRs 0150–0155;
    - wave 2: 0156–0160;
    - wave 3: CC0 art (lane J), procedural audio (0162), the valley (lane L).
  - Gates are green:
    - engine 1803, editor 769, Forest 141, render 86;
    - Release `-warnaserror`, format, shaders.
  - `just forest` plays. Measured with `just forest-bench`:
    - 1080p: p50 13 ms, p99 20 ms;
    - 1440p: p50 18 ms.
  - Screenshots: `docs/images/forest/r1–r5`.
  - Known gaps, from lane L:
    - no SSAO/GI: flat shade under the canopy;
    - FXAA only: leaves and grass shimmer;
    - no impostors or coverage-preserving mips: speckled far canopies;
    - no SSR or refraction: the pond looks like a dark crater;
    - the fall is a ribbon;
    - shadows are 2 × 1024² to 60 m;
    - the R1 shafts are a little milky.
  - **Queued by Brogan, 2026-10-08:** SSAO and TAA, then the "Unreal look" phases 1–8 (G8e proposal:
    `docs/design/future/forest-visual-quality.md`, ADR 0164, written by lane Q).
    - Waves:
      - W4: lane P, the post-processing system + depth prepass + motion vectors + jitter (ADR 0163). **Merged**
        (538d271; gates green: 1819 engine, 769 editor, 141 Forest, 101 render). The G8e proposal is merged too (ADR 0164).
      - W5: S (SSAO/GTAO, 0165), T (TAA, 0166), G8e.2 (shadows, 0167), G8e.4 (cinematic post, 0168). **Merged**
        (1edf326f; gates green: 1892 engine, 769 editor, 153 Forest, 133 render; reference shots regenerated). The Forest
        also has an FPS readout (F3) and the click-recapture fix.
      - W5.5, by Brogan's request 2026-10-09: editor post-processing.
        - A `PostProcessProfile` resource (`.mres`) holds every post setting, with a migration from the
          WorldEnvironment exports. `WorldEnvironment.PostProcess` references it.
        - The editor viewport renders post effects live, with a toolbar toggle.
        - Profiles can be created and saved from the editor. The Forest uses `Content/PostProcess/forest.mres`.
        - Runs alone after W5 merges, before W6, so W6 adds its settings to the profile. **Merged** (ADR 0169; gates
          green per the lane: 1902 unit, 774 editor, 137 render).
        - Follow-up: the inspector squeezes the resource name in the Post Process row to almost nothing.
        - **2026-10-09: G8e.1 probe GI merged** (forest/gi2 fast-forward, 735dd1be; ADR 0170). Lane gates: 1969 engine,
          776 editor, 153 Forest, 152 render, lavapipe probes golden. The bake is committed (`forest-lighting.mres` + `.probes`);
          contact shadow → occlusion g (RGBA16F), set 0 binding 6 = probes. `forest/fo2` (G8e.5) is still running and
          was told to merge this first.
        - **2026-10-09: G8e.5 Ez Tree foliage merged** (forest/fo2 fast-forward, fe610cfe; ADR 0172). Lane gates: 2072 engine,
          776 editor, 153 Forest, 159 render; probes re-baked; R1–R5 regenerated with both lanes. Forest 1080p p50 18.9 → 21.6 ms
          (≈2 ms per-tree LOD cross-fade; GPU cull would fix); 2048² cascades rejected (+6 ms). Art-pass notes: R2's glade
          fleck lost (`TreeClusterShadowDensity` 0.6 restores it), mid-distance cluster cards soft (256×512 atlas cells).
        - **2026-10-09: G8e.8 TAAU merged** (forest/taau fast-forward, b1f26cd7; ADR 0174). Lane gates: 2086 engine, 776 editor,
          154 Forest, 165 render, lavapipe taau/fsr1 goldens. Forest ships Taau 0.75 (`++ --scale 1` = native): 1080p p50
          16.9 vs 20.9 ms native; 2560×1308 21.0 vs 28.0 ms. Below spec's 1.5 dB TAAU-vs-native (thin lines 29.9 vs 33.1 dB).
          The G8e target (p50 ≤ 14 ms at 1440p) is not met on a base M5.
        - **2026-10-09: editor fixes merged** (forest/edfix, f2e5ba3e): resource rows wrap so the name shows (tooltip = path);
          `EditorSettings.ViewResolution` Auto renders the 3D view one pixel per point (Forest preview 30 → 61–69 fps at 2×).
          cba5c2c2: `.probes` files get no `.meta`. Follow-up idea: an editor depth prepass when post preview is off.
        - **2026-10-09: G8e.7 art pass merged** (forest/art, merge ce12b748; ADR 0175): terrain macro texture (set 0 binding 7,
          image without sampler), TerrainBlend, per-instance colour jitter, CC0 scans + clutter, understorey, R6/R7, ACES kept
          with a fitted `forest-morning.cube`. Full gates on the merged branch: build 0 warnings, Release OK, 2097 engine,
          786 editor, 163 Forest, 167 render, format, shaders (133).
        - **2026-10-09/10, after Brogan's playtest:** fc93d611 tamed blown-out sunlight (sun 2, sky 2, probe fill, min lum
          0.03); ADR 0177 histogram auto exposure (forest/histo, 0d6742c8: centre-weighted 10–90 band + highlight protection;
          lavapipe golden for `auto-exposure-histogram` NOT recorded — the lane's sandbox refused Docker; Brogan was given the
          command); ADR 0178 look-dev against Brogan's five Unreal references (forest/look, a301433c: back-lit NNE sun,
          warm haze, AgX, muted olive greens, meadow/flowers/fern dell, dust specks, R1–R7 recomposed; refs never committed).
        - **2026-10-10 playtest round 2:** less haze (5c5f2183); ADR 0179 consistent shadow casters across cascades (the
          "shadow line cut off": coarse cascades from 16 m drew only half-density impostors; Forest `ShadowCoarseCascades` 0,
          impostor shadow hand-off from 55 m); ADR 0180 Forest ESC pause/settings menu (graphics, audio, controls, cameras:
          fly-over, free cam, R1–R7; settings.json); F12 overlay ×1.4; ADR 0181 UI reference-resolution scaling (1920×1080,
          match height; Demo 1280×720; old projects migrate to ConstantPixelSize; editor stays DPI). Gates on the combined
          branch: 2140 engine, 787 editor, 192 Forest, 171 render, Release, format, shaders (137). Open look call: R2/R7 glade
          lost its glow (canopy now shadows the fog); `Valley.TreeClusterShadowDensity` 0.7 restores it for ~2 ms.
        - **Queue done → PR opened for Brogan to play.** Known: an intermittent solid-colour frame after shrinking the window
          with TAAU on (seen twice, 1 in 10 here, never under validation; native scale fine) — `ResizeTargets` /
          `ApplyRenderScale` ordering is the first suspect. Not met: G8e.8's 14 ms at 1440p on a base M5 (≈21 ms). After both: full gates, push, PR (Auto-fix on), milestones G8 → 🚧, tell Brogan to play.
        - **Paused at the usage limit (tip 041d3544).**
          - Merged: G8e.3 volumetric fog (0171), the pipeline-cache stall fix (0176), G8e.6 water (0173), and the
            Slang 41012 warning fix. Gates are green: 1948 engine, 774 editor, 153 Forest, 147 render.
          - Stopped mid-lane, with partial work committed on their branches:
            - `forest/gi` (G8e.1 probe GI, ADR 0170): rendering tests exist; docs and ADR may be missing. Its
              budget: set 0 must stay at 5 or fewer images, because water's set 3 takes the layout to 16/14.
            - `forest/fo` (G8e.5 Ez Tree foliage, ADR 0172): only leaf-cluster cards are done. Still to do: impostors,
              hierarchical wind, bark, mips, species.
          - Resume: relaunch both lanes from their branches with "continue from your branch's commits" (base 041d3544),
            then regenerate all R1–R5 shots, then W7 (TAAU 0174, art pass 0175).
        - (was) **W6 running** (from 3f6f0bcf): G8e.1 probe GI (0170), G8e.3 volumetric light (0171), G8e.5 Ez Tree foliage
          (0172), G8e.6 water SSR/refraction/falls (0173). Next: merge them, then W7 (G8e.8 TAAU 0174, G8e.7 art pass 0175).
        - ADRs for the following waves: G8e.1 0170, G8e.3 0171, G8e.5 0172, G8e.6 0173, G8e.8 0174, G8e.7 0175.
        - Art-pass note: R1 is now too hazy (the shafts and fog wash it out).
      - W6: G8e.1 (probe GI), G8e.3 (volumetric light), G8e.5 (Ez Tree foliage: cluster cards, hierarchical wind,
        impostors, presets), G8e.6 (water SSR, refraction, falls).
      - W7: G8e.8 (TAA upscaling), G8e.7 (art pass).
    - Stop launching at 90% weekly usage (Brogan raised it for this work). When the queue is done, open the PR (Auto-fix on).
- **Status before this:** M0–M10 merged to `main` (fast-forward to `ec6bd70`, 2026-10-05); later work landed through
  PRs #5–#32.
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

### 2026-10-06 — Mouse mode (Driving Range port E1, ADR 0125)
- `MouseMode` (Godot's names/ordinals), `InputState.MouseMode`, `Input.MouseMode`; the engine applies changes through
  `InputRouter.ApplyMouseMode` (Captured → Silk Raw = SDL relative mode, whose position accumulates xrel/yrel; Hidden →
  Hidden) and resets the motion delta on every change. Test: InputMapTests.MouseModeIsGodotsAndNotifiesTheEngineOnChange.

### 2026-10-06 — Canvas pieces for the editor's 2D view (port E18, ADR 0126)
- 2D sub-viewports draw (and consume) their `DebugLines`/`OverlayLines`; edit mode runs only built-in draws and
  `[Tool]` `OnDraw`; a current Camera2D leaves the view alone in edit mode; `MainframeGame.props` copies game NuGet
  dependencies (the editor could not load Crash Site Defense: Newtonsoft.Json). The editor switch to canvas-rendered
  2D tabs worked but was reverted: game `OnReady` runs in edit mode (ADR 0126). Tests: EditModeCanvasTests (3).

### 2026-10-06 — Godot's editor rule for game scripts (port E18, ADR 0127)
- `SceneTree.EditModeScripts`; non-tool script types fall back to their engine base's enter/ready/exit in edit mode
  and skip OnDraw. The editor marks collectible (game) types and its 2D tabs now render through the canvas (sprites,
  Spine, custom draw, shaders, the editor's lines on top). Tests: EditModeCanvasTests (4).

### 2026-10-06 — Character recovery and settling with EPA (Driving Range port E10, ADR 0128)
- `RecoverBody` asks `NarrowPhase.Collision` (EPA signed distance) first, then `Distance`/`MprEpa`: a capsule starting
  exactly on a large box no longer sticks (GJK and MPR both missed the touch). New `PhysicsSpace3D.SettleBody`:
  after a floor hit from a sweep or the snap, the body sits exactly one margin from the floor (Jitter2 sweeps stop mm
  short). With Godot's 1 mm margin a walking/standing body holds 1.000 mm. Tests: two new Physics3DQueryTests.

### 2026-10-06 — Godot 4's just-pressed timing; jump floor state (Driving Range port E10, ADRs 0128–0129)
- Input: a press/release made during a physics step counts from the next step; just pressed/released no longer require
  the action to be held (Godot 4 defaults). CharacterBody3D: recovery skips contacts the body moves away from, so a
  jump is airborne after its take-off step. The Driving Range walk (walk, sprint, jump, strafe, mouse turn) now matches
  Godot's dumps within 1 mm. Tests: two InputMapTests, CharacterJumpingOffTheFloorIsNotOnTheFloorAfterTheJumpStep.

### 2026-10-06 — Disabled bodies leave the physics space (Driving Range port E11, ADR 0130)
- `Node.OnDisabledChanged` (Godot's NOTIFICATION_DISABLED/ENABLED); `CollisionObject3D` leaves its space while its
  resolved `ProcessMode` is `Disabled` and joins again at its current transform (Godot's `DisableMode.Remove`);
  `RigidBody3D` keeps its velocities across. Held items no longer fall or block rays. Test in Physics3DTests.

### 2026-10-06 — RayCast3D node; rays from inside shapes (Driving Range port E3, ADR 0131)
- `RayCast3D` (Godot's names and defaults, cast in its own physics step, `ForceRaycastUpdate`); `RayCast(...,
  hitFromInside)` skips shapes containing the origin when false (Godot's default for the node). Two Physics3DQueryTests.

### 2026-10-06 — Editor: sub-resource arrays, Play Instances (port E18)
- Inspector: array elements that are resources get per-element Edit/Load/New/Clear; expanded elements' properties
  render below the array ("Levels [1]"); the label-fit pass's one-`.prop-name`-per-row assumption kept intact.
  QA commands `inspect <file>` and `inspector-action <property> <action> [element]`.
- Play Instances (Ctrl/Cmd+F5, `project.mfproj` `playInstances`: label, args, delay); Play now puts `++` arguments
  after `--editor-port`/`--scene` (they swallowed the editor flags). `CanvasServer.ReleaseSceneReferences` before code
  reloads (the last frame kept the 2D tabs' game nodes). Edit-mode script check cached per node (the editor suite went
  from 15 s to 3 min without it). Tests: sub-resource array, PlayShortcutTests, Play argument order, settings round trip.

### 2026-10-06 — Material overlays, next passes, OutlineMaterial3D (Driving Range port E2, ADR 0132)
- `GeometryInstance3D.MaterialOverlay`, `Material.NextPass` (chains ≤ 8, `Material.ChainGeneration`), built-in
  `OutlineMaterial3D` (Godot's inverted-hull outline: `Mesh/MeshOutline.vk.vert`, front-culled, unshaded, blended).
  Extra passes join the opaque/transparent lists with less-or-equal depth pipelines; no shadows, not picked. Render
  test + golden `outline_frame0020`; pipeline and serialization unit tests.
### 2026-10-06 — Editor: remote scene tree (port E18, ADR 0133)
- Editor-link protocol 2: `RequestTree` command / `Tree` message (depth, name, type; ≤ 3000 nodes, 48-char names);
  `GameSession.SnapshotTree`, `EditorLinkClient.SendTree`, `PlayService.RequestTree` → `PlayInstance.RemoteTree`.
- Scene panel Local | Remote switch while a game is connected (read-only rows with type icons, expand state per path,
  a chip per instance, back to Local on stop), refreshed once a second. QA `remote on|off|pick|toggle`. Checked on
  Crash Site Defense: 773 live nodes. RmlUi gotchas for tests: `data-if` hides without removing and a hidden element
  keeps its last box; `data-for` keeps a hidden template element.
- Tests: tree round trip + worst-case frame size, session answers RequestTree, snapshot order/cap, PlayService
  RequestTree, a workflow test across two instances.


### 2026-10-06 — One exact contact for spheres (Driving Range port E12, ADR 0134)
- Resting golf balls spun up and rolled away: Jitter2's auxiliary manifold points sit ~1 % of the radius off a
  sphere's centre line. `SphereContactFilter` (chained after the triangle edge filter) registers one contact on the
  line for every overlapping sphere pair, exact against boxes. Two Physics3DTests. Known: Jitter2 lets resting bodies
  settle up to 1 cm (constant).

### 2026-10-06 — Fixed-delta first frame; Godot's Timer countdown (Driving Range port E13, ADR 0135)
- With `--fixed-fps` the start-up frame keeps the fixed delta (Godot's first frame steps physics too; real-time runs
  still discard it). `Timer` counts down a double and fires below zero, like Godot: a 1 s timer at 60 FPS fires on
  frame 60 (the float countdown fired on 61). Tests in GameTimeTests and SceneTreeTests.

### 2026-10-06 — SubViewport readback; transparent 3D background (Driving Range port E6, ADR 0136)
- `SubViewport.CaptureImage(Action<FrameCapture>)` copies the tonemapped image back after the view's next render;
  `TransparentBg` now also clears a 3D view to transparent and the tonemap keeps its alpha (flag bit; other views
  unchanged). Render test `subviewport-capture` (scene self-checks, no golden).

### 2026-10-06 — Packaged .app zips without AppleDouble files (Driving Range port, CSD E19 follow-up)
- `package-game.sh` zips the `.app` with `ditto --norsrc --noextattr`: extended attributes went into the zip as
  `._Name` entries, and `unzip` wrote them out as files. `UiServer` skips `._*.ttf` when loading fonts (it loaded
  `._LatoLatin-Bold.ttf` and the packaged Driving Range died at start-up). Test `FontLoadingSkipsMacAppleDoubleFiles`.
### 2026-10-06 — Scene changes wait for Godot's flush, input included (CSD port, ADR 0137)
- `ChangeScene` from input (outside Tick) was immediate: the port's New Game → Start click built the world inside RmlUi's
  mouse-up dispatch (segfault about one run in three; the crew member also swung at spawn). Now a pending scene is
  applied after the process callbacks + deferred calls, before timers (Godot's `_flush_scene_change`); last change wins.
  Three tests; five real mouse runs clean.

### 2026-10-06 — Game packaging: Developer ID signing and notarization (CSD port E19)
- `build/package-game.sh --sign "Developer ID Application: …"` (or `MF_SIGN_IDENTITY`): every Mach-O, then the bundle,
  with the hardened runtime, a timestamp and the .NET entitlements; `--notarize <profile>` (or `MF_NOTARY_PROFILE`):
  notarytool submit --wait, staple, re-zip. Ad-hoc stays the default (checked: the .app still signs ad-hoc and runs).
  Not run with a real identity (none on this Mac).

### 2026-10-06 — `Node.InputBeforeUi`: Godot's `_input` before the GUI, opt-in (CSD port, ADR 0138)
- The port's crew menu missed TAB once a menu element had focus (RmlUi took it as focus-next; the UI sees input first).
  Nodes with `InputBeforeUi` get `OnInput` before the input servers; everything else unchanged. One test; real-key check.

### 2026-10-06 — GameHost allocation gate (Driving Range port V5 follow-up)
- Driving Range's `--measure` read 40 B/frame under `GameHost`. Bisected with `GetAllocatedBytesForCurrentThread` probes
  through `Engine.OnUpdate`/`RenderFrame`, `SceneTree.Tick`, the frame servers and per process node: every engine
  section was 0 B (Silk loop and SDL events, real Core Audio device, file log, hidden dev overlay, render). The bytes
  were the game's measuring node: a lambda in `DevHarness.OnProcess` captured a pattern variable, so its closure
  (`path`, `this`, `frame`: 40 B) was allocated at method entry every frame. Fixed in the game; no engine change, no ADR.
- New render test `GameHostSteadyStateAllocatesNothing`: the host's `gamehost` run is a real `GameHost` subclass
  (`ProjectSettings`, default audio device + streamed loop, 240 FPS cap, SDL input, hidden dev overlay, `FileLogSink`,
  HUD) measured over 300 frames after 120. 0 B on MoltenVK with Core Audio; a planted 40 B/frame in `GameHost.OnUpdate`
  fails it (12 000 B / 300 frames). `RenderTestGame.DescribeDevice(IVulkanContext)` is now shared.

### 2026-10-06 — Silk's SDL event pump without allocation (Driving Range port, ADR 0139)
- The stray 88 B frame in Driving Range's `--measure`: Silk 2.23's `SdlView.OnEventReceived(IEnumerable<Event>)` boxes
  `List<Event>.Enumerator` on every frame with an SDL event (window event, key, every frame of mouse motion). Found with
  between-frames probes and an SDL event watch; quiet frames were free (.NET's shared empty enumerator).
- `SdlEventBatch` re-subscribes Silk's handler behind a reusable enumerable (reflection once in `OnLoad`; warning and
  Silk left alone if its internals move; Silk's handler restored on close/dispose).
- The `gamehost` gate now pushes mouse motion every frame, an unbound key and an `Exposed` window event, and counts them
  arriving (InputBeforeUi node). Without the swap: 26,400 B / 300 frames; with events dropped: the count fails.
- Seen while probing, not fixed here: `PushInput` allocated 1.6 KB on some key presses (`UiServer.HandleInput` once on an
  injected key, once in two runs; real desk keys too), a 6.7 MB frame inside update/render when the cursor swept the
  Driving Range HUD, and one 136 B between-frames frame on a real mouse motion (1 run in 41 after the fix; not
  reproduced with synthetic events, nothing in the instrumented entry points).

### 2026-10-07 — UI images load with their document (Driving Range port, ADR 0140)
- The 6.7 MB frame: Driving Range's first phone open decoded its seven hidden `<img>`s on the main thread (~15 ms,
  6.6 MB of RGBA as large-object-heap garbage). Found by injecting keys with section probes: Tab → `UiServer.Process`
  6,717,448 B, the exact size of every earlier sighting.
- `UiServer.PreloadImages` (on document load/reload) → `RmlRenderInterface.PreloadTexture(RmlPaths.Join(doc, src))`;
  `VulkanUiRenderer` keeps decoded textures by source and hands them over in `LoadTexture`; unrequested ones drop in
  `RmlCore.ReleaseTextures` and on dispose. No native change. Driving Range: first open 75 KB (the RCSS decorator
  `AppIconBG.png`, not covered); its frames take 9.7–12.4 ms in all (the UI update alone took 14–17 ms before).
- Render test `ImagesArePreloadedWithTheirDocument` (`ui-preload`); fails three ways with preloading off (381,904 B).

### 2026-10-07 — Engine or game on every log line; Godot's 2D math in core (CSD, ADRs 0141, 0142)
- `LogEntry.Source` (engine/game, from the caller file; no registration); console and file lines `[time] [INFO] engine
  Vulkan: …` / `game Net: …`; the editor's Output tags a game process's entries `game·Net` / `engine·Vulkan` (gamepad
  icon for the game's).
- Core math from GodotSharp 4.7.2 (MIT): `Mathf`, `Vector2I`, `Rect2I`, `Color`/`Colors` (↔ `Vector4`), `Side`; Godot's
  `Vector2` members as C# 14 extension members on `System.Numerics.Vector2`; built-in value types (codecs, generator,
  inspector editors for Color/Vector2I/Rect2/Rect2I). `Transform2D(rotation, origin)`, `transform * point/rect`,
  `Texture2D.Size`. Engine files using `System.Drawing.Color` by its simple name alias it (`DrawingColor` in the
  `MainframeEngine` namespace itself).
- `Label2D` (world-space text), `Bitmap` (CPU RGBA8; luminance/red expansion) + `Texture2D.FromBitmap`, `Time`
  (`TicksMsec`, `UnixTime`), `GameHost.IsDebugBuild`, `GameHost.UserDataDirectory`/`UserDataPath`.
- Crash Site Defense moved off its `namespace Godot` shim onto these; behaviour checked with its scenario dumps (29/29)
  and world/UI screenshots against a pre-change baseline.

### 2026-10-11 — 2D skeletal animation design (G17, docs only)
- Brogan asked for an engine-owned 2D skeletal system inspired by Spine, authored in the editor; design docs only.
- Branch `design/skeleton-2d` (local commit, not pushed). Proposals: `docs/design/future/skeleton-2d.md` (runtime,
  format, rendering, import, integrations, `mfe skel`), `skeleton-2d-editor.md` (Skeleton tab), `animation-graph.md`
  (state machine shared with 3D); mockups `docs/images/skeleton-editor-{setup,animate,mesh}.svg`,
  `animation-graph-editor.svg`; ADR 0184; milestones G17a–G17g (G9–G16 were taken by a parallel session's
  proposals); keyframe-animation/spine/animation-authoring updated.
- Key calls: Spine stays alongside; clean-room (no spine-csharp code); `.mskel` + one `.manim` per clip; Y-down; bake
  cache like tree bakes; compute skinning (first compute user); G17b extracts the shared timeline.
- Next: Brogan reviews the docs; then an implementation plan for G17a.

### 2026-10-11 — 2D canvas post-processing and more 2D lights (ADRs 0185, 0186)
- For Crash Site Defense's art pass (a survival-horror vertical slice), on branch `mainframe-engine-port` (recreated from
  `main` e0469a4).
- ADR 0185: `CanvasEnvironment` (2D node; Godot's Canvas background mode equivalent): glow (soft-knee bright-pass,
  13-tap down / tent up from half size), adjustments, lift/gamma/gain, tint, vignette, chromatic aberration, grain over
  the canvases up to `MaxLayer`; the layers above draw on top after it (`CanvasPass.PostSplit`, load pass); UI untouched.
  `VulkanCanvasRenderer.Post.cs`, `Content/Shaders/Canvas/Canvas{BloomDown,BloomUp,Post}.vk.frag.slang`,
  `CanvasPostMath` (CPU mirror). Gamma space on RGBA8 (no HDR canvas). Off by default: existing goldens unchanged.
- ADR 0186: 2D lights culled against the drawn area, 32 per frame (`MAX_CANVAS_LIGHTS`), 8 shared texture slots
  (`MAX_CANVAS_LIGHT_TEXTURES`, `flags.y`); `CanvasShaderBuild.IncludeRevision` (2) in the `.gdshader` lock hash so
  include changes make compiled canvas shaders stale.
- Tests: `CanvasPostTests`, `PointLight2DTests` (cap, slots, culling); render tests `canvas-post`,
  `canvas-many-lights` (moltenvk + lavapipe goldens). Gates: build, Release, format, 2356 unit, 175 render tests green.

### 2026-10-12 — GpuParticles2D life features (ADR 0187)
- `OneShot` + `Finished`, `Explosiveness`, allocation-free `Restart`; material damping, spin, lifetime randomness,
  `ScaleCurve` (new `Curve` resource) and `ColorRamp` (`Gradient`). Default emitters unchanged (goldens identical).
- `CanvasEnvironment` icon → `brightness-half` (the A commit reused `adjustments`, failing `IconAtlasTests`); `Curve` →
  `math-function`. Gates: build, Release, format, 2359 unit + 792 editor + 175 render tests green.

### 2026-10-12 — MainframeEngine + MainframeEngine.Templates NuGet packages (ADR 0188)
- The `MainframeEngine` package (nuget.org, release version) carries everything a game needs:
  - the engine and the bundled spine-csharp in `lib/`;
  - the generator in `analyzers/`;
  - the natives in `runtimes/` for 4 RIDs;
  - the engine content as `mainframe/Content`, copied into the app's `Content/`;
  - mf-l10n in `tools/`;
  - the checkout's `MainframeGame.props`/`Localization.targets` in `buildTransitive/`.
  Pack it with `build/pack-nuget.sh` (`just pack`); the result is about 31 MB.
- `MainframeEngine.Templates` ships at the same version. Its packed `template.json` defaults the engine version to
  its own version (an inline task writes the file verbatim; `WriteLinesToFile` turned `\"` into `/"`).
- nuget.org Trusted Publishing instead of a stored API key: the policy is owned by the `Mainframe-Games` org (workflow
  `publish.yml`, environment `release`), with `NuGet/login@v1` and `id-token: write` on the release job. No secrets:
  `NUGET_USER: brogan` in the workflow is the policy creator's nuget.org username.
- Template: it defaults to `--engine-source package` and sets `ImportDirectoryPackagesProps=false`. The editor
  publishes the template next to itself, and New Project has a package/checkout toggle (package at the editor's
  version in release builds).
- `publish.yml` gained a `package` job; the release attaches the `.nupkg`, then pushes it to nuget.org (Trusted Publishing, below).
- CI `template`: the smoke test runs in package mode as well (pack, build, run, publish linux-x64).
- Generating XML docs exposed existing broken crefs and misplaced comments; they are fixed (CS1591/CS1573/CS0419
  suppressed).
- Gates: build, Release, format, shaders, 2359 unit + 812 editor + 175 render tests, and the template smoke test in
  both modes (macOS). A plain `dotnet new console` + `dotnet add package` game also builds and runs, and the published
  editor (0.0.1, no checkout) created, built and opened a package project.

### 2026-10-12 — Facepunch.Steamworks on Windows/macOS/Linux + player builds from the package (ADR 0189, #48)
- Steamworks.NET (x64-only, no natives) replaced by Facepunch.Steamworks 2.4.1, vendored in `Plugins/Facepunch.Steamworks`
  as Speed Golf has it. The DLLs are plain git, not LFS (they are compile references). Valve's `steam_api` natives are
  committed in `runtimes/<rid>/native` (LFS like the others, outside `natives.lock`).
- `MainframeEngine.Steamworks` shim, built twice under one assembly name (`Win64/`, `Posix/`). The output goes to
  `steamworks/{win64,posix}/`, and `SteamworksLoader` (ALC `Resolving`) loads this OS's. Native `setenv` for
  `SteamAppId` on POSIX.
- Speed Golf's lifecycle lessons are applied: `asyncCallbacks: false`, events after init and removed on shutdown,
  leave the lobby and clear presence before `SteamClient.Shutdown`, never `OnDebugCallback`.
- #48: the package ships `tools/package-game.sh` (+ `tools/macos/png-to-icns.sh`), found through
  `MainframePackageGameScript`. The script prunes other platforms' Steam files and refuses `steam_appid.txt`. The
  package template smoke packages linux-x64, win-x64 and osx-arm64 and checks each archive.
- Verified on macOS arm64 with Steam open:
  - `SteamTests` initialize as Spacewar.
  - A packaged `.app` built from the NuGet package and launched from `/` logged `Steam: running (app 480, …)`.
  - CI's three OSes check `SteamNotRunning`.
- Follow-ups: `SteamSocketsTransport` on Facepunch relay sockets; avatar textures.

### 2026-10-18 — Logic-style DAW: built-in effects, plugin window, bottom pane, Browser (ADR 0193; #80, #81)
- 15 built-in insert effects in C# (`Music/Effects/`): Channel EQ, Filter, Compressor, Limiter, Noise Gate, Beat Delay,
  Reverb, Chorus, Flanger, Phaser, Tremolo, Saturation, Distortion, Bitcrusher, Utility. Lock-free parameter push,
  bypass crossfade, staged chains with VST3, `EffectContext` (tempo/position) for synced effects. `docs/design/music-effects.md`.
- `PluginWindow`: knobs from the parameter descriptors, the EQ curve with draggable handles, gain-reduction meters.
- Bottom pane one at a time (P/X, resizable, saved); M/S mute/solo (#81).
- Browser (F): songs and plugins, search, collapse, drag onto headers/strips/slots/lanes; mixer insert slots per strip;
  `RmlStructType.List` (one level of nested data-for lists).
- Designs merged: wavetable synth (#78, ADR 0195, open questions) and adaptive music (#79, ADR 0196; A1 code in #82).

### 2026-10-18 — Mainframe Wavetable: engine, presets, window (ADR 0195; #84, #85, synth window PR)
- Synth engine (`Music/Synth/`, #84): mip-mapped wavetables, warps, unison, sub/noise, two filters, DAHDSR envelopes,
  LFOs, mod matrix, poly/mono/legato, FX chain; zero allocation; live pitch bend / mod wheel / aftertouch.
- Content (#85): `.wav` wavetable import (`clm ` chunk, UID refs), 39 factory presets, `WavetablePreset` engine resource.
- `SynthWindow`: Osc / Matrix / FX tabs, wavetable / filter / envelope / LFO canvases, drag-to-assign modulation with
  live dots, drawn LFOs, keyboard, CPU meter; the plugin window's synth stopgap removed. Next (PR 4): embedded FX panels
  in the FX tab, render-test goldens of the window.
- PR 4: `EffectPanel` shared by the plugin window and the synth's FX tab (EQ curve, meters, presets; drag-to-reorder
  rows), tile hover marks, zero-allocation steady frames with the window open; goldens `editor-synth-osc` (allocation
  gate), `-matrix`, `-fx` at 1440 × 900.

### 2026-10-18 — Adaptive music A2: the runtime (ADR 0196 accepted)
- The design's ten open questions were decided with the recommended answers (recorded in the design's "Decided questions").
- `MusicVoice` (audio thread, a `SoundComponent` per `AudioBusInfo.MusicVoices` slot, 2 on Music by default): 4 lane
  groups of 8 lanes sharing one read position, a sorted action queue split per sample, linear fades, tails and stingers
  on 8 one-shot lanes, per-lane layer gain and the shared `Svf` (moved to `MainframeEngine/Src/Audio/Dsp/`). Music
  commands and events through `AudioServer` (`AudioServer.Music.cs`); groups return to the game thread only on
  `MusicGroupFreed`; continuations prefetched; misses rescheduled (`LateMusicTransitions`).
- `AdaptiveMusic` resource + `AdaptiveMusicImporter` (`ResourceLoader` now routes `.mmusic` with its embedded UID),
  `AdaptiveMusicInstance` (game thread, the director), `AdaptiveMusicPlayer` node with SectionStarted/Beat/Bar/Marker/
  TransitionScheduled/Finished.
- Tests with generated WAV stems (exact sample timing, fades, curves, misses, resampling, signals), an allocation gate,
  benchmarks (instance frame ≈ 0.4 µs, 16-lane block ≈ 42 µs) and an adaptive player in the render-test showcase.
- Next: A3 (bake) and A4 (editor sections/layers/Live preview).

### 2026-10-19 — Adaptive music A4: Sections lane, layers, Adaptive tab, Live preview (ADR 0196)
- `SongEngine` adaptive mode: the snapshot's `MusicDirector` on the render thread (actions queued and executed on their
  frames, continuations, a stinger cursor, layer buses with filter and curve gain), state moved to each new snapshot's
  director; decisions match a bare director frame for frame; 0 B per block.
- Sections lane under the ruler (create/move/resize by bars, markers, menus), layer chips, the right panel's Adaptive
  tab (A), Shift+Space / T / Bake split button; golden `editor-music-adaptive` with the allocation gate (found and fixed
  the mixer meters' per-frame closure).

### 2026-10-19 — Adaptive music A5: Curves pane, transition popover, Baked audition (ADR 0196)
- **Curves pane** (`SongCurves`, `CurvesCanvas`): a third bottom pane showing a layer's volume or cutoff over a
  parameter, with ghosts and the current-value line. Point drags (Shift: one axis) are one entry each; double-click adds
  or removes a point; right-click picks the shape. Layers get **+ Curve**.
- **Transition popover:** an inline editor under the rule row covering from (any/several), to, all/any conditions,
  quantize/every, entry, fades, segment and stinger.
- **Live | Baked** (`BakedAudition`): plays the last `.mmusic` through `AdaptiveMusicInstance` on the editor's
  `AudioServer`; the panel, lane and playhead follow the instance.
- **Tests:** `AdaptiveA5Tests`, including an end-to-end bake → runtime playback on a null device.
- **Golden:** `editor-music-curves` (both drivers). Its allocation gate found `Canvas.SortedRoots`' `List.Sort(IComparer)`
  allocating a delegate per frame with two root items, so the engine now has `NoAllocSort`.
- **Next:** A6 (editor link v5 Target menu, dev overlay Music panel, Demo scene, README/landing).

### 2026-10-19 — The content folder is `Assets/` (ADR 0197)
- `Content/` → `Assets/` everywhere: engine, editor, tests, Demo, Forest, the template, the build output and every
  path string. API names stay (`ContentPaths`, `AssetDatabase.ContentDirectory`, `MainframeContentSource`,
  `LocaleContentRoot`).
- Legacy `Content/…` paths still resolve (`ContentPaths.UpgradeLegacyPath`). `project.mfproj` is now format 4: the
  3 → 4 step moves `Content/…` values.
- The editor offers to migrate an old project when it opens (`AssetsFolderMigration`: rename the folder and rewrite
  the paths); Cancel leaves the project closed.

### 2026-10-19 — FileSystem panel: Simplified view (ADR 0099 amendment)
- The default view shows two roots: Assets (`Assets/`) and Scripts (the game library's `Src/`, only C# files and the
  folders that hold one). The header's `folders` toggle switches to the whole project folder, saved as
  `EditorLayoutSettings.FileSystemVerbose`. A folder with neither root shows the whole folder.
- The roots cannot be renamed, moved, trashed or dragged. Drops keep C# in Scripts and assets in Assets. New scenes,
  resources and songs go to Assets. In list and grid views Up from a root shows the roots.
- The header buttons are 18dp so eight fit beside the title. The `editor-filesystem` and `editor` goldens were
  re-recorded; `editor-walkthrough.qa` captures both views.

### 2026-10-25 — 3D skeletal animation: Skeleton3D, AnimationPlayer, compute pre-skinning, skinned import (#119, ADR 0207/0208)
- **Runtime:** `Skeleton3D` (bones as data), `Skin`, `MeshSurface.Joints`/`Weights`, `MeshInstance3D.Skeleton`/`Skin`,
  `BoneAttachment3D`; `Animation` (position/rotation/scale + `EventTrack`), `AnimationLibrary`, `AnimationPlayer` (Godot's
  play/queue/stop/seek/speed/crossfade API, `AnimationEvent`); deterministic, 0 B, works under `HeadlessHost`.
- **Skinning:** `RenderServer.SkinMeshes()` after `BeginFrame` dispatches `Skinning/Skin.vk.comp` per instance into
  frame-slot vertex buffers; a per-instance `MeshGpu` view makes every pipeline (shadows, outline, ids, sub-viewports)
  draw the pose; `MeshDepthSkinned` gives limbs motion vectors.
- **Import:** bones → `Skeleton3D`, skins, clips → `AnimationPlayer`; loops/events from `-loop`, glTF animation extras,
  `.meta` `clips`. Silk's `QuatKey` is 8 bytes larger than the native Assimp 6.0.2 one: the importer detects the stride.
- **Tests/assets:** `SkinnedCharacter` generates `Tests/Assets/Models/Character/character.glb`; unit suites under
  `Tests/MainframeEngine.Tests/Animation/`; render tests `skinned` (moltenvk golden) and `skinned-crowd` (0 B, 120 fps);
  Demo scene `animation_3d`. Lavapipe golden for `skinned` still to record.

### 2026-10-25 — Spatial shaders (#121, ADR 0205)
- `shader_type spatial` `.gdshader`s load as `ShaderMaterial`s on 3D meshes: `SpatialShaderCompiler` (five passes:
  colour, depth prepass + velocity, two shadow casters, object IDs), `MeshRenderer.Spatial.cs` (a set and pipeline layout per
  shader, `PipelineKey.UserShader`), `ShaderParameters` in `.mres`, `MultiMesh` custom data (`INSTANCE_CUSTOM`).
- `just shaders-check` also checks committed `.gdshader` SPIR-V; `mf-shaders translate` prints the Slang.
- Render tests `spatial-shaders`, `spatial-shaders-taa` (+ allocation gate); lavapipe goldens still to record.

### 2026-10-26 — Forest: log cabin, beach and lake, edge cliffs, distant mountains (ADR 0209)
- Engine: twelve terrain splat layers (third weight map `splat-2.png`, a texture array on the GPU; 8-layer terrains
  byte-identical, goldens unchanged); the probe fingerprint skips glass-only meshes like the bake (fresh bakes read stale).
- Forest: 320 m map; dune, sand beach (layer 8, Ground054) and lake off the south edge; cliffs W/N/E kept under the low
  sun's line (`SunCap`) with ravines where held down; R6's frame keeps the old rim (≈ 20 m of north edge wall-only);
  `ForestMountains` (2048 m coarse terrain, impostor conifers, far lake plane).
- Cabin: Blender script `Tools/cabin/build_cabin.py` (`just forest-cabin`) → `Assets/Art/Cabin/cabin.gltf`; Poly Haven
  furniture at `Place_` sockets; `CabinFire`, `CabinDoor` (`interact`, E/X), `CabinInterior` (dims sky ambient indoors),
  fire crackle synth, sand footsteps; R9 `r9-cabin`; Cameras page 3 × 3.
- Bench (loaded machine, same conditions): +1.6 ms p50 vs main; baseline not re-recorded (needs a quiet machine).

### 2026-10-27 — Housekeeping
- `current-state.md` was last written for v1.0.0: now lists what shipped since M10, v1.1.5, the Steam follow-ups as
  done (#127, #128), current test counts and the new next steps.
