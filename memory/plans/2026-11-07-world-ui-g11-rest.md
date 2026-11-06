# Plan: finish G11 world-space UI (batched labels, panel stand-ins, IgnoreExposure, mesh panel input)

**Status:** done, see [log](../logs/2026-11-07-world-ui-g11-finish.md)
**Docs:** [World-space UI proposal](../../docs/design/world-space-ui.md) (`Label3D` rendering, `UiPanel3D` edit mode,
task list G11.7/G11.9, open questions 2, 5, 6, 7), [World-space UI](../../docs/design/world-space-ui.md) (as built),
[ADR 0251](../decisions/0251-label3d-on-the-mesh-batcher.md) (what the batched renderer replaces),
[ADR 0206](../decisions/0206-gpuparticles3d.md) (`ParticleRenderer`: the template for a renderer beside the mesh batcher),
[Editor → UI preview](../../docs/design/editor.md#ui-preview) (stand-in data)
**Started:** 2026-11-07

## Goal

G11 is complete: every unshaded `Label3D` of a world is drawn by one `Text3DRenderer` (one draw per atlas page and
pipeline), panels in the editor's scene view show their documents with the UI preview's stand-in data, labels and
unshaded panels can opt out of exposure (`IgnoreExposure`), and panels with a custom `Mesh` take pointer input. The
owner's answers to open questions 2, 5, 6 and 7 are recorded, and the proposal ships into `docs/design/world-space-ui.md`.

## Not in scope

- SDF/MSDF text (open question 1 stays decided as coverage bitmaps; nothing asks for it now).
- Depth-buffer marker occlusion (owner, question 5: physics rays only).
- A bundled CJK face; `baseline.json` entries (quiet machine only).
- Lit (`Shaded`) labels on the batched path: they stay on the mesh batcher.

## Design notes (decided here)

- **Batched labels (G11.9 / ADR 0258).** `World3D.LabelList` holds every `Label3D`. A label is batched unless it is
  `Shaded`, or an `AlphaCut = Discard` cutout that casts shadows (both keep ADR 0251's mesh, so lighting and cutout
  shadows do not regress); a batched label returns no render mesh. `Text3DRenderer` (`Src/Rendering/Text/`), one per
  render server like `ParticleRenderer`:
  - `Prepare(world)` before the frame records: each batched label rebuilds its retained glyph run only when it changed
    (`Label3D` keeps the layout code and writes `TextGlyph`s instead of mesh arrays), and font pages are uploaded through
    `TextureGpu` (one material set per page).
  - `Draw(world, camera, phase, invExposure)` in the scene pass, after the blended meshes and blended visuals: per view,
    each label's model matrix comes from `Billboard.Face`/`Billboard.FixedSize` on the CPU (as ADR 0251), it is culled
    by its bounds and `VisibilityRangeBegin/End`, cutouts first, then blended labels sorted by `TextRenderPriority` then
    back to front, then `NoDepthTest` labels the same way. Label records (model, colours, flags, object id) go to a
    per-frame-slot storage ring, glyph instances (rect, UV, label index, outline bit) to a per-frame-slot vertex ring; one
    instanced quad draw per run of equal (page, pipeline). Within a label the outline draws first unless
    `OutlineRenderPriority` > `TextRenderPriority`.
  - Pipelines: depth test × blend/cutout × cull × TAA-reactive (main view), built lazily; `Text/Text3D.vk.{vert,frag}.slang`
    reads set 0 (fog), set 2 (sampler, page image, label records); the fragment matches the unshaded mesh path
    (texture × colour, `applyFog`, alpha 0.5 for cutouts).
  - Object IDs: `DrawObjectIds(world, camera)` with an ID fragment (cutouts discard), added to the root and sub-viewport
    pickers' callbacks, so labels pick and box-select.
  - Stats: `Text3DRenderer.Stats` (labels, glyphs, draws) for tests and the renderer debug window.
- **`IgnoreExposure` (owner, question 2).** `Label3D.IgnoreExposure` and `UiPanel3D.IgnoreExposure`, off by default.
  The view's effective exposure (manual × the world's `ExposureFor` × auto exposure's `scale / adapted luminance`) is
  known on the CPU: `AutoExposure` copies its 1 × 1 adapted luminance into a per-frame-slot readback buffer and reads it
  back when the slot returns (a 2–3 frame lag, smoothed by adaptation anyway). Labels multiply their colour by
  1 / exposure in `Text3D.vk.frag` (push constant); an unshaded panel's emission energy becomes `Brightness / exposure`
  (updated when it moves more than 0.5 %). ACES still applies. Shaded labels and panels ignore the flag.
- **Panel stand-ins in the editor (G11.7).** The editor's viewport keeps a `UiPreviewData` per `UiPanel3D` of an edited
  scene: when the panel's context exists and its documents' sources are known, it binds stand-ins from
  `RmlBindings.ParseFile` of every document and reloads the documents (the panel renders once after the reload); it
  rebinds when a document's source changes or a hot reload touches it, and drops entries for panels that left (code
  reload: `ReleaseEditorReferences`).
