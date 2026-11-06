# ADR 0258 — Unshaded Label3Ds draw through a batched text renderer with page groups

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G11 (G11.9 batched text)
- **Docs:** docs/design/world-space-ui.md#batched-text-renderer, memory/plans/2026-11-07-world-ui-g11-rest.md
- **Builds on:** [ADR 0251](0251-label3d-on-the-mesh-batcher.md) (labels on the mesh batcher, `FixedSize` on the CPU)

## Context

ADR 0251 left every `Label3D` an `ArrayMesh` on the mesh batcher: one draw per label and atlas page, up to 2 000 draws for
1 000 outlined labels. The proposal designed a `Text3DRenderer`: labels posed in the vertex shader, one draw per atlas
page and pipeline. Two things in it do not hold: drawing per page breaks back-to-front blending between labels of
different pages or fonts, and the proposal's labels lose lighting and the cutout shadows ADR 0251 shipped.

## Decision

- **A renderer beside the mesh batcher** (`Text3DRenderer`, one per render server, like `ParticleRenderer`) draws every
  unshaded label of a world after the blended meshes and visuals. A `Shaded` label, and an `AlphaCut = Discard` cutout
  that casts shadows, stay meshes (`Label3D.IsBatchedText`).
- **Retained glyph runs:** each label keeps its glyph quads in label space, rebuilt only on change, from the same layout
  code as the mesh.
- **Page groups:** atlas pages take slots in groups of eight; one descriptor set per group holds a sampler and eight
  images, and each glyph names its slot. The fragment shader samples the slot under a branch per slot with gradients
  taken first (dynamic indexing only, no non-uniform indexing feature needed). A draw then spans pages and fonts.
- **CPU poses:** billboard and `FixedSize` go into each label's model matrix per view (`Billboard.Face`/`FixedSize`, as
  ADR 0251), so culling, sorting and picking use the drawn pose.
- **Per view:** cutouts, then blended labels by `TextRenderPriority` and back to front, then `NoDepthTest` labels; a label
  record per label (storage buffer) and a 40-byte instance per glyph (vertex buffer), both per-frame-slot rings; one draw
  of six vertices per glyph for each run of equal pipeline and page group. The object-ID pass draws the same quads.

## Ruled out

- One draw per atlas page (the proposal): breaks blending order across pages; or sorting by page first, which breaks it
  differently.
- A texture array per font: pages would have to be copied into array layers and every font's array re-created as glyphs
  are added.
- Non-uniform descriptor indexing: a device feature the renderer does not enable.
- Posing in the vertex shader: picking, culling and the mesh path would disagree with the shader's pose.

## Consequences

- 300 labels over two fonts draw in three calls (the `label3d-batch` render test) and frames allocate nothing.
- Batched labels cast no shadows, are not in the depth prepass (no motion vectors) and draw after every other blended
  surface, so a blended mesh in front of a label does not cover it in blending order.
- A world with more than eight resident pages needs a draw per page group per pipeline.

## Revisit when

- Labels must sort among other blended surfaces (a shared transparent sort across renderers).
- SDF text (open question 1) is wanted: the run, groups and records stay; the shader and atlas change.
