# ADR 0264 — 3D navigation meshes: DotRecast for baking and queries, one Detour tile per region

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G3b (part: G3.12)
- **Docs:** docs/design/navigation.md#3d-navigation-meshes-g312, memory/plans/2026-11-07-navigation-meshes-3d-g3-12.md

## Context

G3.12 asked for Godot's `NavigationMesh` and `NavigationRegion3D` with baking from collision shapes and meshes, and left
the library open: Recast/Detour in C++ behind a native shim, DotRecast (the C# port, NuGet), or an own voxel pipeline.
The owner chose DotRecast on 2026-11-07, provided NativeAOT/trimming and per-query allocation check out first; the
engine is trim- and AOT-clean since G19 phase 4 (ADR 0259).

## Decision

- **DotRecast 2026.3.1** (`DotRecast.Recast`, `DotRecast.Detour`; zlib), versions central in `Directory.Packages.props`,
  every call inside `Src/Navigation/Meshes3D` (`RecastBaker`, `DetourMesh3D`), as physics keeps Jitter2 inside
  `Src/Physics`. Checked before adopting it: a NativeAOT publish (osx-arm64, `IsAotCompatible`, all trim warnings shown)
  raises no IL2xxx/IL3xxx warnings, and `FindNearestPoly` + `FindPath` + `FindStraightPath` through the span overloads
  allocate 0 bytes over 1000 queries (≈ 1.5 µs native, ≈ 6 µs JIT on a small level).
- **The resource keeps the Detour tile.** `NavigationMesh.DetourData` is `dtmd1:` + base64 of DotRecast's mesh data
  (little-endian, C-compatible layout), loaded at a map sync into a `DtNavMesh` with a main-thread `DtNavMeshQuery`;
  Godot's `Vertices`/`Polygons` sit beside it for the debug draw and tools. One region is one tile, in the region's
  local space; queries move into it and back, so regions can be moved and rotated without re-baking.
- **Solid primitives mark their inside.** Recast rasterises surfaces only, so the air inside a closed box taller than
  the agent reads as floor. Each box, sphere, capsule and cylinder adds a convex volume (its XZ hull, from its bottom to
  just below its lowest top) that sets those spans unwalkable; tops and ramps stay walkable.
- **Paths stay in one region** for now: the region whose surface is nearest the start answers. Joining 3D regions
  (shared edges, links) needs a tiled `DtNavMesh` and is left for when a game needs it.

## Ruled out

- **Recast in C++ behind a native shim:** a native build per RID and static linking on iOS, for code DotRecast already
  ports; the owner chose DotRecast.
- **An own voxel/region/contour pipeline:** months of work for what Recast does.
- **Storing only Godot's vertices and polygons** and rebuilding the Detour tile at load: Detour needs Recast's
  neighbour and detail data, which the tile already carries; rebuilding it would repeat the bake's last stage.

## Consequences

- 3D games get Recast-quality navigation meshes with no native code; exports stay single-file under NativeAOT.
- A dependency with mostly one maintainer: the pinned version and the Meshes3D boundary keep an upgrade or a swap local.
- Large worlds want tiles; levels bake on the main thread (tens of milliseconds for the Demo's level).

## Revisit when

- A game needs paths across 3D regions, `NavigationLink3D`, 3D avoidance, or baking on a thread or in tiles.
- DotRecast stops being maintained or an upgrade changes its API.
