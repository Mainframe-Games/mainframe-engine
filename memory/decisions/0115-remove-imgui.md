# ADR 0115 — Remove ImGui: RmlUi is the only UI stack (dev overlay + screen gizmos)

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** Demo & polish — D2 (spec: `docs/design/future/remove-imgui.md`)

## Context

The engine carried two UI stacks. RmlUi runs the game UI and the whole editor; ImGui (ImGui.NET) drew only the F12 developer
overlay, the light and axis gizmos and a few debug windows. That cost a second Vulkan backend
(`VulkanImGuiController`, its own pipeline, shaders and input hooks), a native library (`cimgui`) in every published
build, a texture registry that existed only for it, and render tests that drew an ImGui frame in every scene. The overlay
was also unreachable from `GameHost` games: it was built in `Engine.OnImGui`, which needs an `Engine` subclass, so only
the Sandbox could use it. ImGui.NET ships no Android or iOS native either, which blocked the mobile plans.

## Decisions

1. **RmlUi is the only UI stack.** ImGui.NET, `cimgui`, `VulkanImGuiController`, the ImGui shaders, the ImGui texture
   registry (`IVulkanContext.ImGuiTextures`), `SubViewport.ImGuiTextureId`, `AudioImGui` and `Engine.OnImGui` are removed.
2. **`DevOverlay`** (F12, hidden by default) is an RmlUi layer on top of every game layer, registered by `Engine.OnLoad`.
   Built-in panels: frame, renderer, shadows, GPU memory, audio, physics, network. Any game adds panels with
   `DevOverlay.AddPanel(id, title, rml, bind)`; values refresh at 4 Hz without allocating.
3. **`ScreenGizmos`** (`RenderServer.ScreenGizmos`, a CPU-tessellated Vulkan batch drawn in the overlay pass) replaces the
   ImGui-drawn light and axis gizmos. `OverlayOrder` fixes the overlay draw order: canvas 0, gizmos 100, UI 200.
4. **Engine images in documents** go through `UiServer.RegisterTexture` (`engine://name`), including a source callback
   for images whose view can change (`UiTextureView.Generation`) and `UiTextureConversion.DepthToGray` for the shadow maps.

## Consequences

- No ImGui.NET package and no `cimgui` in the build or in published editors; one UI stack to maintain and to port to mobile.
- Debug UI is written in RML (bound data models), not immediate-mode calls. Games that used `OnImGui` use `AddPanel`.
- Render-test goldens were re-recorded: the overlay is hidden by default (it used to be visible), the picking golden shows
  its sub-viewport through a `UiDocument` image, and a dedicated test shows the overlay.
- The allocation gate runs with the overlay visible and both gizmos on.
- The editor keeps the overlay off; its debug surface is the RmlUi debugger (F9).
