# Plan: Tree variants, collider scenes and a scatter type (G8b.7)

**Status:** done, see [log](../logs/2026-11-07-tree-variants.md)
**Docs:** [Procedural trees → Changes from the proposal](../../docs/design/procedural-trees.md#changes-from-the-proposal),
[Procedural trees → Inspector](../../docs/design/procedural-trees.md#inspector), [Terrain → Objects](../../docs/design/terrain.md#objects),
[ADR 0271](../decisions/0271-tree-inspector-bake-staleness.md), [ADR 0273](../decisions/0273-terrain-objects-list-and-nodes.md)
**Started:** 2026-11-07

## Goal

From a `Tree3D`'s inspector, one click bakes N seeds of its options into meshes and tree scenes (with a trunk collider
when Create collider is on) and a `ScatterObjectType` listing them, so the Terrain dock's Objects mode scatters varied,
colliding trees without generating anything at load.

## Not in scope

- The objects' `MultiMesh` mode: forests of thousands are `TreeScatter`'s (per-chunk multimeshes and trunk capsules);
  ADR 0274 records why.
- A backdrop `FoliageType` from a baked tree: `FoliageType` is one mesh with one material, a tree is two surfaces with
  their own materials.
- Impostors in the bake, `ITerrainObject` on the tree scene's root.

## Steps

- [x] `TreeVariants` (editor): seeds, file names (`<stem>_v<i>_<style>.mfres`, `<stem>_v<i>.mfscene`,
      `<stem>.mfres` type), bake each variant, build and save its scene (`StaticBody3D` root + baked `Tree3D` +
      `CollisionShape3D` trunk capsule, or a bare baked `Tree3D` without the collider), write the `ScatterObjectType`
      (spacing from the canopy, sink from the trunk radius). Verified by: `TreeVariantsTests` (files, seeds differ, scenes
      load and collide, type lists the scenes, re-run overwrites).
- [x] Inspector: Variants count field, Bake variants (`stack-2`) and Create collider toggle (`box`) icon buttons with
      tooltips, status line. Verified by: `Tree3DInspectorTests` (actions run, toggle state).
- [x] Objects mode picks the new type up: a terrain with the type scatters tree scenes that collide. Verified by: a
      `TerrainDockTests` case or `TreeVariantsTests` with a headless terrain.
- [x] Docs: procedural-trees.md (Inspector, Scatter, Not yet, Changes from the proposal), terrain.md Objects, editor.md,
      ADR 0274, README + landing line, editor screenshot, log with State changes. Verified by: check.sh, state-guard.

## Done when

- Gates pass; Bake variants on an Oak Medium with 4 variants and Create collider on writes 4 meshes, 4 scenes and one
  type; each scene loads with a `StaticBody3D` root whose capsule matches the variant's trunk; the type scatters on a
  terrain.

## Open questions

- None; the defaults follow the proposal (4 variants, collider on).
