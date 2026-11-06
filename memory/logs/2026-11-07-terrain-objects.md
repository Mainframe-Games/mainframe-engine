---
summary: >-
  G8a.9: terrain object scatter — ScatterObjectType, a stable-id list in objects.json, one scene instance per object on
  the ground (ITerrainObject gets its id first), re-heighting, removal; the Terrain dock's Objects mode (Scatter, Place,
  Shift removes, one undo per stroke). Nodes mode only (ADR 0273).
---

# 2026-11-07: Terrain object scatter and the dock's Objects mode

Branch `feature/terrain-objects` (stacked on `feature/terrain-foliage`). Plan:
[Terrain objects](../plans/2026-11-07-terrain-objects.md). As built: [Terrain → Objects](../../docs/design/terrain.md#objects),
[ADR 0273](../decisions/0273-terrain-objects-list-and-nodes.md).

## What changed

- Engine (`Src/Scene/Nodes3D/Terrain/Objects/`): `ScatterObjectType`, `TerrainObject`, `ITerrainObject`,
  `TerrainObjectChange`, `TerrainObjectFile` (`objects.json`); `TerrainData` and `Terrain3D` became `partial`, with
  `TerrainData.Objects.cs` (the list, lazy load, `SaveObjects` from `SaveLayers`, kept for the next id once emptied) and
  `Terrain3D.Objects.cs` (the unowned `Objects` container, `GetObjectNode`, `RemoveObject` with `QueueFree`,
  `ObjectCount`, `ObjectsRoot`, `VariantHash`, re-heighting from `OnDataEdited`/`RefreshAll`, re-spawn when the type list
  changes or after changes outside the tree; `ObjectsChanged` subscribed with `Edited`).
- Editor: `TerrainDockMode.Objects` (`cube`), `TerrainEditorTool.Objects.cs` (`TerrainObjectTool` Scatter/Place,
  occupancy per grid cell, slope/water limits, Shift removal, Cmd/Ctrl+click type pick, the type list with counts and a
  red total above 2,000, `TerrainObjectsAction`); `.td-hint.td-over` in `inspector.rml`.
- Tests: `TerrainObjectsTests` (10), three new `TerrainDockTests` (Scatter fills once and undo/redo keep ids; Shift
  removes only the chosen type; Place adds one and Scatter skips water).
- Docs: terrain.md (Objects section, key types, files table incl. the foliage images, editing, testing, changes from
  the proposal, gaps), milestones G8a/G8b rows, README, landing page, editor.md sample.

## Decisions

- ADR 0273: `objects.json` (not `objects.mfres`), `Nodes` mode only, the file kept for `nextId` after the last removal.
- The proposal's "Single" tool is `TerrainObjectTool.Place` (CA1720 forbids `Single` as an identifier).

## Not done

- The `MultiMesh` render mode (G8b.7), `AlignToNormal`, per-chunk containers; no editor screenshot of the Objects mode
  (README/site still show the Sculpt screenshot); no QA-script steps for objects.
