# ADR 0256 — Generated LODs through meshoptimizer in an in-house native shim (`mfmeshopt`)

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G6 (part: G6.4)
- **Docs:** docs/design/rendering-features.md#generated-lods, docs/design/natives.md,
  memory/plans/2026-11-07-g6-pbr-default-and-generated-lods.md

## Context

ADR 0245 built mesh LODs from code and authored `_LODn` nodes and reserved `generateLods` in the model sidecar until a
mesh simplifier was chosen (proposal open question 1: meshoptimizer through a `Native/` shim, or an in-house quadric
simplifier). The owner chose meshoptimizer (MIT, what Godot uses) on 2026-11-07, approving the native dependency.

## Decision

- **Shim:** `Native/MeshOpt` builds `mfmeshopt` (`mfmeshopt.dll`, `libmfmeshopt.so`, `libmfmeshopt.dylib`) like `mfsvg`:
  meshoptimizer v1.3 as a submodule (`external/meshoptimizer`) compiled static and hidden, our C ABI in
  `include/mfmeshopt.h` (`mfmeshopt_abi_version`, `_simplify`, `_simplify_scale`, `_optimize_vertex_cache`), export
  lists, a CTest smoke test, a `natives.lock` component. The binaries live in `MainframeEngine/runtimes/<rid>/native`
  (the importer runs in the editor and in games that import at run time).
- **Generation** (`ModelLods.Generate`, during import when `generateLods` is true): per surface without authored levels
  and not skinned, simplify with normals as attributes, halving the index count per level until a level saves less than
  15 % or 8 levels exist; borders are locked when the mesh has more than one surface (no cracks between materials).
  Each level's `Error` is meshoptimizer's absolute error in mesh units, forced ascending; indices are vertex-cache
  optimised. Levels are index-only (`MeshLod.Indices`), so no vertex memory is added.
- **Missing library** (a platform without the binary): import logs one warning and builds no levels.
- `generateLods` stays false by default.

## Ruled out

- An in-house quadric simplifier: meshoptimizer's seam and attribute handling is years of work and the same code
  Godot's importer uses, so levels match Godot's.
- P/Invoking meshoptimizer's own exports without a shim: no ABI version check, and its symbol set changes per release.
- A NuGet wrapper (Meshoptimizer.NET and similar): third-party binaries we cannot rebuild, and a package the repo has
  not approved.

## Consequences

- One more native component to rebuild on three OSes (`natives.yml`); adding it changed `Native/CMakeLists.txt`, an
  input of every component, so one full `natives.yml` run restamps every binary.
- Imported models get LODs without authoring them; levels share the surface's vertices, so UV seams and normals stay.

## Revisit when

- Skinned meshes need generated LODs, or LOD cross-fade needs per-level geometry: meshoptimizer's
  `simplifyWithUpdate`/sparse modes may fit.
