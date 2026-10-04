# Current state — mainframe-engine

_Last updated: 2026-10-05 (M0 SDL + M1 stabilization, lane `m0-m1`)_

## Where things left off

- Engine runs on macOS (Apple Silicon: Sandbox ~121 fps Release, 2 dir + 1 point + 2 spot shadow
  casters, Spine, ImGui). Vulkan goes through MoltenVK; `Silk.NET.MoltenVK.Native` is bundled so no
  SDK install is needed to run. Windowing/input are SDL2 (Silk.NET SDL backend; GLFW removed).
- M0 and M1 are done (see `docs/milestones.md`, ADRs 0003/0004).

## Known gotchas

- macOS: SDL and Silk.NET must bind the SAME Vulkan library — `VulkanLoaderBootstrap` enforces this
  (`Probe()` then `HandOffToSdl()` → `SDL_Vulkan_LoadLibrary`; Silk via `TryCreateVk`). Don't add a
  bare `Vk.GetApi()` call anywhere; take `Vk` from `IVulkanContext`. `MAINFRAME_VULKAN_LIBRARY=<path>`
  forces a library for QA.
- HiDPI: SDL reports window size AND Silk's `IWindow.FramebufferSize` in points for Vulkan windows; use
  `Engine.FramebufferSize` (pixels, `SDL_Vulkan_GetDrawableSize`).
- Per-frame GPU resources are keyed by `IVulkanContext.FrameSlot` (2 slots), never by swapchain image.
- `ShadowSystem.RenderShadows` once per frame (per-frame-slot dynamic-offset light-VP ring).
- Shadow comparison samplers are IMMUTABLE (baked into the set-2 layout, shared by ShadowSystem and
  the renderer's ShadowFallback) — required by MoltenVK (mutableComparisonSamplers=false).
  MaxShadowSpot is 7, one less than LightEnvironment.MaxSpot, to fit MoltenVK's 16 per-stage sampler
  limit (4 dir + 7 spot + 4 point + 1 material texture); the 8th spot light casts no shadow.
  Keep C# constants and the MAX_SHADOW_* defines in Shapes.vk.frag / SpineLit.vk.frag in sync, and
  run `just shaders` after shader edits.
- Validation is on by default only in Debug builds (`EngineOptions.EnableValidation`).
- Render tests / `just qa` need the display awake (`caffeinate -u`). The unbundled `dotnet` Sandbox
  process can't be driven by computer-use; use the `--qa-*` scripted flags instead.
- ENet macOS natives are x86_64-only (won't load on Apple Silicon); Steamworks.NET has
  no osx-arm64 assets.

## Next steps

- Record lavapipe goldens for the new render tests (multi-light, spine-no-shadows) from CI.
- Replace/rebuild ENet natives for osx-arm64 before using networking on macOS.
