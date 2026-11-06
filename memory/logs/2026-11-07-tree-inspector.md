---
summary: >-
  G8b.3: the Tree3D inspector — Preset dropdown with Ez Tree JSON import, Seed with a dice, Style toggle, wind preview,
  a LOD table whose eyes force a level (Tree3D.ForceLod), Bake to Assets/Trees/*.mfres with the bake's status from a new
  TreeMesh.BakeHash (stale bakes preview live in the editor; ADR 0271); level foldouts named and trimmed per level; the
  tree, trees, leaf and wind icons; a README screenshot.
---

# 2026-11-07: Tree inspector (G8b.3)

Branch `feature/tree-inspector`, cut from `origin/main` (7ca385ce). Proposal: [Procedural trees → Editor](../../docs/design/future/procedural-trees.md#editor).
As built: [Procedural trees → Inspector](../../docs/design/procedural-trees.md#inspector).

## What changed

- **Engine:** `TreeMesh.BakeHash`, `ComputeBakeHash`, `IsCurrentFor`; `Tree3D.ForceLod` (not saved), `GetBakeStatus()`
  and `TreeBakeStatus`, `DrawsBake`, `NeedsRegenerate`, `LastRegenerateMilliseconds`, `GenerateBake()` (the bake keeps a copy of the
  options); a stale bake yields to the generator in edit mode; `TreeLevel.LevelIndex` (set by `TreeOptions.Level`);
  `World3D.PreviewWind` and `World3D.GetFrameEnvironment` (the mesh renderer and render server read the frame's wind
  and fog through it). Icons: `Tree3D`, `TreeSpecies`, `TreeLod3D` `tree`; `TreeOptions` `trees`; `FoliageMaterial3D`,
  `TwigOptions` `leaf`.
- **Editor:** `Src/Trees/Tree3DInspector.cs` (`Tree3DInspector`, `TreeOptionsInspector`, `TreeLevelInspector`);
  `ICustomInspector.ElementLabel`/`ElementSummary` (array element titles and key values); header `<select
  data-header-input>` dropdowns send their value on change; new `Tree3D`s start as an inline Oak Medium
  (`NewNodeDefaults`); the QA `add-node` takes an optional parent path; `tree`, `trees`, `leaf`, `wind` in the icon
  atlas (`just editor-icons-fetch`); inspector styles in `inspector.rml`.
- **Tests:** `TreeBakeStatusTests` (6, engine), `Tree3DInspectorTests` (9, editor), three icon theory rows.
- **QA and docs:** `readme-screenshots.qa` section 3e2 and `editor-tree` in the recipe's list; `docs/images/editor-tree.png`;
  procedural-trees (Inspector, Tree3D table, Bake status, Not yet), editor.md, README (editor table, feature line),
  the landing page's editor list, milestones (G8b row), AGENTS.md, current-state.

## Decisions

- [ADR 0271](../decisions/0271-tree-inspector-bake-staleness.md): the hash and what it covers, stale bakes preview live
  in the editor only, `ForceLod` and the wind preview as unsaved view state, re-bakes overwrite the tree's file, level
  foldouts stay the generated rows (`LevelIndex`, `ElementLabel`/`ElementSummary`).
- The preset list is every `TreePresets` name (Ez Tree's 15 and the engine's 12 species) plus Custom.
- The bake does not hold the impostor (Tree3D draws none yet), so the Bake tooltip names the meshes only.

## Dead ends

- The worktree's LFS files were pointers (the build failed on the editor's `.ico`); `git lfs checkout` filled them from
  the shared store without a download.
- A first bake path switched to the node's name once the options were edited (the preset no longer matched), so a
  re-bake wrote a second file; bakes now overwrite the tree's existing file, and the default name uses the preset the
  options came from.
- Text directly inside the flex label rows did not render in RmlUi; wrapping it in a span did. The LOD table's
  "13,806 tris" columns overlapped at the inspector's width; a header row with bare numbers fits.
- A level's full summary in the element title overflowed into the type column; the title is now "Level 1" and the
  values replace the type name beside it.

## Verified

- Gates from `.agents/workflow.md` except `just test-render` and `just shaders-check` (no shader or render output
  change; `PreviewWind` is off by default): see the report.
- The screenshot (`editor-tree.png`) inspected: the tree, the header with the LOD table and status, Level 1's rows.

## Not done

- Bake variants and Create collider (G8b.7, with G8a's object scatter); impostors in the bake; the LOD table's switch
  sizes in pixels (needs G6.4's screen-space selection on `Tree3D`).
- The wind preview was not checked by eye (a still screenshot cannot show it).

## State changes

- milestones G8b: the description cell gains "; the inspector (G8b.3) ✅ ([ADR 0271](../memory/decisions/0271-tree-inspector-bake-staleness.md)); G8b.7 scatter (Bake variants, Create collider) waits for G8a's object scatter"; status stays 🚧.
- state: the What exists Since-M10 list gains "the editor's tree inspector with bake staleness (G8b.3, ADR 0271)"; Next steps 2 replaces "tree inspector" with "tree scatter G8b.7".