- **Mesh panel input (G11.9).** `TryRayToPixel` on a panel with a `Mesh`: Möller–Trumbore over the mesh surfaces' CPU
  arrays in local space, nearest front-facing hit, UV interpolated → panel pixel. Capture past the edge keeps the quad's
  plane only for the default quad (a mesh panel releases at the edge).
- **Open questions 6 and 7:** the owner keeps the defaults (ADR 0253's layers-first; panels are scene geometry), recorded
  in one decision with 2 and 5.

## Steps

- [x] **Decision.** ADR for the owner's answers to questions 2, 5, 6, 7 (`mf-decide`). Verify: file in `memory/decisions/`.
- [x] **Effective exposure.** `AutoExposure` readback, `RenderServer.ViewExposure` (root and sub-viewports). Verify:
      unit test of the exposure maths (`PostProcessSettings`), render test below.
- [x] **`Text3DRenderer` + shaders.** Renderer, `Text3D.vk.{vert,frag}.slang`, `TextId.vk.frag.slang`, `.spv`,
      `shaders.lock`; `World3D.LabelList`; `Label3D` glyph runs and `IsBatched`; draw hooks in `RenderServer.DrawWorld`
      and the pickers. Verify: `Label3DTests` (runs, batching rule, sort order through the CPU half), the `label3d` and
      `label3d-layout` goldens re-recorded (inspected), validation clean.
- [x] **Batching render test.** `label3d-batch`: 300 labels over two fonts; the scene checks `Text3DRenderer.Stats.Draws`
      ≤ one per page and pipeline; 0 B per frame. Verify: render test + golden (moltenvk, lavapipe).
- [x] **`IgnoreExposure`.** Label and panel properties, the push constant and panel energy. Verify: unit tests
      (`Label3DTests`, `UiPanel3DTests`: energy follows exposure, 0 B), render test `label3d-exposure` (manual exposure 4:
      an `IgnoreExposure` label's swatch equals the exposure-1 ACES value; the other is brighter).
- [x] **Mesh panel input.** `TryRayToPixel` on meshes. Verify: `UiPanel3DTests` (a rotated `PlaneMesh` and a curved mesh:
      pixel matches the UV, back faces miss).
- [x] **Editor stand-ins.** `PanelStandIns` in the viewport. Verify: editor test (a panel document's `{{title}}` shows the
      stand-in text: the bound model exists in the panel's context), QA capture of the Demo terminal in the scene view.
- [x] **Docs and ship.** Move the proposal into `world-space-ui.md` (+ "Changes from the proposal"), repoint links, milestones
      ✅, README / landing page, editor screenshot, `AGENTS.md`, log; gates; PR with Auto-fix.

## Done when

- `Label3D` (unshaded) draws through `Text3DRenderer`; `label3d-batch` shows ≤ 1 draw per (page, pipeline) for 300 labels
  and 0 B per frame; `label3d`/`label3d-layout` goldens re-recorded and inspected on both drivers.
- `IgnoreExposure` exists on `Label3D` and `UiPanel3D`, off by default, with unit tests and the `label3d-exposure` render test.
- Panels with a `Mesh` answer `TryRayToPixel`; a test covers it.
- An edited scene's panel shows stand-in data (editor test), with a scene-view screenshot in `docs/images/`.
- `docs/design/future/world-space-ui.md` is gone, its content in `docs/design/world-space-ui.md` with "Changes from the
  proposal"; no links point at the old path; G11 is ✅ in `docs/milestones.md`.
- The gates in `.agents/workflow.md` pass; a log is in `memory/logs/`; a PR is open with Auto-fix on.
