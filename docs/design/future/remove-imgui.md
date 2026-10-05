# Proposal: Remove ImGui — RmlUi dev overlay and Vulkan screen gizmos

**Milestone:** [Demo & polish](../../milestones.md#demo--polish-) (D2) · **Status:** ✅ done (see [Developer overlay](../dev-overlay.md)) ·
**Depends on:** [Demo project](demo-project.md) (removes the Sandbox, the biggest ImGui user) ·
**Replaced:** `docs/design/imgui-and-debug-tools.md` (now [Developer overlay](../dev-overlay.md)) · **Decision:**
[ADR 0115](../../../memory/decisions/0115-remove-imgui.md) (ImGui removed; RmlUi is the only UI stack)

## Problem

The engine carries two UI stacks. RmlUi (M8) runs the game UI and the whole editor; ImGui (ImGui.NET 1.89.9.3,
`Directory.Packages.props:26`) survives only for the F12 developer overlay and a few screen-space gizmos. It costs:

- a second renderer backend (`VulkanImGuiController`, 579 lines, own pipeline, shaders and input hooks) and a native
  library (`cimgui`) in every published build;
- a texture registry that exists only for it (`IImGuiTextureRegistry`, `IVulkanContext.ImGuiTextures`);
- an `Engine.OnImGui` hook that GameHost games cannot use (it needs an `Engine` subclass) — so the overlay is
  unreachable from real game projects;
- render tests that run an ImGui frame in every scene (`DevOverlayVisible` defaults to **true**, `Engine.cs:100`) and
  assert ImGui pixels.

The editor's transform gizmos already draw through Vulkan (`MainframeEngine.Editor/Src/Viewport/TransformGizmo.cs`,
`TransformGizmo2D.cs` → `DebugLines` / `SceneViewport.OverlayLines`); they are out of scope.

## Goals

- No ImGui anywhere: package, native library, shaders, controller, hooks, tests, docs.
- Keep every developer feature, rebuilt on RmlUi: a **`DevOverlay`** reachable from any game (GameHost included).
- Replace the ImGui-drawn gizmos with a Vulkan **`ScreenGizmos`** overlay renderer.
- Showing the overlay allocates nothing per frame (allocation gate) and is validation clean.

## Inventory (what goes)

| ImGui code | Replacement |
|---|---|
| `Rendering/Vulkan/VulkanImGuiController.cs`, `Content/Shaders/ImGui/ImGui.vk.{vert,frag}` (+ `.spv`, `shaders.lock:4-5`) | deleted |
| `IVulkanContext.ImGuiTextures` / `IImGuiTextureRegistry` (`IVulkanContext.cs:136-150`), `VulkanRenderer.ImGuiTextures` (`VulkanRenderer.cs:146`) | `UiServer.RegisterTexture` (`UiServer.cs:295-300`) |
| `Engine.OnImGui` + controller wiring (`Engine.cs:115,230,338,421-424,443-446,483-492,532-535,577`) | `DevOverlay.AddPanel` |
| `Engine.DevOverlayVisible` / `EngineOptions.DevOverlayVisible` (default true) / F12 (`Engine.cs:100,133-139,387-391`) | kept, now drives `DevOverlay`; **default false** |
| `Rendering/Vulkan/RendererDebugWindow.cs` | DevOverlay Renderer, Shadows, GPU memory panels |
| `Audio/AudioImGui.cs` | DevOverlay Audio panel |
| Sandbox `Game.OnImGui` "Developer" window, `NetworkDemo.DrawImGui` | gone with the Sandbox ([Demo project](demo-project.md)); counters move to DevOverlay Physics / Network panels |
| `Lighting/LightEnvironment.DrawLightGizmos` (`LightEnvironment.cs:156-280`), `Rendering/Gizmos/ImGuiCoordGizmo.cs`, `Utils/ImGuiGizmos.cs`, `ColorExtensions.ToImColor` | `ScreenGizmos` |
| `SubViewport.ImGuiTextureId` (`SubViewport.cs:23,88-91,144,165,191-206`) | removed; UI shows sub-viewports via `RegisterTexture` (already how the editor does it, `ViewportController.cs:180-187`) |
| `imgui.ini` entries in `.gitignore`, the template `.gitignore` | removed |

## DevOverlay

An engine-owned node created by the engine when a `UiServer` exists (every `Engine`, so GameHost games too), added
under `Root` outside `CurrentScene` like an autoload, so scene changes keep it.

- One `UiLayer` with `Layer = int.MaxValue` (always on top, input first while visible) holding a `UiDocument`
  (`MainframeEngine/Content/UI/dev/overlay.rml` + `overlay.rcss`, built on the widget library).
