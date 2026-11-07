# Plan: G3.12 3D navigation meshes on DotRecast

**Status:** done, see [log](../logs/2026-11-07-navigation-meshes-3d.md)
**Docs:** [Navigation](../../docs/design/navigation.md) (the proposal's G3.12 text, now moved there; the 3D map and
agents of G3.11), the owner's answer of 2026-11-07 (DotRecast, after AOT and allocation checks)
**Started:** 2026-11-07

## Goal

- `NavigationMesh` (Godot's names and defaults, the baked polygons and the Detour tile) and `NavigationRegion3D` on the
  3D map; a bake from meshes and static collision through DotRecast; Detour queries with 0 bytes per query; agents,
  debug draw, editor Bake/Clear, a Demo scene with ramps and stairs.

## Not in scope

- Paths across 3D regions, `NavigationLink3D`, 3D avoidance, baking on a thread or in tiles.

## Steps

- [x] Check DotRecast under NativeAOT and per-query allocation (scratch probe outside the repo). Verify: no IL2xxx/IL3xxx
  warnings; 0 B over 1000 queries.
- [x] Package references; `NavigationMesh`, `RecastBaker`, `DetourMesh3D`, `NavigationRegion3D`; map integration; debug
  draw. Verify: `NavigationMesh3DTests`.
- [x] Editor `NavigationRegion3DInspector`; resource round trip. Verify: editor and resource tests.
- [x] Demo `navmesh_3d` + tests + screenshot.
- [x] Docs (the proposal moved into `navigation.md`), THIRD_PARTY_NOTICES, ADR 0264, log; gates incl. the NativeAOT
  template smoke; PR.

## Done when

- `NavigationMesh3DTests` (with the 0-byte case), the editor case and `NavigationMesh3DSceneTests` pass.
- `just template-smoke-aot` passes with DotRecast in the engine.
- All gates pass locally; the PR is open.
