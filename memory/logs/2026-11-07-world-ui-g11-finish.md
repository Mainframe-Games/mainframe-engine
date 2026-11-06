---
summary: >-
  G11 finished: unshaded Label3Ds draw through a batched Text3DRenderer (atlas pages in groups of eight, CPU poses, one
  draw per pipeline and page group; ADR 0258), opt-in IgnoreExposure on labels and unshaded panels with auto exposure
  read back to the CPU (ADR 0257, the owner's answers to questions 2/5/6/7), mesh input on panels, and panels showing
  the UI preview's stand-in data in the editor's scene view. The proposal moved into world-space-ui.md.
---

# 2026-11-07: World-space UI finished (G11.7, G11.9, IgnoreExposure)

Branch `feature/g11-world-ui-finish`. Plan: [Finish G11](../plans/2026-11-07-world-ui-g11-rest.md).
As built: [World-space UI](../../docs/design/world-space-ui.md) (the proposal is gone; its "Changes from the proposal" and
"Decided questions" sections hold what differed).

## What changed

- **Batched labels (ADR 0258):** `Src/Rendering/Text/Text3DRenderer.cs` (+ `StreamRing<T>`), `Assets/Shaders/Text/Text3D*`,
  `include/text3d.slang`. `Label3D` keeps a glyph run (`Glyphs`, `GlyphsVersion`, `SyncGlyphs`) built by the same layout as
  its mesh; `IsBatchedText` (unshaded, not a shadow-casting cutout) decides the path, and batched labels return no render
  mesh. `World3D.LabelList`; `RenderServer` prepares runs in `PrepareFrame`, draws labels last in `DrawWorld`, adds them to
  both pickers, and exposes `Text3DStats`. `MaterialDescriptorAllocator` takes storage-buffer descriptors.
- **IgnoreExposure (ADR 0257):** `Label3D.IgnoreExposure`, `UiPanel3D.IgnoreExposure` (off by default); `AutoExposure`
  copies its adapted luminance into a per-slot readback buffer (`LastAdaptedLuminance`); `PostExposure.Effective`,
  `IPostProcessHost.EffectiveExposure`, `SubViewportPost.EffectiveExposure`, `RenderServer.MainViewExposure`/`ExposureOf`.
  Labels scale their record colours; an unshaded panel's emission energy is `Brightness / exposure` (rewritten past 0.5 %).
- **Mesh panel input (G11.9):** `UiPanel3D.TryRayToPixel` on a custom `Mesh` (Möller–Trumbore, front faces, UVs); a held
  pointer that leaves the mesh keeps its pixel.
- **Editor (G11.7):** `Viewport/PanelStandIns.cs` binds `UiPreviewData` stand-ins in the active tab's panels and reloads
  their documents; `ViewportController.PanelStandIns`. `Tests/QA/world-ui.qa` captures the terminal;
  `docs/images/editor-world-ui-panel.png`.
- Tests: `Label3DTests` reworked for the glyph run (+ the batching rule, outline order, rebuilds, the lit mesh),
  `UiPanel3DTests` (bent mesh UVs, IgnoreExposure emission), `PostExposureTests`, editor
  `APanelInTheSceneViewShowsItsDocumentWithStandInData`; render tests `label3d-batch` (+ allocation run) and
  `label3d-exposure`.
- Docs: world-space-ui (batched renderer, ignoring exposure, mesh input, editor stand-ins, testing, non-goals, changes,
  decided questions, history), editor, testing, post-processing, color-pipeline, neon links, milestones (G11 ✅),
  AGENTS.md, README, landing page (feature text, showcase).

## Decisions

- [ADR 0257](../decisions/0257-world-ui-owner-answers.md): the owner's answers (2: opt-in IgnoreExposure; 5: no depth
  occlusion; 6 and 7: keep the defaults).
- [ADR 0258](../decisions/0258-batched-label3d-renderer.md): batched labels with page groups and CPU poses; lit labels and
  shadow-casting cutouts stay meshes.
- ADR numbers: G6's session claimed 0255/0256 (shared notes folder), so these are 0257/0258.

## Dead ends

- The proposal's "one draw per atlas page" would break blending order across fonts; page groups with a per-slot branch
  in the shader keep order and need only dynamic indexing (non-uniform indexing is not enabled).
- A vertex shader shared by the colour and ID passes left an unread output in each; `Text3DId.vk.vert` includes
  `Text3D.vk.vert` with `TEXT3D_OBJECT_ID` so each writes only what its fragment reads.
- `timeout` is not on this Mac: the first builds "ran" through a missing command (grep hid the error).
- `label3d-layout`'s golden moved by up to 0.7 % of pixels: batched labels rasterise glyphs into the atlas in another
  order, so filtering at glyph edges differs (visually identical; re-recorded and inspected).

## Verified

- See the PR for the gate runs (build, Release `-warnaserror`, unit, render, format, shaders, check.sh).
- `label3d-batch` on moltenvk: 254 of 300 labels drawn (46 culled), 1 559 glyphs, 3 draws over 2 pages, a front label
  picked through the object-ID pass, 0 B over 120 frames. `label3d-exposure`: the IgnoreExposure label and panel match
  the exposure-1 run; the others brighten. The ADR 0200 `label3d` golden is unchanged.
- Editor QA capture of the Demo terminal in the scene view, showing "Gate open" / "Close gate" from stand-ins.

## Not verified

- IgnoreExposure under auto exposure on a GPU (the maths is unit-tested; the render test uses manual exposure).
- The 1 000-label prepare benchmark and `baseline.json` (quiet machine only).

## Next

- Quiet-machine benchmarks: `WorldUiBenchmarks` into `baseline.json`, a label-batch case.
- Labels sorting among other blended surfaces, if a game needs it (ADR 0258's revisit).
