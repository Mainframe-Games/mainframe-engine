# ADR 0255 — PBR is the default shading; content written before keeps Blinn-Phong through migrations

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G6 (part: G6.1)
- **Docs:** docs/design/rendering-features.md, docs/design/materials-and-meshes.md#standardmaterial3d,
  docs/design/asset-pipeline.md#pbr-materials-g61, memory/plans/2026-11-07-g6-pbr-default-and-generated-lods.md

## Context

ADR 0150 built PBR (`ShadingMode.Pbr`, ORM, sky IBL) as an opt-in mode; the G6 proposal's open question 3 asked whether
the default should flip, as Godot's is. The owner answered on 2026-11-07: flip it, so new materials and new imports are
PBR. Scenes, `.mfres` files and model sidecars omit default values, so a file written under the old default carries no
`ShadingMode` (no `shading` for models) and would silently become PBR.

## Decision

- `StandardMaterial3D.ShadingMode` defaults to `Pbr`, and so does `StandardMaterial3D.Default` (surfaces without a
  material). Model import's `shading` defaults to `pbr`.
- **Materials in files:** `StandardMaterial3D` becomes `[SerializedVersion(2)]`; the v1 migration adds `ShadingMode:
  BlinnPhong` when the property is absent. A v1 material with no `props` at all runs the migration too (the applier used
  to drop a migrated bag when there were no props). The next save writes `v: 2` and the explicit value.
- **Model sidecars:** `.mfmeta` gains a top-level `format` (absent = 1). New sidecars are written at format 2. A model
  sidecar below 2 without `shading` imports Blinn-Phong; a model with no sidecar at all is new content and imports PBR.
- **Code:** the examples, render tests, benchmarks and editor QA that built materials relying on the old default pin
  `ShadingMode.BlinnPhong`, so their look and goldens stay. Only goldens drawn with the default material change.

## Ruled out

- Hand-editing every committed scene and sidecar: misses user projects, which the migration covers.
- A project-wide "legacy shading" setting: two defaults to reason about forever, for a one-time change.
- Leaving `StandardMaterial3D.Default` Blinn-Phong: the fallback would differ from `new StandardMaterial3D()`.

## Consequences

- New content looks like Godot's out of the box (metallic 0, roughness 1 unless set); old content keeps its look until
  someone switches it.
- Code-built materials in user projects that relied on the default change look on upgrade (no file carries a version
  for them); the release notes must say so.
- Sidecars now carry a version, which later importer default changes can reuse.

## Revisit when

- A later importer or material default changes: bump the sidecar `format` or the material version again rather than
  adding another mechanism.
