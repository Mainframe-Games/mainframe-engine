# Progress log — M0→M10 autonomous build

Plan: [m0-m10-plan.md](m0-m10-plan.md). Branch: `feature/m0-m10`. Final PR → `main` (rebase-merge).

## ▶ Resume here

- **Current wave:** W1 (setup)
- **Next action:** push `feature/m0-m10`, watch CI (first lavapipe run records goldens — see Step 1 entry); W1 lanes A (M0 SDL → M1) and C (M5 scaffold fixes, ENet natives CI) rebase onto Step 1
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
