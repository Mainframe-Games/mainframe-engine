---
summary: >-
  G8b.7: Bake Variants on the tree inspector — N seeds baked into meshes and tree scenes (a StaticBody3D with the trunk
  capsule when Create Collider is on) plus a ScatterObjectType for the Terrain dock's Objects mode (ADR 0274; no
  MultiMesh objects mode: forests stay TreeScatter's). G8b is complete: the procedural-trees proposal moved into
  docs/design/procedural-trees.md with Changes from the proposal and was removed.
---

# 2026-11-07: Tree variants and scatter scenes (G8b.7)

Branch `feature/tree-variants`, cut from `origin/main` (1271884f). Plan: [Tree variants](../plans/2026-11-07-tree-variants.md).
As built: [Procedural trees → Variants and scatter scenes](../../docs/design/procedural-trees.md#variants-and-scatter-scenes).

## What changed

- **Editor:** `Src/Trees/TreeVariants.cs` (`TreeVariants.Bake`, `BuildScene`, `Folder`, `TreeVariantBake`); the
  `Tree3DInspector` header's Variants count (`tree-variants=`), Create Collider toggle (`box`, on by default) and Bake
  Variants (`packages`), with the result under the row; `inspector.rml` (`.tree-variants`).
- **Tests:** `TreeVariantsTests` (4, editor).
- **Docs:** procedural-trees.md (Inspector row, Variants and scatter scenes, Testing, Not yet, Changes from the
  proposal), terrain.md (Objects: trees, limits, changes, gaps), editor.md, AGENTS.md, README (tree inspector caption,
  the trees feature line; a duplicated River tool row in the editor table removed), the landing page's Showcase;
  `docs/images/editor-tree.png` re-captured (the Variants row).
- **Proposal closed:** `docs/design/future/procedural-trees.md` removed; links repointed (docs/README.md, milestones'
  G8b link target, the G8a/G8c/G8e proposals, ADRs 0151, 0152, 0158, 0172, 0271, the tree-inspector log,
  `TreeParityTests`, the forest-slice plan).

## Decisions

- [ADR 0274](../decisions/0274-tree-variants-and-scatter-scenes.md): variants as files (scenes + type) from the
  inspector; Create Collider is a toggle; re-runs keep the type's placement fields and the scenes' UIDs; no `MultiMesh`
  objects mode and no backdrop `FoliageType` (forests and backdrops are `TreeScatter`'s).

## Dead ends

- None in code. The README screenshot was re-captured with a scratch QA script holding only the tree section of
  `readme-screenshots.qa`; Bake Variants is not run in it, because the screenshot project links the repository's Demo
  and would gain `Assets/Trees/`.

## Verified

- `TreeVariantsTests` and `Tree3DInspectorTests` (13) pass; full gates: see the PR.
- `editor-tree.png` inspected: the Variants row (count 4, Create Collider on, Bake Variants) under Bake.

## Not done

- `ITerrainObject` on the tree scene's root; painting a `TreeScatter` forest from a terrain density layer (ADR 0274's
  revisit); the rest of [Procedural trees → Not yet](../../docs/design/procedural-trees.md#not-yet).
- A person should place a few variant trees with the Terrain dock and walk into them in Play (the collider is checked
  by tests on the node structure, not by a physics query).

## State changes

- milestones G8b: status cell → "✅ G8b.1–G8b.7: the port, options and presets, LowPoly and Realistic, `Tree3D` and
  Bake, the inspector (ADR 0271), wind and `FoliageMaterial3D`, impostors (G8e.5, ADR 0172), Bake Variants into
  colliding tree scenes and a terrain scatter type ([ADR 0274](../decisions/0274-tree-variants-and-scatter-scenes.md));
  not built: `RuntimeGeneration` modes, impostors on `Tree3D` and in the bake, the trellis
  ([Procedural trees → Not yet](../../docs/design/procedural-trees.md#not-yet))"; the description cell's "G8b.7 scatter … waits
  for / builds on G8a's object scatter …" clause → "G8b.7 Bake Variants ✅".
- milestones G8a: in the status cell, "the objects' `MultiMesh` mode (G8a.9, with G8b.7)" → "the objects' `MultiMesh`
  mode (dropped: forests are `TreeScatter`'s, ADR 0274)".
- state: Next steps 2 drops "tree scatter G8b.7".
