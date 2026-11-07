# Proposal: Navigation — later phases (G3b)

**Milestone:** [Gameplay toolkit](../../milestones.md#gameplay-toolkit-) (G3) · **Status:** G3a shipped (G3.1–G3.6, see
[Navigation](../navigation.md)); G3.7 avoidance shipped ([Avoidance](../navigation.md#avoidance-g37)); G3.8 closed (nothing to build); G3.9 navigation
meshes shipped ([Navigation meshes](../navigation.md#navigation-meshes-g39)); G3.10 async queries shipped
([Async queries](../navigation.md#async-queries-g310)); G3.11 3D grids shipped ([3D grids](../navigation.md#3d-grids-g311));
G3.12 planned ·
**Depends on:** [Navigation](../navigation.md) (the shipped core) · **Related:** [Networking](../networking.md),
[Mobile](mobile.md) (AOT), [Tile maps](../tile-maps.md)

The grid pathfinder, point graphs, `NavigationServer2D`, `NavigationGrid2D` (physics bake and the live tile map source),
`NavigationAgent2D`, the editor's Bake/Clear and Visible Navigation are current state: [Navigation](../navigation.md)
(ADRs 0227 and 0228; the tile map source and its changes from this proposal are in
[Tile map source](../navigation.md#tile-map-source)). This proposal keeps what is left: the later phases.

## Design

### Threading

- **Shipped (G3.1–G3.5): main thread.** Queries run inside the caller's `OnPhysicsProcess`; bakes run on the main thread
  because physics queries are main-thread only. A 128 × 128 query is expected well under 0.1 ms (benchmarked in
  G3.1), so most games never need more.
- ~~**G3.10: async queries (opt-in).**~~ Shipped: [Navigation → Async queries](../navigation.md#async-queries-g310)
  (ADR 0262).

### Later phases

- ~~**Avoidance (G3.7).**~~ Shipped: [Navigation → Avoidance](../navigation.md#avoidance-g37) (ADR 0250).
- ~~**2D navmesh polygons (G3.9).**~~ Shipped: [Navigation → Navigation meshes](../navigation.md#navigation-meshes-g39)
  (ADR 0261).
- ~~**3D grids (G3.11).**~~ Shipped: [Navigation → 3D grids](../navigation.md#3d-grids-g311) (ADR 0263):
  `NavigationServer3D`, `NavigationGrid3D`, `NavigationAgent3D`.
- **3D navigation meshes (G3.12).** `NavigationMesh` + `NavigationRegion3D` on the 3D map, with baking from collision
  shapes and meshes on DotRecast ([Open questions](#open-questions)).

## Testing

- ~~G3.7, G3.10~~ shipped ([Navigation → Tests](../navigation.md#tests)).

## Task list

1. ~~**G3.6 TileMapLayer source**~~ shipped: [Navigation → Tile map source](../navigation.md#tile-map-source).
2. ~~**G3.7 Avoidance.**~~ shipped: [Navigation → Avoidance](../navigation.md#avoidance-g37).
3. ~~**G3.8 Grid polish.**~~ closed as not needed (the owner agreed, 2026-11-07): none of its conditions arose (tile maps are square only, no game
   needs HPA*, the live tile source covers cell painting).
4. ~~**G3.9 2D navmesh polygons.**~~ shipped: [Navigation → Navigation meshes](../navigation.md#navigation-meshes-g39).
5. ~~**G3.10 Async queries.**~~ shipped: [Navigation → Async queries](../navigation.md#async-queries-g310).
6. ~~**G3.11 3D grid.**~~ shipped: [Navigation → 3D grids](../navigation.md#3d-grids-g311).
7. **G3.12 3D navmesh.** `NavigationMesh`, `NavigationRegion3D`, baking on DotRecast (open question 1).

## Open questions

1. **3D navmesh baking library.** Answered by the owner (2026-11-07): **DotRecast** from NuGet (DotRecast.Recast and
   DotRecast.Detour, every call kept inside `Src/Navigation`), once AOT/trimming and per-query allocation check out. The
   candidates were:
   - *Recast/Detour* (C++, zlib licence, actively maintained by the recastnavigation project; Godot bakes with
     Recast). Needs a native shim built per RID in `natives.yml` like `mfrmlui` ([natives.md](../natives.md)), and
     static linking for iOS ([mobile.md](mobile.md)).
   - *DotRecast* (C# port of Recast/Detour, zlib licence, NuGet; mostly one maintainer). No native build; check AOT
     and trimming, per-query allocation, and release cadence before relying on it.
   - Our own voxel/region/contour pipeline: no dependency, but months of work.
2. ~~**Polygon clipping for 2D navmeshes.**~~ Answered by G3.9 ([ADR 0261](../../../memory/decisions/0261-navigation-meshes-2d.md)):
   Clipper2 covers the offsetting and merging; the convex partition is the engine's own.
3. ~~**Avoidance algorithm source.**~~ Answered by G3.7 ([ADR 0250](../../../memory/decisions/0250-navigation-avoidance-orca.md)):
   a C# port of RVO2 2.0 with its Apache-2.0 notice and licence text.
4. **Tile map dirty rects.** G3.6 re-reads the used cells on the plain `Changed` signal (0 B once warm). Should tile maps
   add `CellsChanged(Rect2I)` once a game digs or builds every frame and the full re-read shows in the profiler?

Answered by G3a ([ADR 0227](../../../memory/decisions/0227-grid-navigation-core.md),
[ADR 0228](../../../memory/decisions/0228-navigation-server-agents-and-sources.md)): JPS ignores weights like Godot;
grids stay axis-aligned; no cell painting in the editor yet; Godot's pixel defaults; played games use the dev overlay.

## Related

- [Navigation](../navigation.md) (current state), [Physics](../physics.md), [Networking](../networking.md),
  [Dev overlay](../dev-overlay.md), [Editor](../editor.md), [Demo](../demo.md)
- Gameplay toolkit siblings: [Tile maps](../tile-maps.md) (G2), [Editor viewport tools](../editor.md#viewport-tools)
  (G7), [Save games and settings](save-and-settings.md) (G4: agents hold no saved state; save the target),
  [Keyframe animation](../animation.md) (G1)