- F12 (`Engine.DevOverlayKey`) toggles `Visible`; hidden by default (`EngineOptions.DevOverlayVisible = false`).
  The editor keeps it off and its shortcut help no longer lists F12 as an ImGui overlay (`EditorCommands.cs:662`).
- Layout: a docked panel on the right with collapsible sections (one per panel), semi-transparent, scrollable.

**Built-in panels** (same information as the ImGui windows today):

| Panel | Contents |
|---|---|
| Frame | FPS, frame ms (avg/min/max over 1 s), frame number, VSync, max FPS, UI draw stats (`UiRenderStats`) |
| Renderer | Exposure slider + reset, colour-pipeline line (scene format → swapchain format/encoding), mesh stats (draw calls, surfaces, instances, culled, pipeline/material binds, shadow + ID draws, resident meshes/materials/textures, pipeline-cache hits) |
| Shadows | Cascade count/colours, stable cascades, filter/radius, passes, draws, CPU/GPU ms, map memory; **cascade and atlas images** |
| GPU memory | Allocator totals and per-type usage, staging ring, pending deletions, pipeline-cache KiB, shader-module count |
| Audio | Device name, voices / steals / underruns; per bus: dB fader, mute, solo, peak meter (writes go through `AudioServer` commands like the ImGui mixer did) |
| Physics | 3D and 2D bodies / awake counts, collision-shape debug draw toggle |
| Network | Shown only while a `MultiplayerApi` is active: tick, nodes, bytes/s in/out, clients |

**Extensibility.** `DevOverlayPanel DevOverlay.AddPanel(string id, string title, string rml, Action<DevOverlayPanel>? bind = null)`
adds a collapsible section whose body is the `rml` fragment. The panel's `RmlDataModel` (named `dev_{id}`, with an `open`
variable the title toggles) is created when the overlay document is ready, so values are bound in the `bind` callback with
the usual `Bind` / `BindList` / `Event` calls from [Game UI → binding](../game-ui.md); the returned handle exposes `Model`
(set once bound) and `Dirty(name)`. `DevOverlay.RemovePanel(id)` removes it; the `Refreshed` event (4 Hz while visible) and
the per-frame `Frame` event are where panels update values. Nodes reach the overlay through `Tree.Servers` (`DevOverlay` is
registered like the other servers), so games and the [Demo](demo-project.md) add sections without an `Engine` subclass. This
replaces `OnImGui`. The built-in panels use the same API.

**Shadow-map images.** RmlUi shows engine textures by name (`engine://...`). Shadow maps are depth images, so
`VulkanUiRenderer` gains a depth-view path: `UiServer.RegisterTexture(name, ShadowSystem view, …)` samples the depth
image in its read-only layout through a dedicated sampler and a "depth → grey" conversion
(`UiTextureConversion.Depth`). This preserves what `ShadowTests.ShadowMapViewerIsValidationClean` tests today.

**No per-frame allocation.**
- Values refresh at ~4 Hz, not every frame (`Model.Dirty(name)` only on refresh ticks).
- Bindings use static lambdas over a stats struct (the `Bind(name, this, static d => d.X)` pattern); numbers are
  formatted into reused buffers, never `$"..."` per frame.
- While hidden, the overlay does no work beyond the F12 check.

## ScreenGizmos

A new `ScreenGizmos : IOverlayRenderer` (`Rendering/Vulkan/IOverlayRenderer.cs`) — an immediate-mode screen-space
batch drawn **after tonemapping in the overlay pass**, so colours are exact sRGB like the UI:

- API (pixels, top-left origin, matching `Engine.FramebufferSize`): `Line(a, b, color, thickness)`,
  `Polyline`, `Circle(center, radius, color, thickness)`, `FilledCircle`, `Triangle`, `Arrow`, plus
  `TryProject(world, camera, out screen)`.
- Thick lines are expanded on the CPU into quads (anti-aliased with a 1 px feathered edge in the fragment shader);
  one pipeline, one dynamic vertex buffer per frame slot (`IVulkanContext.FrameSlot`, `MaxFramesInFlight`), capacity
  grows only on overflow (allocation gate stays green), pipeline created through `IVulkanContext.Pipelines`, shaders
  `Content/Shaders/Gizmos/ScreenGizmo.vk.{vert,frag}` (+ committed `.spv`, `shaders.lock`).
- Overlay renderers get an explicit order (`IVulkanContext.AddOverlayRenderer(renderer, order)`; registration order
  breaks ties): canvas (`OverlayOrder.Canvas = 0`, from the port) → `ScreenGizmos` (`OverlayOrder.Gizmos = 100`) → UI
  (`OverlayOrder.Ui = 200`), so game UI and the dev overlay draw on top of gizmos, and gizmos on top of 2D.
