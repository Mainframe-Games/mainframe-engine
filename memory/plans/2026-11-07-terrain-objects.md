# Plan: Terrain object scatter and the Terrain dock's Objects mode (G8a.9)

**Status:** done, see [log](../logs/2026-11-07-terrain-objects.md)
**Docs:** [Terrain proposal → `ScatterObjectType` and `ITerrainObject`](../../docs/design/future/terrain.md#scatterobjecttype-and-iterrainobject),
[Terrain → Objects](../../docs/design/terrain.md#objects), [ADR 0273](../decisions/0273-terrain-objects-list-and-nodes.md)
**Started:** 2026-11-07

## Goal

Terrains hold an explicit list of scattered objects with stable ids (saved beside the layers), each spawned as its scene
on the ground, and the Terrain dock scatters, places and removes them with one undo step per stroke or click.

## Not in scope

- The `MultiMesh` render mode (flattened scenes, per-chunk compound bodies): with G8b.7's forests.
- `AlignToNormal`, per-chunk containers, removing every type at once.

## Steps

- [x] `ScatterObjectType`, `TerrainObject`, `ITerrainObject`, `TerrainObjectChange`, `objects.json` (`TerrainObjectFile`);
      `TerrainData.ObjectTypes`/`Objects`/`NextObjectId`/`AddObject`/`RestoreObject`/`RemoveObject`/`GetObject`/
      `ObjectTypeIndex`/`ObjectsChanged`, lazy load, `SaveObjects` from `SaveLayers`. Verified by: `TerrainObjectsTests`
      (ids, save only when changed, kept for the next id, carried to a new folder, bad file).
- [x] `Terrain3D` spawning: unowned `Objects` container, variant by id hash, `ITerrainObject` before enter-tree, placement
      on the bed minus the sink, re-heighting after height and water edits, removal at the frame's end, re-spawn on type
      changes and after changes outside the tree. Verified by: `TerrainObjectsTests` (headless tree).
- [x] Dock Objects mode: Scatter (grid occupancy, slope and water limits), Place, Shift removes, Cmd/Ctrl+click picks the
      type, type list with counts and the 2,000 warning, `TerrainObjectsAction`. Verified by: three `TerrainDockTests`.
- [x] Docs (terrain.md Objects, changes from the proposal, gaps), milestones, README, landing page, ADR 0273, log.
      Verified by: check.sh.

## Done when

- Gates pass; a Scatter stroke adds the chosen type, repainting adds nothing, Shift removes, Place adds one, and each is
  one undo step whose undo and redo restore the same ids.
