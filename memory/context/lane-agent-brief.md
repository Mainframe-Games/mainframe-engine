# Lane agent brief (read first)

You are one lane of the autonomous M0→M10 build. The orchestrator integrates lanes onto `feature/m0-m10`.

## Setup
- You are in an isolated git worktree. Create your branch first: `git checkout -b lane/<id>` (id given in your prompt), based on the current `feature/m0-m10` HEAD (`git reset --hard feature/m0-m10` if your worktree started elsewhere — check with `git log -1`).
- Init submodules you need: `git submodule update --init Plugins/Spine` (native submodules only if your lane needs them).
- Read: `CLAUDE.md`, `memory/context/m0-m10-plan.md` (esp. Step 3 Definition of Done and the "Defaults for open questions"), `memory/context/progress-log.md`, `docs/design/testing.md`, `docs/design/build-and-platforms.md`, plus the design docs named in your prompt.

## Rules
- Build is warnings-as-errors with central package management (`Directory.Packages.props`). Approved NuGet packages only (see plan); anything else → stop and report.
- Never modify `Plugins/Spine` (vendored).
- Use `just` recipes (`just build`, `just test`, `just test-render`, `just shaders`, `just shaders-check`, `just format`, `just qa`). Shaders are Slang (ADR 0144): `mul(v, M)` with the C# matrices, `SV_VulkanVertexID`, `glsl_mod`, and a vertex shader writes only what its fragment shader reads (`docs/design/shaders.md#writing-shaders`). After any shader edit run `just shaders` and commit the regenerated `.spv` + `shaders.lock`.
- High performance: zero managed allocations per steady-state frame (the render-test allocation gate enforces it); no LINQ/closures/boxing in hot paths; `Span`/`stackalloc` (bounded)/pooling where appropriate; avoid `QueueWaitIdle` in frame paths.
- Rock solid: check every Vulkan `Result`, dispose deterministically, no `!` nullable-suppression spam, guard public API args.
- TDD: write tests alongside every feature (unit tests in `Tests/MainframeEngine.Tests`; rendering → render tests with goldens in `Tests/MainframeEngine.RenderTests`, regenerate `moltenvk` goldens with `just golden-update` and LOOK at the PNGs with the Read tool). Validation layers must stay at 0 warnings/errors.
- Render tests and `just qa` need the display awake: wrap with `caffeinate -u -t 600 &` or similar.
- Docs: when a proposal in `docs/design/future/` ships, move its content into the current-state doc in `docs/design/`, update `docs/milestones.md` rows (✅), and repoint links. Record open-question decisions as ADRs in `memory/decisions/NNNN-title.md` (next free number).
- Do NOT edit `memory/context/progress-log.md` (orchestrator owns it) — put your summary in your final report instead.
- Commits: logical, messages prefixed `M<n>:` (or the prefix in your prompt), ending with trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Do not push. Do not touch `main`.

## Before you report done
1. `just build` (Debug) and `dotnet build -c Release -warnaserror` → 0 warnings.
2. `just test` and `just test-render` pass; `just format-check` and `just shaders-check` clean.
3. Self-review your diff (`git diff feature/m0-m10...HEAD`) for bugs, allocations, leaks; fix.
4. Final report (<400 words): branch, commits (SHA + title), what shipped vs the design doc, deviations + why, tests added, gate results, API changes other lanes must know, risks/follow-ups.

## Mobile-ready plumbing (do when touching these areas)
Mobile (M12, `docs/design/future/mobile.md#mobile-ready-plumbing-checklist`, ADR 0100) is additive only if lanes stop adding desktop-only assumptions. When your lane touches one of these areas, follow the rule (no extra work elsewhere):
- **Renderer/surface:** surfaces and swapchains can disappear and come back (Android) — keep create/teardown paired in one place; a surface-format change must rebuild present pipelines, not throw. No GPU work may be assumed while the app is backgrounded.
- **Lifecycle:** subsystems with threads or devices (audio, logging, editor link, streaming) expose `Suspend/Resume`; route new pause/focus logic through engine lifecycle hooks (`OnApplicationPause/Resume/LowMemory` once they exist), and clamp the frame delta after a gap.
- **Input:** new pointer-like events carry a `PointerId` + device kind and framebuffer-pixel positions; never assume one pointer, hover or a right button; `InputMap` bindings stay `kind:args` data.
- **Layout:** use `Engine.ContentScale`/`FramebufferSize` (pixels) and safe-area insets (full framebuffer on desktop), not window points or hard-coded margins; HUD edge content sits in a safe-area container.
- **No JIT-only APIs** in runtime code: no `Reflection.Emit`/`DynamicMethod`/`Expression.Compile`/`Assembly.Load*` of files/reflection-based `JsonSerializer`; use generators and `JsonSerializerContext`; annotate editor-only features (collectible ALCs, file watchers).
- **Content I/O:** load content only through `ContentPaths` (later `IContentFileSystem`), preferably as streams; never `File.*`/`Directory.*` directly on content and never write into `Content/` at run time.
- **Textures:** take a format + per-mip data (block-compressed formats via `FormatInfo`), don't hard-code RGBA8 + GPU mip generation.
- **Render passes (TBDR):** pick load/store ops deliberately (`CLEAR`/`DONT_CARE` over `LOAD`, `DONT_CARE` stores for depth/MSAA/intermediates), avoid `vkCmdClearAttachments` and new full-screen passes, keep potential transients free of extra usage flags.
- **Vulkan 1.1 + capability flags:** no hard dependency on 1.2/1.3 core features or SPIR-V > 1.3; stay within ABP 2022 limits (≤ 4 descriptor sets, ≤ 16 samplers/stage, ≤ 4 colour attachments).
- **Quality knobs:** every new expensive feature gets a `ProjectSettings` `rendering.*` setting (like `ShadowQuality`) so mobile tiers can bundle it.
- **Natives:** new native code goes into `Native/` + `natives.yml` with a flat versioned C ABI, buildable static, 16 KB-aligned ELF.
- **Platform integrations** are optional servers behind a seam with a null implementation; per-user files only via `UserDataPaths`.

## Console-ready plumbing (do when touching these areas)
Consoles (PS5 + Xbox Series, `docs/design/future/consoles.md#console-ready-plumbing-checklist`, ADR 0143) extend the mobile rules above. This repo is public: never commit, log or name anything learnt under a console NDA.
- **Rendering:** no new raw `Vk` calls outside the renderer backend (use `IVulkanContext.Pipelines`/`Uploads`/`Deletions`, later `IGpuDevice`); pipelines come from a finite, enumerable set and are never first created mid-frame; no runtime shader compilation; shaders stay portable (≤ 4 bind groups, ≤ 128 B push data, no target-specific features without a fallback).
- **Users and input:** code that needs "the player" takes a user (`IUserPlatform` once it exists); game-facing UI is fully gamepad-usable; button prompts use glyphs, never baked "A"/"X" images or text.
- **I/O:** saves only through `ISaveStorage`; sockets only through `ITransport`; no `Process.Start`, direct URL opening or behaviour from environment variables in runtime code; caches have size limits and show up in the dev overlay.
