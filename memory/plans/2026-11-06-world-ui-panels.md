# Plan: G11.5/G11.6 world-space UI panels

**Status:** in progress
**Docs:** [World-space UI proposal](../../docs/design/future/world-space-ui.md) (`UiPanel3D`, task list G11.5–G11.7),
[World-space UI](../../docs/design/world-space-ui.md), [Game UI](../../docs/design/game-ui.md),
[ADR 0216](../decisions/0216-viewport-textures-on-3d-materials.md) (the borrow path this generalises)
**Started:** 2026-11-06

## Goal

A `UiPanel3D` holds `UiDocument`s in its own RmlUi context, renders them into its own mipmapped texture before the
scene pass (so the scene shows this frame's UI), shows it on a quad (or mesh), and takes input from the mouse ray, a
crosshair or code, with focus and keyboard routing. The Demo's World UI tab has a terminal that opens a gate.

## Not in scope

- Input on custom panel meshes (`MeshRaycast`, G11.9): a panel with a `Mesh` renders on it, but rays only hit the quad.
- The batched `Text3DRenderer` (ADR 0249 keeps labels on the mesh batcher).
- Stand-in data for panels in the editor's scene view (they render once with whatever models exist); the QA step.
- Open questions 6 and 7 (input precedence, panels and TAA/glow): defaults below, listed in the PR for the owner.

## Design notes (decided here)

- **ADR 0252: panel segments in the UI command list, replayed before the scene.** `UiServer` updates and renders due
  panels first (`VulkanUiRenderer.BeginTarget`), then the screen layers (`BeginScreen`). `VulkanUiRenderer` is an
  `IPreSceneRenderer` (`IVulkanContext.AddPreSceneRenderer`, run by `RenderServer.RenderOffscreen` before the
  sub-viewports): each panel segment replays into the top-left of the shared layer stack (its extent becomes the max of
  the swapchain and the largest registered panel), then a resolve pass composites it over `Background` (or
  un-premultiplies it for `Transparent`) into the panel's RGBA8 `UNORM` image (`MUTABLE_FORMAT`), blits build the mips,
  and a barrier hands it to fragment shaders. The overlay replays only the screen segment, sampling its sub-rectangle.
  Gradient uniforms are addressed by their index in the whole list, so both replays share the per-slot region.
- **`Texture2D.FromGpuSource(Func<GpuImage?>, w, h)`** (internal): `TextureGpu` borrows whatever image the source returns
  (sRGB view for colour slots, a sampler with the image's mips), the same code the viewport path now goes through.
- **`UiPanel3D : GeometryInstance3D`**, `IUiContextHost` with `UiLayer`; `UiDocument.Host`, `UiDocument.Panel`;
  `Layer` stays (null under a panel). Unshaded panels: albedo black + emission = the panel × `Brightness` (glow catches
  bright panels); shaded: albedo = the panel. `device-desktop` icon (the atlas has no `device-tv`).
- **Gating:** `UpdateMode` (`Always`, `Once`, `Disabled`), within `MaxUpdateDistance` of the panel viewport's camera, its
  world bounds in that camera's frustum; at most `UiServer.MaxPanelRendersPerFrame` (8) nearest first, others keep their
  image. Edit mode (a panel of an edited scene): renders once after load and after each document (re)load, no input.
- **ADR 0253: panel input.** Screen layers first (a HUD menu over a terminal wins: open question 6's default); then
  the focused panel for keys/text/gamepad; pointer: `Mouse` (cursor ray, not while the mouse is raw), `Crosshair` (screen
  centre ray every frame, `ClickAction` presses the left button, works while raw), `Manual` (`PushPointer`/`PushButton`/
  `PushWheel`). Nearest hit within `MaxInteractDistance`; `RequireLineOfSight` casts a physics ray excluding the panel's
  own body. A press on a panel captures it until release (moves follow on its plane past the edge) and focuses it; a press
  elsewhere or Escape releases focus. `InputBeforeUi` nodes keep seeing input first as today. Panels draw in the scene
  like any mesh: TAA and glow apply (open question 7's default).

## Steps

- [ ] **GPU-sourced textures.** `Texture2D.FromGpuSource`, `TextureGpu` borrow path shared with viewports, DecalAtlas and
  canvas guards. Verified by: `viewport-material` golden unchanged; unit test of `FromGpuSource` size/no pixels.
- [ ] **Context hosts.** `IUiContextHost`, `UiDocument.FindHost`/`Host`/`Panel`, model names per host. Verified by:
  existing UI tests green; `UiPanel3DTests` documents attach to panels, model names per panel.
- [ ] **UI render targets (G11.5).** `IPreSceneRenderer`, panel registration and segments in `VulkanUiRenderer`, shared
  stack sizing, resolve shader (`UiPanelResolve.vk.frag.slang` + `.spv` + `shaders.lock`), mips. Verified by: `ui-panel3d`
  golden (gradient, clip mask, blur) on moltenvk and lavapipe, same-frame self-check, validation clean, UI goldens unchanged.
- [ ] **`UiPanel3D` node (G11.6).** Quad, material, `Brightness`, `UpdateMode`, distance and frustum gating, ≤ 8 per
  frame, edit-mode render-once. Verified by: `UiPanel3DTests` gating cases.
- [ ] **Input.** Mouse, crosshair + `ClickAction`, manual, capture across the edge, focus + keyboard + Escape, line of
  sight, layer-first precedence. Verified by: `UiPanel3DTests` routing cases; `TryRayToPixel` on rotated, scaled quads.
- [ ] **Allocation.** One panel updating every frame. Verified by: `ui-panel3d` `--alloc` run 0 B; a headless unit gate.
- [ ] **Editor (G11.7 part).** `UiPanel3D` inspector header button "Open document in UI preview" at the panel's
  `Resolution` (`UiPreview.FixedSize`). Verified by: editor unit test.
- [ ] **Demo.** Terminal panel in `world_ui` opening a gate, Tab toggles mouse/crosshair, Spanish and qps strings,
  `Demo.Tests` clicks it through `PushPointer`; screenshot. Verified by: Demo tests, `just demo-screenshots` image.
- [ ] **Docs and close.** world-space-ui.md Panels section + "Changes from the proposal", proposal status, game-ui,
  materials, testing, editor, demo, milestones, AGENTS.md, README, landing page; ADRs 0252/0253; log, state. Verified
  by: `check.sh`, `just docs-build`.

## Done when

- `UiPanel3DTests` pass headless; the `ui-panel3d` golden and its same-frame check pass on moltenvk and lavapipe.
- The panel allocation run is 0 B; no validation warnings; `.spv` and `shaders.lock` committed (`just shaders-check`).
- The gates in `.agents/workflow.md` pass; one PR, one push, Auto-fix on.

## Open questions

- Proposal questions 6 and 7: the owner (defaults above).
