# ADR 0274 — Tree variants bake to scenes and a scatter type; forests stay on TreeScatter

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8b (G8b.7)
- **Docs:** docs/design/procedural-trees.md#variants-and-scatter-scenes, docs/design/terrain.md#objects,
  memory/plans/2026-11-07-tree-variants.md

## Context

The procedural-trees proposal (ADR 0149) ends G8b with scatter: **Bake variants** bakes N seeds of one preset, **Create
collider** wraps each in a `StaticBody3D` scene with its trunk capsule, and the scenes scatter through G8a's
`ScatterObjectType` in both of its modes, `Nodes` (one scene per object) and `MultiMesh` (scenes flattened into per-chunk
multimeshes and compound bodies), plus a backdrop `FoliageType`. G8a.9 (ADR 0273) built the object list in `Nodes` mode
only and left `MultiMesh` to this step. Since the proposal, ADR 0172 shipped `TreeScatter`: per-chunk, per-level
multimeshes of a species' variants, impostors, per-instance levels and one static body per chunk with a trunk capsule per
tree. The Forest draws its 2,000 trees with it.

## Decision

- **Bake Variants is an editor action on the `Tree3D` inspector** (`TreeVariants`). It bakes the tree's options for
  seeds `seed … seed + N − 1` (N = 1–16, default 4; variant 0 is the tree being inspected) and writes everything to
  `Assets/Trees/<stem>_<style>/`: one `TreeMesh` `.mfres` and one `.mfscene` per variant, and `<stem>_<style>.mfres`, a
  `ScatterObjectType` listing the scenes. The scenes keep the inspected tree's level distances, shadows and material
  overrides. Nothing in the open scene changes, so there is no undo step.
- **Create Collider is a toggle (default on), not a second action.** On: a variant's scene root is a `StaticBody3D`
  named after the variant, holding the baked `Tree3D` (its internal trunk body off) and a `CollisionShape3D` with the
  trunk capsule, so games can set layers, groups and scripts on the body. Off: the root is the baked `Tree3D` with
  `Collision` off (shrubs and decoration).
- **A re-run overwrites the meshes and scenes (their UIDs stay) and keeps the type's placement fields**, replacing only
  its `Scenes`. A new type gets spacing from the canopy (1.5 × the widest canopy radius, at least 2 m), sink = the trunk
  radius, 35° slope limit, scale 0.85–1.15, random yaw, no water.
- **No `MultiMesh` mode for terrain objects.** Forests of thousands are `TreeScatter`'s: it already batches per chunk
  and level, draws impostors and collides per chunk, which a generic flattening of arbitrary scenes would only redo for
  trees, and terrain objects keep their ids and scripts. The Terrain dock places the near trees a player meets as
  objects; a forest is a `TreeScatter`.
- **No backdrop `FoliageType` from a baked tree.** A foliage type is one mesh and one material; a tree is a bark and a
  leaf surface with their own materials and levels. Backdrops are `TreeScatter` chunks with impostors.

## Ruled out

- **Variants as a list on `TreeSpecies`.** `TreeScatter` species already take `Seeds` or `Baked` variants; the new action
  is for scenes the Objects mode instances, which need files.
- **Seeds by random roll.** Consecutive seeds are as different as random ones (Ez Tree's RNG hashes the seed), and the
  same N gives the same files on every run.

## Consequences

- The Terrain dock scatters colliding, varied trees with stable ids after one click and adding the type to the terrain's
  `ObjectTypes`; they load without the generator.
- About 2,000 objects per terrain stays the limit (ADR 0273); past it, use `TreeScatter`.
- The tree scene's root does not implement `ITerrainObject`; a game that needs the id wraps the scene or scripts the body.

## Revisit when

- A game wants to paint a forest with the Terrain dock: feed `TreeScatter` placements from a terrain density layer rather
  than adding a `MultiMesh` objects mode.