- Lives on the render server (`RenderServer.ScreenGizmos`), cleared every frame like `DebugLines`.

**Ports (1:1, they already project on the CPU):**
- `LightEnvironment.DrawLightGizmos` → `LightGizmos.Draw(ScreenGizmos, camera, lights)`: point lights (dot + three
  range circles), spot lights (cone + inner/outer rims), directional lights (arrow + sun icon). Drawn while the dev
  overlay's "Light gizmos" toggle is on (Debug builds, as today).
- `ImGuiCoordGizmo` → `AxisGizmo.Draw(ScreenGizmos, camera, corner)`: the corner XYZ axes with labels drawn as
  small glyph strokes (X/Y/Z built from lines; no font dependency). Toggle in the dev overlay.

The overlay pass itself does not change: `VulkanRenderer.BeginOverlayPass` (`VulkanRenderer.Presentation.cs:338-377`)
already runs every `IOverlayRenderer`, and `EndPasses` (lines 430-435) calls it when nobody else did — removing the
ImGui controller removes only the last step. Comments mentioning ImGui in `IOverlayRenderer`, `IVulkanContext`,
`VulkanRenderer.Presentation`, `WindowPixels`, `UiSystemInterface`, `VulkanUiRenderer`, `UploadQueue`,
`SubViewportCompositor` and `PseudoLocalizer` are updated.

## Tests

| Today | After |
|---|---|
| Every render test runs an ImGui frame (overlay visible by default) | Overlay hidden by default; a dedicated test shows it |
| `SceneTests.HdrTonemapSrgbTextureAndOverlayMatchTheReferenceMath` asserts ImGui overlay pixels (`SceneTests.cs:177-199`, `ColorPipelineScene.cs:48-53`) | Asserts `ScreenGizmos` filled shapes with the same reference colours (exact sRGB after tonemap) |
| `SceneTests.ObjectIdPickingAndSubViewportsWork` golden shows the sub-viewport via `ImGui.AddImage` (`MeshScenes.cs:383-393`) | Shown with a `UiDocument` `<img src="engine://…">`; goldens `picking_frame0020.png` re-recorded for moltenvk and lavapipe |
| `ShadowTests.ShadowMapViewerIsValidationClean` (ImGui shadow images) | `DevOverlayShadowsPanelIsValidationClean`: overlay visible with the Shadows panel open |
| `SceneTests.SandboxSteadyStateAllocatesNothing` (ImGui text, gizmos, mixer) | `ShowcaseSteadyStateAllocatesNothing` with the dev overlay visible, light + axis gizmos on |
| `ColorPipelineTests.UnormIsPreferredSoImGuiStaysExact` | renamed `UnormIsPreferredSoOverlaysStayExact` |
| — | Unit tests: `ScreenGizmos` CPU tessellation (quad expansion, capacity growth), `TryProject` behind-camera rejection |

## Docs

- `docs/design/imgui-and-debug-tools.md` → `docs/design/dev-overlay.md` (DevOverlay, panels, `AddPanel`,
  `ScreenGizmos`, light/axis gizmos).
- `CLAUDE.md`: frame order step 1 (`OnImGui`) removed, step 7 ("ImGui render") → UI + dev overlay; Dependencies list
  drops ImGui.NET; `Engine` legacy hooks list drops `OnImGui`.
- `README.md`, `THIRD_PARTY_NOTICES.md:33`, `MainframeEngine/Content/UI/credits.rml:23`, the ~30 design docs that
  mention ImGui (engine-lifecycle, vulkan-renderer, testing, color-pipeline, future/mobile, …) and the SVGs
  `docs/images/{architecture-layers,main-render-pass,viewport-and-depth,rmlui-integration}.svg`.

## Acceptance

- `git grep -in imgui -- ':!memory' ':!docs/design/future/remove-imgui.md'` finds nothing.
- No `cimgui` / `ImGui.NET.dll` in `just publish-local` output.
- F12 shows the dev overlay in the Demo (GameHost) and in render-test games; every panel populates.
- All gates in `CLAUDE.md` pass, including the allocation gate with the overlay visible.

## Task list

1. `ScreenGizmos` renderer + shaders; port light and axis gizmos; colour-pipeline test switch.
2. Depth-view support in `VulkanUiRenderer` / `UiServer.RegisterTexture`.
3. `DevOverlay` node, RML/RCSS, built-in panels, `AddPanel`; allocation gate + shadows validation test.
4. Remove ImGui (controller, hooks, registry, shaders, package, `SubViewport.ImGuiTextureId`); picking goldens.
5. Docs + ADR.
