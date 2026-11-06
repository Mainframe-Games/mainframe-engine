---
summary: >-
  G11.5/G11.6: UiPanel3D, a world-space RmlUi panel whose context renders into its own mipmapped texture before the
  scene pass (UI command-list segments replayed through IPreSceneRenderer, a resolve pass, Texture2D.FromGpuSource;
  ADR 0252), with mouse, crosshair and manual input, focus and keyboard after the screen layers (ADR 0253); the
  editor's "Open document in UI preview" button and a Demo terminal that opens a gate.
---

# 2026-11-06: World-space UI panels (G11.5, G11.6)

Branch `feature/world-ui-panels`, cut from `feature/world-space-ui-g11` (PR #180, not merged yet). Plan:
[World UI panels](../plans/2026-11-06-world-ui-panels.md). As built: [World-space UI → Panels](../../docs/design/world-space-ui.md#panels).

## What changed

- **G11.5:** `IUiContextHost` (`UiLayer`, `UiPanel3D`; `UiDocument.Host`/`Panel`), panel contexts in `UiServer`
  (`UiServer.Panels.cs`), `VulkanUiRenderer.BeginTarget`/`BeginScreen` segments, `IPreSceneRenderer` +
  `IVulkanContext.AddPreSceneRenderer`/`RecordPreScene` (from `RenderServer.RenderOffscreen`), the shared stack grown to
  the largest panel, `UiPanelResolve.vk.frag.slang` + mips, `UiPanelTarget`, `Texture2D.FromGpuSource` and the shared
  `TextureGpu` borrow path.
- **G11.6:** `UiPanel3D` (quad or mesh, private material, `Brightness`, `UpdateMode`, distance and frustum gating, ≤ 8
  renders per frame with owed renders first, edit-mode render-once), input (mouse, crosshair + `ClickAction`, manual
  push API, capture past the edge, focus, keys, text, gamepad, Escape, line of sight).
- **G11.7 (part):** `UiPanel3DInspector` button, `UiPreview.FixedSize`.
- **Demo:** `WorldUiTerminal` on a panel opens a gate, Tab toggles mouse/crosshair (camera aims), `use` action (E, A);
  Spanish and qps; screenshot.
- Tests: `UiPanel3DTests` (11), editor preview test, Demo terminal tests, `ui-panel3d` render tests (golden both drivers,
  same-frame check, 0 B allocation run).
- Docs: world-space-ui (Panels, Panel input, changes, decided questions), proposal status, game-ui, materials, testing,
  editor, demo, milestones, AGENTS.md, README, landing page.

## Decisions

- [ADR 0252](../decisions/0252-ui-panels-render-before-the-scene.md): segments, pre-scene replay, resolve + mips.
- [ADR 0253](../decisions/0253-ui-panel-input.md): layers first (open question 6 default), crosshair, capture, focus.
- Open question 7 (panels and TAA/glow): default, panels are scene geometry. Both 6 and 7 are listed for the owner.
- ADR numbers 0252/0253; #180's ADRs are 0251/0254 after main took 0249 (file extensions) and #179 holds 0250.

## Dead ends

- The per-frame allocation (24 B per panel, even when disabled) was a lambda capturing a local in `UiPanel3D.Sync`: C#
  allocates the closure at method entry, before the early return. A method group fixed it.
- Without clamping, the panel's bottom edge bled the header's colours (repeat sampling).
- The local slangc rebuilt four unrelated `.spv` files differently; they were reverted and the lock rewritten, so only the
  new shader changed.
- `UpdateMode.Once` renders on the frame the panel enters, then turns `Disabled` (Godot's semantics); a first test
  expected a second render.

## Verified

- See the PR: gates from `.agents/workflow.md` (build, Release `-warnaserror`, unit, render, format, shaders, check.sh).
- `UiPanel3DTests` 11/11 (0 B with a panel updating); UI tests 206/206; editor `UiPreviewTests` 11/11; Demo
  `WorldUiSceneTests` 4/4.
- `ui-panel3d` on moltenvk and lavapipe (Docker): golden inspected on both, the swatch green in the frame its document
  changed, validation clean, 0 B over 120 frames; `viewport-material` and the UI goldens unchanged.

## Not done

- Panels' stand-in data in the editor's scene view (G11.7 rest), the batched `Text3DRenderer`, G11.9 (custom mesh rays,
  SDF text, depth occlusion); `WorldUiBenchmarks` panel case and `baseline.json` (quiet machine); the editor QA step.
