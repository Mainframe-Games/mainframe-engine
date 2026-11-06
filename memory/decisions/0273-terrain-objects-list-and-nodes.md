# ADR 0273 — Terrain objects are a JSON id list beside the layers, spawned as nodes

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8a (G8a.9)
- **Docs:** docs/design/terrain.md#objects, docs/design/future/terrain.md#scatterobjecttype-and-iterrainobject,
  memory/plans/2026-11-07-terrain-objects.md

## Context

The terrain proposal (ADR 0149) puts scattered objects (rocks, stumps, crates: scenes that need collision, scripts or
removal) in an explicit list with stable ids, saved as `objects.mfres`, spawned either as one scene per object (`Nodes`)
or flattened into per-chunk multimeshes with one collision body per chunk (`MultiMesh`). The terrain's other per-terrain
data already lives beside `terrain.mfres` as files `SaveLayers` writes: the layer PNGs and the river carve records
(`carve_<id>.json`).

## Decision

- **The list is `objects.json`** in the layer folder: `{ format, nextId, objects: [[id, type, x, z, yaw, scale], …] }`,
  read lazily through `ContentFiles` and written by `SaveLayers` only when it changed (or the folder moved). It is not a
  `Resource`: nothing references it by UID, it never loads through `ResourceLoader`, and a resource's JSON for thousands of
  records would be several times larger. A terrain that never had an object writes no file; one whose objects were all
  removed keeps the file for `nextId`, so an id is never reused across saves.
- **Y is not stored.** Every instance sits on the bed (`HeightAt − SinkMeters × scale`) and is re-placed after height
  and water edits.
- **`Nodes` mode only.** Each object is its scene, instanced under one unowned `Objects` container (not saved); a root
  implementing `ITerrainObject` gets its id and terrain before it enters the tree. Removal frees the node at the end of
  the frame (`QueueFree`), so an object's own script can remove it.
- **The editor stores records, not pixels:** `TerrainObjectsAction` holds the added and removed records and puts them back
  with their ids (`TerrainData.RestoreObject`; the next id never moves back).

## Ruled out

- **`objects.mfres` as a Resource with a `TerrainObject[]` export.** Typed arrays of structs would need a codec, the file
  would be rewritten by every resource save, and it would show in the FileSystem dock as an asset to open.
- **Implicit presence images (TerraBrush).** A painted mask cannot give a game a stable id to remove or save.
- **`MultiMesh` mode now.** It needs scene flattening (meshes and collision shapes out of an arbitrary scene) and per-chunk
  compound bodies; forests are G8b.7's job and can add it without changing the list or the file.

## Consequences

- Games remove objects by id (`Terrain3D.RemoveObject`) and can keep per-object state by id in their saves.
- Keep to about 2,000 objects per terrain (the dock warns above that): each is a full scene in the tree.
- Per-chunk containers (`Objects/c<chunk>`) are not built: one container holds every instance.

## Revisit when

- A forest needs more than a few thousand trees: add `RenderMode = MultiMesh` (G8b.7) on the same list.
