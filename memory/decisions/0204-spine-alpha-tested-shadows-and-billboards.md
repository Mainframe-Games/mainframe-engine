# ADR 0204 — SpineNode alpha-tested shadows and billboards

- **Date:** 2026-10-25
- **Status:** accepted
- **Milestone:** — (Crash Site Defense in 3D, mainframe-engine#122)
- **Docs:** docs/design/spine.md#shadows, docs/design/spine.md#billboards

## Context

Crash Site Defense is moving to full 3D and keeps its Spine crew as `SpineNode`s until the engine's skeletal animation
ships. The crew stand under a low dusk sun, so their shadows are long and in plain view, and a torch carried by the crew
casts point-light shadows too. Two problems stood in the way:

- **Blocky shadows.** `SpineRenderer` drew a positions-only buffer with `ShadowSystem.DoubleSidedShadow2DPipeline`/
  `DoubleSidedShadowPointPipeline`. Every region attachment cast its whole quad, so a character's shadow was a cluster of
  rectangles with the transparent corners filled in.
- **No facing.** The crew face a fixed camera (about 45° pitch), lean back to meet it and flip by facing. `Sprite3D` and
  `Label3D` have `Billboard` (ADR 0199), but `SpineNode` is not a `GeometryInstance3D`: it draws itself, is not batched,
  and has no bounds.

## Decision

**Alpha-tested casters.** `ShadowSystem.EnsureSpineCasters` builds two double-sided pipelines once, when the first
`SpineRenderer` with a shadow system is created:

- `Spine/SpineShadow2D.vk.vert` with `SpineShadow.vk.frag` for cascades and spot atlas tiles.
- `Spine/SpineShadowPoint.vk.{vert,frag}` for cube faces.

They read the colour pass's own vertex buffer (stride 40). Set 1 is the atlas page, as set 1 is the material for the
cutout `StandardMaterial3D` casters. The fragment discards where `atlas alpha × tint alpha < threshold`.

- The layouts come from `CreateCasterLayout`, so set 0 (the pass's light view-projection, bound with a dynamic offset by
  `RecordPass`) and the push ranges (64 B vertex; 80 B vertex+fragment for points) are those of the plain caster layouts.
  Set 0 stays bound and compatible, and nothing else in the shadow pass changes.
- The 2D push range has no room for the threshold. The threshold rides in the vertex's spare float (offset 36, the old
  unused `TextureIndex`), next to the tint alpha at offset 32, and the casters read both as one `float2` attribute. That
  also gives **slot-alpha fading for free**: a fading slot or skeleton fades its shadow (the issue's optional item 3).
- `SpineNode.ShadowAlphaThreshold` is exported and serialised, with a range of 0–1 and a default of 0.5. At 0 nothing is
  discarded, so it casts the quad shadow it cast before.
- The colour pass and the shadow passes share one upload per frame slot after each `BuildVertices`; the separate
  positions array and buffers are gone.
- The page set layout comes from `SpineRenderer.CreateTextureSetLayout`, used for the colour pass's set 2 and the casters'
  set 1. The two layouts are identically defined, so one descriptor set per page serves both.

**Billboards.** `SpineNode.Billboard` (`BillboardMode`) is done on the CPU, as in ADR 0199. `SpineNode` stays a
`VisualInstance3D`; it does not move under `GeometryInstance3D`.

- `OnProcess` builds the vertices in skeleton space as before.
- `Draw(camera)` pushes `Billboard.Face(SpineScale × ModelMatrix, mode, camera.ViewMatrix)` as the model matrix and
  derives the lighting normal from it.
- Shadows need the camera too, which `DrawShadow2D/Point` do not get. A new internal hook,
  `VisualInstance3D.PrepareShadowCasting(ICamera?)`, is called by `RenderServer.RenderShadows` once per shadow render on
  every non-batched caster, with the camera the shadows are rendered for (the main view, or the sub-viewport that owns the
  shadow maps). `SpineNode` stores the billboarded matrix there. This matches `Sprite3D`, which casts from the quad the
  collecting view sees (Godot's `MAIN_CAM_INV_VIEW_MATRIX`).
- `FlipX` still mirrors the skeleton (its `ScaleX` sign, applied to the vertices), billboarded or not, and `ZSpacing`
  stays along the billboard's +Z, toward the camera.

## Consequences

- One shader pair per shadow kind, two pipelines, and two pipeline layouts plus a set layout in `ShadowSystem`, created
  only when Spine is used. The plain double-sided pipelines stay (public API) but the engine no longer uses them.
- Shadow passes now bind one atlas page per batch, so there is one draw per page batch per pass instead of one draw per
  pass. The fragment discard costs early-Z in those passes. Spine casters are few, so neither matters.
- The default 0.5 changes existing Spine shadows: they become shaped. The `spine` golden was re-recorded, and other
  scenes with SpineBoy can differ within tolerance.
- Several views in one frame see the colour pass billboarded per view, but the shadows come from one view. This is the
  limitation `Sprite3D` already has.
- The editor's selection box (none for Spine today) would not follow the billboard.
- The engine's own skeletal animation can reuse the same caster layout idea when it replaces Spine for these crews: one
  texture set at set 1, and per-vertex data for the alpha test.
