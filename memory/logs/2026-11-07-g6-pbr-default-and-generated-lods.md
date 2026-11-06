---
summary: >-
  G6 closed: PBR is the default shading (ADR 0255; StandardMaterial3D v2 and .mfmeta format 2 keep old content
  Blinn-Phong) and generateLods builds index-only levels through meshoptimizer 1.3 in the new mfmeshopt shim (ADR 0256).
---

# 2026-11-07: G6 close-out — PBR by default, generated LODs

Branch `feature/g6-pbr-default-lods`. Plan:
[G6 close-out](../plans/2026-11-07-g6-pbr-default-and-generated-lods.md). As built:
[Rendering features](../../docs/design/rendering-features.md) (Generated LODs, Changes from the proposal).

## What changed

- **PBR default (owner, 2026-11-07):** `StandardMaterial3D.ShadingMode` and `StandardMaterial3D.Default` are `Pbr`;
  `[SerializedVersion(2)]` with a v1 migration writing `ShadingMode: BlinnPhong` when absent. `PropertyApplier` now
  applies what a migration adds to an entry without `props` (it dropped it before). `AssetMeta.Format` (new sidecars
  2, absent = 1); `ModelImportSettings.Shading` defaults to `Pbr`, `LegacyDefault` for format-1 sidecars; the importer
  sets the mode on every material it creates.
- **Content kept as it looked:** code-built materials in the Demo, Neon, Forest, render-test host, the Showcase fixture
  builder, editor smoke QA and `RemovedNodeTypes` pin `ShadingMode.BlinnPhong` (a brace-matching script, then a check
  that no regenerated Demo/Showcase material lacks a mode). Demo scenes and `Tests/Assets/Scenes/Showcase.mfscene`
  regenerated: only `"v": 2` and `"ShadingMode": "BlinnPhong"` were added. Other committed scenes, `.mfres` files and
  every model sidecar load through the migrations unchanged on disk.
- **Generated LODs (owner approved meshoptimizer, 2026-11-07):** `Native/MeshOpt` (`mfmeshopt`: meshoptimizer v1.3
  submodule, C ABI `mfmeshopt.h`, export lists, `mfmeshopt_smoke`), `MF_NATIVES_MESHOPT`, `build.sh`,
  `natives-lock.sh`, `natives.yml`; binaries for osx (universal, local) and linux-x64 (Docker ubuntu:22.04, GCC 11,
  glibc 2.34). `MeshSimplifier` (`IsAvailable`, `GenerateLods`) and `ModelLods.Generate` behind `"generateLods": true`.
  Package excludes for the tool folders.
- **Tests:** `PbrDefaultTests` (scene and `.mfres` migrations, v2 round trip), `ModelPbrImportTests` (PBR default,
  format-1 sidecars, new sidecars at format 2), `MeshSimplifierTests` (ABI, sphere halving, locked borders, small,
  authored, skinned and shared meshes), `ModelLodImportTests.GenerateLodsBuildsIndexOnlyLevelsOnImport`.
- **Docs:** rendering-features (open questions 1 and 2 closed, Generated LODs), materials-and-meshes, asset-pipeline
  (sidecar `format`), scene-serialization (changed defaults), natives, distribution-nuget, THIRD_PARTY_NOTICES,
  AGENTS.md, README and landing page (the milestones row and state lines: State changes below).

## State changes

Pending for the state sync (ADR 0280); this PR does not edit these files.

- `docs/milestones.md`, the G6 row's status cell: replace "🚧 all built (ADRs 0150, 0165, 0206, 0245, 0246);
  owner-pending: the PBR default flip (open question 3) and the LOD simplifier for generated LODs (open question 1)"
  with "✅ (ADRs 0150, 0165, 0206, 0245, 0246); PBR is the default shading, old content keeps Blinn-Phong through
  migrations (ADR 0255); generated LODs through meshoptimizer, `Native/MeshOpt` (ADR 0256)".
- `memory/context/current-state.md`, the **G6** bullet: replace "PBR + IBL, SSAO, 3D particles, mesh LODs (ADR 0245),
  forward decals (ADR 0246), `.hdr` skies; owner-pending: PBR default, LOD simplifier." with "PBR + IBL (the default
  shading since ADR 0255: `StandardMaterial3D` v2 and `.mfmeta` `format` 2 keep older content Blinn-Phong), SSAO, 3D
  particles, mesh LODs (ADR 0245; `generateLods` through meshoptimizer in the `mfmeshopt` shim, ADR 0256), forward
  decals (ADR 0246), `.hdr` skies: G6 complete."
- `memory/context/current-state.md`, Next steps item 2: remove "G6's PBR default (owner); ".

## Decisions

- [ADR 0255](../decisions/0255-pbr-is-the-default-shading.md): PBR default; old files keep Blinn-Phong through a
  material version and a sidecar format, code in the repo pins it.
- [ADR 0256](../decisions/0256-meshoptimizer-shim-for-generated-lods.md): meshoptimizer through `mfmeshopt`; index-only
  levels halving per level, borders locked for multi-surface meshes, skinned surfaces skipped.

## Dead ends

- GCC 11 warns (`-Wsign-conversion`) inside `meshoptimizer.h`'s templates, which our `-Werror` turned fatal on Linux
  only: the header is a `SYSTEM` include now. The Linux smoke test also needed `-lm`.
- `git submodule add` stages the remote's default branch; checking out `v1.3` afterwards needs a `git add` of the
  gitlink, or `natives.lock` records the wrong commit.
- Lowering the normal weight does not make a UV-seamed sphere's coarse levels cheap: positions alone give 0.61 at 123
  triangles, the seam's cost. The weight stays 0.5.

## Natives from CI

- The owner approved the PR's one `natives.yml` run (2026-11-07). Its `natives-all` replaced every native (the local
  `mfmeshopt` builds included) and restamped `natives.lock`; `natives-lock.sh verify` passes.
- `natives-all` carries `steam_api64.dll` as a Git LFS pointer (the `collect` job checks out without LFS and copies the
  whole `runtimes/` tree): restored from git rather than committed. Noted in `natives.md`.

## Verified

- `just build` (0 warnings), Release `-warnaserror` build, `just test` (3482 engine, 1706 editor, 464 video),
  `just format-check` (and the Demo, Neon, Forest solutions), `just shaders-check`, `check.sh`; Demo 133, Neon 215,
  Forest 227 tests.
- Render tests: moltenvk 238 (8 tests, 9 frames re-recorded: the floor without a material is PBR now, only its shadowed
  part lightens); lavapipe in Docker 238/238 after re-recording the same 9 frames, "Every compared frame has a golden".
- Linux unit tests in Docker: the new simplifier and PBR tests pass on linux-x64 against the committed `.so`; one
  unrelated failure, `CrashProcessTests.WithTheDumpVariablesANativeFaultLeavesADump` (createdump under x86_64 emulation).
