# Proposal: Navigation — later phases (G3b)

**Milestone:** [Gameplay toolkit](../../milestones.md#gameplay-toolkit-) (G3) · **Status:** G3a shipped (G3.1–G3.6, see
[Navigation](../navigation.md)); G3.7 avoidance shipped ([Avoidance](../navigation.md#avoidance-g37)); G3.8–G3.12 planned ·
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
- **G3.10: async queries (opt-in).** `NavigationMap2D.QueryPathAsync(in query, NavigationPath2D result)` returns a
  `NavigationPathRequest` handle (a struct). Workers run on the .NET thread pool through a cached
  `IThreadPoolWorkItem` (no per-request allocation), each with its own `GridSearch` scratch, against an immutable
  snapshot of the grid taken at the last sync (copy-on-write: a changed grid allocates a new snapshot only on the
  tick it changed). Results are delivered at the **next sync point**, which waits for requests submitted before it.
  Delivery therefore always lands exactly one tick later, so async queries stay deterministic. Agents with
  `AsyncPath = true` keep following the old path until the new one arrives, then raise `PathChanged`. A
  `navigation` section in `project.mfproj` (format bump + migration) holds the worker count and the default.

### Later phases

- ~~**Avoidance (G3.7).**~~ Shipped: [Navigation → Avoidance](../navigation.md#avoidance-g37) (ADR 0250).
- **2D navmesh polygons (G3.9).** `NavigationPolygon : Resource` (outlines → convex polygons) and
  `NavigationRegion2D` (Godot names), A* over polygon adjacency plus the funnel algorithm, `NavigationLink2D` for
  jumps and teleports, regions joined by shared edges. Baking from physics shapes and tile outlines with agent-radius
  offsetting. This is the step between grids and 3D navmeshes, and it tests the map/region/agent split on
  non-grid data.
- **3D (G3.11–G3.12).** `NavigationServer3D` with the same map/region/agent split, `NavigationAgent3D`
  (`Vector3`, plus `PathHeightOffset`), and first a `NavigationGrid3D`: an `AStarGrid2D` on the XZ plane at a
  height, baked with `IntersectShape` against 3D static bodies — enough for top-down 3D games, tower defence and RTS.
  Then `NavigationMesh` + `NavigationRegion3D` with baking from collision shapes and meshes; the baking library is
  undecided ([Open questions](#open-questions)).

## Testing

- ~~G3.7~~ shipped ([Navigation → Tests](../navigation.md#tests)). G3.10: delivery exactly one sync later, snapshot
  copy-on-write, determinism under thread scheduling.

## Task list

1. ~~**G3.6 TileMapLayer source**~~ shipped: [Navigation → Tile map source](../navigation.md#tile-map-source).
2. ~~**G3.7 Avoidance.**~~ shipped: [Navigation → Avoidance](../navigation.md#avoidance-g37).
3. **G3.8 Grid polish.** Isometric cell shapes if G2 needs them; HPA* only if a game needs it; cell painting in the
   inspector if tile maps do not cover it.
4. **G3.9 2D navmesh polygons.** `NavigationPolygon`, `NavigationRegion2D`, funnel, `NavigationLink2D`, bake.
5. **G3.10 Async queries.** Snapshots, worker scratch, next-sync delivery, `project.mfproj` section; tests.
6. **G3.11 3D grid.** `NavigationServer3D`, `NavigationGrid3D`, `NavigationAgent3D`; Demo 3D variant.
7. **G3.12 3D navmesh.** `NavigationMesh`, `NavigationRegion3D`, baking — after the dependency decision.

## Open questions

1. **3D navmesh baking library.** Candidates, none assumed:
   - *Recast/Detour* (C++, zlib licence, actively maintained by the recastnavigation project; Godot bakes with
     Recast). Needs a native shim built per RID in `natives.yml` like `mfrmlui` ([natives.md](../natives.md)), and
     static linking for iOS ([mobile.md](mobile.md)).
   - *DotRecast* (C# port of Recast/Detour, zlib licence, NuGet; mostly one maintainer). No native build; check AOT
     and trimming, per-query allocation, and release cadence before relying on it.
   - Our own voxel/region/contour pipeline: no dependency, but months of work.
2. **Polygon clipping for 2D navmeshes** (agent-radius offsetting, merging obstacles): Clipper2 is now an approved
   dependency (ADR 0203, `Geometry2D`), so the question is only whether it covers the offsetting needs.
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
