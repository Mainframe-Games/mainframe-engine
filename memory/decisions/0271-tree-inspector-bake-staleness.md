# ADR 0271 — Tree inspector: bake hashes, stale bakes preview live in the editor, view-only LOD and wind toggles

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8b (part: G8b.3)
- **Docs:** docs/design/procedural-trees.md#inspector, docs/design/future/procedural-trees.md#editor

## Context

The proposal's tree inspector (G8b.3) shows a bake's state (up to date / stale / not baked) from a `BakeHash` of the
options, seed, style and generator version, forces a level with `ForceLod` and previews wind in the edit view. The
engine had `TreeMesh.GeneratorVersion` but no hash, no `ForceLod` and no way for the editor to wind one view. A baked
`Tree3D` drew its `BakedMesh` as is, so edits to its options changed nothing on screen until the next bake.

## Decision

- **`TreeMesh.BakeHash`** (exported string): SHA-256 of a text form of every exported value of the `TreeOptions`
  (levels and twig options walked through the type registry, `ResourceName` left out, external resources by UID),
  the seed, the style and `TreeGenerator.GeneratorVersion`. Every generated `TreeMesh` records it; `IsCurrentFor` and
  `Tree3D.GetBakeStatus()` compare it. A bake without a hash (older files) reads as stale; a tree with only a bake (no
  options or preset of its own) reads as up to date. Material inputs (tints, textures) count too: a bake holds the
  options its materials come from.
- **In edit mode a stale bake yields to the generator**, so the inspector's live preview follows every edit while the
  status says "Stale"; an up-to-date bake still draws. Games never regenerate a baked tree (unchanged).
- **Bakes keep a copy of the options** (`Tree3D.GenerateBake`, `TreeOptions.Clone`), so later edits to the tree's
  options never reach a bake in memory.
- **`ForceLod` is not exported** (the proposal had `[Export(Range = "-1,3,1")]`): it is a view setting, so it is not
  saved and not undone, like the inspector's wind toggle, **`World3D.PreviewWind`**: a breeze (strength 1) for the
  frame data of one world while it has no wind of its own, read through `World3D.GetFrameEnvironment` by the mesh
  renderer and the render server.
- **Re-bakes overwrite the tree's bake file**; a first bake goes to `Assets/Trees/<preset>_<seed>_<style>.mfres` (the
  preset the options came from, edited or not; the node's name without one). The file stays on undo, as Save as .mfres
  does.
- The level foldouts are the generated nested rows of `Options`. `TreeLevel.LevelIndex` (set by `TreeOptions.Level`)
  lets `TreeLevelInspector.ShowProperty` hide Angle and Start on the trunk and Children on level 3, and a new
  `ICustomInspector.ElementLabel` hook names array elements ("Level 0 · trunk · 6 children · length 37.24").

## Consequences

- The hash formats values as text (doubles in .NET's shortest round-trip form), so it is stable across machines and
  runs; adding an exported property to `TreeOptions` or `TreeLevel` marks every existing bake stale once, which is the
  safe direction.
- What the editor shows for a stale bake is not what the game draws until the tree is baked again; the status line says
  so.
- Impostors are not baked into the `.mfres` (Tree3D draws none yet); the bake tooltip names the meshes only.
- Bake variants and Create collider stay with G8b.7 (scatter, after G8a's object scatter).
