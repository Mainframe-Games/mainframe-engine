# Current state — mainframe-engine

_Last updated: 2026-08-24_

## Where things left off

- Engine now runs on macOS (verified on Apple Silicon: Sandbox renders at 120fps with
  shadows, Spine, ImGui). Vulkan goes through MoltenVK; `Silk.NET.MoltenVK.Native` is
  bundled so no SDK install is needed to run. See the macOS section in CLAUDE.md/README.

## Known gotchas

- macOS: GLFW and Silk.NET must bind the SAME Vulkan library — `VulkanLoaderBootstrap`
  enforces this (GLFW via `glfwInitVulkanLoader`, Silk via `TryCreateVk`). Don't add a
  bare `Vk.GetApi()` call anywhere; take `Vk` from `IVulkanContext`.
- Shadow comparison samplers are IMMUTABLE (baked into ShadowSystem's descriptor set
  layout) — required by MoltenVK (mutableComparisonSamplers=false). MaxShadowSpot is 7,
  one less than LightEnvironment.MaxSpot, to fit MoltenVK's 16 per-stage sampler limit
  (4 dir + 7 spot + 4 point + 1 material texture); the 8th spot light casts no shadow.
  Keep C# constants and the MAX_SHADOW_* defines in Shapes.vk.frag / SpineLit.vk.frag
  in sync, and recompile .spv with glslc after shader edits.
- ENet macOS natives are x86_64-only (won't load on Apple Silicon); Steamworks.NET has
  no osx-arm64 assets.
- ShadowSystem's single VP UBO is overwritten during command recording — with >1
  shadow-casting light every pass executes with the last-written matrix.

## Next steps

- Fix the ShadowSystem per-pass VP matrix (push constant or per-pass buffer offsets).
- Replace/rebuild ENet natives for osx-arm64 before using networking on macOS.
