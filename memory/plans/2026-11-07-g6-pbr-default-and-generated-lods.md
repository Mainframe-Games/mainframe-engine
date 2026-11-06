# Plan: G6 close-out — PBR by default, generated LODs through meshoptimizer

**Status:** in progress
**Docs:** [Rendering features](../../docs/design/rendering-features.md) (Changes from the proposal "PBR stays opt-in",
Open questions 1 and 2), [Materials & meshes](../../docs/design/materials-and-meshes.md#standardmaterial3d),
[Asset pipeline → PBR materials](../../docs/design/asset-pipeline.md#pbr-materials-g61),
[Scene serialization](../../docs/design/scene-serialization.md) (versions and migrations),
[Native libraries](../../docs/design/natives.md), [ADR 0150](../decisions/0150-pbr-shading-and-sky-ibl.md),
[ADR 0245](../decisions/0245-mesh-lod-by-projected-error.md)
**Started:** 2026-11-07

## Goal

The owner answered G6's two open questions on 2026-11-07:

- **PBR is the default.** `StandardMaterial3D.ShadingMode` defaults to `Pbr` (so does `StandardMaterial3D.Default`, the
  material of surfaces without one), and the model importer's `shading` setting defaults to `pbr`. Content written
  under the old default keeps Blinn-Phong: `StandardMaterial3D` becomes `[SerializedVersion(2)]` with a v1 migration
  that writes `ShadingMode: BlinnPhong` when the property is absent; `.mfmeta` sidecars gain a `format` (absent = 1,
  new sidecars 2) and a model sidecar below format 2 without `shading` imports as Blinn-Phong. Code that builds
  materials in the examples, render tests and the template pins `ShadingMode.BlinnPhong` where it relied on the old
  default, so their look does not change.
- **Generated LODs.** A new native component `mfmeshopt` (`Native/MeshOpt`: a C ABI over meshoptimizer v1.3, MIT,
  submodule) built like `mfsvg`, committed for osx (universal, local) and linux-x64 (Docker), win-x64 from `natives.yml`.
  `generateLods: true` in a model `.mfmeta` simplifies each surface without authored levels into index-only levels
  (halving the index count per level, measured absolute error, vertex-cache optimised), skinned surfaces excepted.

## Not in scope

- `generateLods` defaulting to true (stays false: ADR 0245's promise that projects do not change).
- LOD cross-fade, LODs for skinned meshes, vertex tangents (open question 3 stays open).
- Changing the look of any existing scene, example or golden beyond surfaces drawn with `StandardMaterial3D.Default`
  or an imported model without a sidecar.

## Steps

- [x] ADRs 0255 (PBR default and how old content keeps its look) and 0256 (meshoptimizer shim). Verified by: files in
  `memory/decisions/`.
- [x] `StandardMaterial3D` default `Pbr`, `[SerializedVersion(2)]`, migration; `PropertyApplier` applies a migrated bag
  when a resource has no `props`. Verified by: unit tests — a v1 material without `ShadingMode` (inline and `.mfres`,
  with and without props) loads as Blinn-Phong, a v1 `Unshaded`/`Pbr` keeps its value, a v2 file without the property
  loads as PBR, round trip writes `v: 2`.
- [x] `AssetMeta.Format`; model settings default `Pbr`, legacy sidecars Blinn-Phong; importer sets the mode explicitly.
  Verified by: `ModelPbrImportTests` cases for a format-less sidecar, a format-2 sidecar, no sidecar, explicit `shading`.
- [x] Pin `ShadingMode.BlinnPhong` in code-built materials of Demo, Neon, Forest, render-test host, benchmarks, editor
  QA and template; regenerate the Demo scenes. Verified by: `Demo.Tests`, the Demo scene diff only adds
  `ShadingMode`/`v`, render goldens unchanged except the default-material ones.
- [x] `Native/MeshOpt` (CMake, header, shim, exports, CTest smoke), top-level option, `build.sh`, `natives-lock.sh`,
  `natives.yml`; build + stage macOS; build linux-x64 in Docker (ubuntu:22.04, like CI). Verified by: CTest
  ~~`mfmeshopt_exports`~~ (dropped: the smoke test calls all five exports) `mfmeshopt_smoke` on both, `natives-lock.sh` lists the new binaries.
- [x] Managed `MeshSimplifier` (LibraryImport, ABI check, `IsAvailable`) and `ModelLods.Generate`; importer wires
  `generateLods`; package excludes. Verified by: `MeshSimplifierTests` (ABI, a sphere halves per level with ascending
  errors, borders locked across surfaces), `ModelLodImportTests` generated cases (authored levels win, skinned skipped,
  missing library warns).
- [x] Goldens: re-record only those that change (moltenvk locally, lavapipe in Docker), inspect PNGs. Verified by:
  `just test-render` and `just render-tests-linux` green.
- [x] Docs: rendering-features (open questions 1–2 closed, Changes from the proposal, LOD generated section),
  materials-and-meshes, asset-pipeline, scene-serialization (StandardMaterial3D v2), natives, distribution-nuget,
  THIRD_PARTY_NOTICES, AGENTS.md structure line, milestones G6 ✅, README/landing line. Verified by: `check.sh`.
- [ ] Gates, log, PR with Auto-fix; ask the owner before the push that triggers `natives.yml`. Verified by: gates
  output in the log.

## Done when

- `new StandardMaterial3D().ShadingMode == ShadingMode.Pbr`, and every committed `.mfscene`/`.mfres`/model sidecar in
  the repo loads with the shading it had before (tests above pass).
- `generateLods: true` on a test model produces levels with ascending errors on macOS and Linux.
- Every gate in `.agents/workflow.md` passes locally; the G6 row in `docs/milestones.md` is ✅; a log is in
  `memory/logs/`; the PR is open with Auto-fix on.

## Open questions

- Windows `mfmeshopt.dll` needs one `natives.yml` run (Actions minutes): the owner says when.
