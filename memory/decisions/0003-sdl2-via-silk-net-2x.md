# ADR 0003 — Windowing and input on SDL2 via Silk.NET 2.x

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M0 (W1 lane A)

## Context

Windowing and input went through Silk.NET's default backend, GLFW. The roadmap needs gamepads (SDL's
controller database, rumble, gyro; what Steam Input and the Steam Deck expect), IME text input,
clipboard and display/DPI queries. The switch to SDL was agreed on 2026-10-05; the open question was
whether to stay on SDL2 (Silk.NET 2.x) or bind SDL3 directly. The plan's default: SDL2 via Silk 2.x.

The proposal assumed SDL was already a transitive dependency. It is not for `net10.0`: the
`Silk.NET.Windowing` / `Silk.NET.Input` meta packages only pull SDL for netstandard / mobile TFMs; on
`net6.0+` they pull GLFW only.

## Decision

- **SDL2 through Silk.NET 2.22.0.** The engine references `Silk.NET.Windowing.Sdl` and
  `Silk.NET.Input.Sdl` (same Silk.NET family and version; they bring `Silk.NET.SDL` and
  `Ultz.Native.SDL` 2.30.8, universal macOS dylib + win/linux natives) **instead of** the
  `Silk.NET.Windowing` / `Silk.NET.Input` meta packages. GLFW (`Silk.NET.GLFW`, `Ultz.Native.GLFW`)
  is no longer in the engine's dependency graph, so it can't be loaded at runtime.
  `Examples/SpineExamples` drops its own meta-package references and uses the engine's.
  `Examples/SilkVulkanExamples` (standalone, `Silk.NET` meta) is untouched.
- The `Engine` constructor registers the platforms explicitly (`SdlWindowing.RegisterPlatform()`,
  `SdlInput.RegisterPlatform()`, `Window.PrioritizeSdl()`) rather than relying on reflection discovery,
  which trimming/AOT would break.
- **macOS loader handoff:** `VulkanLoaderBootstrap` is split into `Probe()` (unchanged probe order) and
  `HandOffToSdl()` (`SDL_Vulkan_LoadLibrary(ActiveLibraryPath)` on the shared `SdlProvider` instance,
  before the window exists). `TryCreateVk()` is unchanged, so SDL and Silk's `Vk` bind one library.
  `MAINFRAME_VULKAN_LIBRARY=<path>` forces a library (used to QA each source).
- **HiDPI:** Silk's SDL `IWindow.FramebufferSize` reports the GL drawable, which for a Vulkan window is
  the size in points. `Engine.FramebufferSize` (`WindowPixels`) returns `SDL_Vulkan_GetDrawableSize`
  in pixels; the renderer and games use it for extents and aspect ratios.

## Consequences

- No new third-party dependency: the two packages are the SDL backends of the already-approved
  Silk.NET 2.22.0 family (flagged in the lane report for the record).
- SDL3 / Silk.NET 3 remains a separate future migration; the loader handoff is the only direct SDL call
  besides the drawable-size query.
- Verified on macOS arm64 with all three loader sources (SDK `/usr/local/lib`, `~/VulkanSDK/<ver>`,
  bundled `libMoltenVK.dylib`). Windows/Linux are covered by CI (lavapipe + Xvfb for render tests).
