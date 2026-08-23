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
- macOS validation flags two shadow-path spec violations that currently render fine on
  MoltenVK but are UB per spec: mutable comparison samplers (VUID 04450 — layout should
  use immutable samplers) and 17 > 16 per-stage samplers on the SpineLit pipeline
  (VUID 03016 — shadow-map arrays 4+8+4 plus uTexture).
- ENet macOS natives are x86_64-only (won't load on Apple Silicon); Steamworks.NET has
  no osx-arm64 assets.
- ShadowSystem's single VP UBO is overwritten during command recording — with >1
  shadow-casting light every pass executes with the last-written matrix.

## Next steps

- Fix the two shadow-path portability violations above.
- Fix the ShadowSystem per-pass VP matrix (push constant or per-pass buffer offsets).
- Replace/rebuild ENet natives for osx-arm64 before using networking on macOS.
