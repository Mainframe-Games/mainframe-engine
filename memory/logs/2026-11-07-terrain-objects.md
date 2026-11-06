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

## State changes

- milestones G8a (final): status cell → "🚧 core, Realistic look, foliage scatter ✅; viewport-tool API and Terrain dock: create, sculpt, paint, water, foliage, objects ✅ (ADR 0270); Faceted `TerrainMaterial3D` ✅; painted foliage density ✅; object scatter (`Nodes` mode) and the Objects mode ✅ ([ADR 0273](../decisions/0273-terrain-objects-list-and-nodes.md)). Not built: sub-rectangle texture uploads (G8a.1/G8a.15), the foliage build radius (G8a.8), the objects' `MultiMesh` mode (G8a.9, with G8b.7), `CollisionMode.NearBodies` (G8a.13), layer thumbnails in the dock (G8a.15), Realistic grass's distance shrink (G8a.16) ⬜".
- milestones G8b: the description cell's "G8b.7 scatter … waits for G8a's object scatter" → "G8b.7 scatter (Bake variants, Create collider) builds on G8a's object scatter (`ScatterObjectType`, ADR 0273; its `MultiMesh` mode comes with it)".
- milestones G8e (final): status cell → "🚧 all eight phases built and merged: G8e.1 probe GI ([ADR 0170](../decisions/0170-light-probe-volume.md)), G8e.2 shadows (0167), G8e.3 volumetric fog (0171), G8e.4 cinematic post (0168), G8e.5 Ez Tree foliage and impostors (0172), G8e.6 water SSR/refraction/falls (0173), G8e.7 art pass (0175), G8e.8 TAAU (0174); look-dev 0177–0181; `forest-mini` golden covers them (G8d). Open: the frame budget is not met (2026-11-07, busy machine, Apple M5: 1080p with TAAU p50 25.5 ms, p99 33.6 ms; 2560 × 1316 native p50 38.5 ms, p99 51.4 ms; target 14 ms / 16.7 ms on an M-series Pro: [Forest → Performance](../../docs/design/forest.md#performance)); a quiet-machine `forest-bench` baseline; GPU per-instance culling for trees ([Procedural trees → Not yet](../../docs/design/procedural-trees.md#not-yet)); Brogan's sign-off of each phase's reference shots; the proposal's open questions 1–14 (built on their defaults)".
- state: Next steps 2's G8a–e rest → "G8a's open items in its milestones row, tree scatter G8b.7, the Forest frame budget: G8e, see `docs/design/forest.md#performance`".
