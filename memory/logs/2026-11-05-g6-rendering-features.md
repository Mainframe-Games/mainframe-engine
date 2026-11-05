---
summary: >-
  G6 finished on Vulkan: mesh levels of detail by projected error with authored _LODn import (ADR 0245), forward
  decals with per-instance masks and a shared atlas (ADR 0246), .hdr sky panoramas, Sky.RadianceSize/ProcessMode, and
  PBR model import behind "shading": "pbr". The PBR default flip and the LOD simplifier stay with the owner.
---

# 2026-11-05: G6 rendering features (LOD, decals, the rest of IBL and PBR import)

Branch `feature/g6-rendering-features`. Plan: [G6 rendering features](../plans/2026-11-05-g6-rendering-features.md).
As built: [Rendering features](../../docs/design/rendering-features.md) (the proposal's content moved there; the
proposal is deleted).

## What changed

- **LOD (G6.4):** `MeshLod` on `MeshSurface.Lods` (index-only or own geometry), appended in `MeshGpu`'s buffers
  (`MeshGpu.Layout`, `Range`); `LodSelection` in `MeshRenderer.Prepare` (`LodBias`, `RenderServer.MeshLodThreshold`,
  `rendering.meshLodThreshold`, Project Settings); casters at the camera's level; 3 lod bits in the opaque and caster
  keys; `ModelLods` merges `<name>_LODn` siblings on import (`lodErrors`; `generateLods` reserved).
- **Decals (G6.5):** `Decal` node; `DecalSelection` per view (nearest 32, masks, UBO at set 0 b8); `DecalAtlas` (b9);
  `MeshInstanceData` 80 → 84 bytes (decal mask, location 10); `include/decals.slang` applied in `Mesh.vk.frag` and
  `TerrainSplat.vk.frag`; `MAX_DECALS` in `limits.json`; editor scene icon, box gizmo and size handles
  (`DecalHandles`); icons `sticker`, `stairs-down`.
- **IBL (G6.2):** `.hdr` panoramas as RGBA16F (`HdrImage`, `GpuTexture.Create2DTexels`); `Sky.RadianceSize`,
  `Sky.ProcessMode`.
- **Import (G6.1):** `"shading": "pbr"` reads glTF factors and maps (`OrmPacker.PackGltf`); Blinn-Phong stays the default.
- **Borrowed fix:** `SurfaceShaderBuild.RealPath` from `fix/stale-goldens-shader-lock` (f4e886ae), needed because
  `limits.slang` changed and every surface shader's lock was rewritten with temp paths without it.
- **Docs:** rendering-features.md (new), materials-and-meshes, sky, asset-pipeline, editor, testing,
  project-and-gamehost, post-processing, particles-3d, milestones, AGENTS.md, README, landing page (feature card and
  editor screenshot).

## Decisions

- [ADR 0245](../decisions/0245-mesh-lod-by-projected-error.md): levels in the mesh's own buffers, one selection formula
  for both projections, authored LODs only.
- [ADR 0246](../decisions/0246-forward-decals.md): forward decals, per-instance masks, one shared atlas.
- Not decided here: the PBR default (asked the owner through the parent session; no answer, so Blinn-Phong stays for
  materials and import), the simplifier dependency.

## Dead ends

- The ORM plate in the first `decals` frame looked absent: it sat in the box's sky-lit shadow, where a metal reflecting
  the sky matches the shadow's colour. Moved into sunlight.
- The first LOD self-check assumed one pixel scale; moltenvk captures at 2×, lavapipe at 1×, so the row is spaced to give
  three or more levels at either.

## Verified

- `just build` and the Release `-warnaserror` build: 0 warnings. `just format-check`, `just shaders-check` (161 shaders, 20
  surface shaders), `check.sh` and `just docs-build` (95 pages) pass.
- Unit tests: engine 3340/3341 in the suite run (`FreeListBlockTests.AFragmentedBlock…` failed while Docker loaded the
  machine and passes alone; three `SpirvInputsTests` expectations updated for the decal mask), editor 1667 (after adding
  `meshLodThreshold` to the settings round trip), video 464.
- Render tests (moltenvk): 228/234. The six failures (`subviewport-post` ×2, `physics-joints`, three editor goldens) are
  main's stale moltenvk goldens: with `fix/stale-goldens-shader-lock`'s goldens all six pass on this branch. New:
  `lod`, `decals`, `pbr-ibl-hdr` goldens inspected, and the `lod` and moving-decal allocation gates are 0 B.
- Lavapipe (`just render-tests-linux` in Docker): the new `lod`, `decals`, `pbr-ibl-hdr` tests and the PBR tests 8/8, self-checks
  included at 1×; the three new lavapipe goldens inspected and committed.
- Every existing golden matches unchanged: the decal pass and the reordered `Mesh.vk.frag` are bit-identical without
  decals.
- `just qa-editor`: the new `21b-decal-gizmo` capture (scene icon, projection box, handles, inspector groups), used as
  `docs/images/editor-decal.png`.
- Benchmarks: `BuildAndSortOpaqueWithLod10k` 0.169 ms vs 0.146 ms without LOD; `Assign32x10k` 0.35 ms; 0 B both.

## Not verified

- Benchmarks against `baseline.json` on a quiet machine (both new ones print NEW).
- Windows (CI covers it).

## Next

- Owner: flip the PBR default (materials and import, `[SerializedVersion(2)]`, re-recorded goldens) or keep it.
- Owner: the simplifier for `generateLods` (meshoptimizer shim or in-house).
- G19 protected exports phases 2–3, then G8a–e, per the parent session's queue.
