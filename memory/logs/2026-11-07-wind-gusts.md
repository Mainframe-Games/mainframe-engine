---
summary: >-
  G8e.5 rest: WorldEnvironment.WindGusts (frame.windParams.w) scales the simple wind (grass, cards without pivots) by the
  same rolling gust noise the hierarchical trees use (windGust in wind.slang); off by default, 0.4 in the Forest (ADR 0278).
---

# 2026-11-07: Gusts on the simple wind (G8e.5 rest)

Branch `feature/wind-gusts`, cut from `origin/main`. Plan: [Wind gusts](../plans/2026-11-07-wind-gusts.md).
As built: [Materials & meshes](../../docs/design/materials-and-meshes.md) (Wind), [Procedural trees → Hierarchical wind](../../docs/design/procedural-trees.md#hierarchical-wind).

## What changed

- **Engine:** `WorldEnvironment.WindGusts` (0..1) → `FrameEnvironment.WindParams.W`; `include/wind.slang` `windGust`,
  used by `windHierarchy` (same result) and `windOffset` (when `windParams.w` > 0); the four foliage vertex shaders'
  `.spv` and their lock lines.
- **Forest:** `WindGusts = 0.4` (`ForestScene.cs` and `forest.mfscene`).
- **Tests:** render `GustsVaryTheSimpleWind` (`foliage-wind --count 2`), golden `foliage-gusts_frame0090.png` on moltenvk
  and lavapipe; the steady `foliage-wind` golden checked unchanged in the same test.
- **Docs:** materials-and-meshes.md (Wind → Gusts), procedural-trees.md (Hierarchical wind).
- **Forest tests:** `just forest-test` 227 passed (the scene file and the generator agree).

## Decisions

- [ADR 0278](../decisions/0278-gusts-on-the-simple-wind.md); `--bake-tree-clusters` dropped there.

## Dead ends

- `just shaders` again rewrote the four unrelated `.spv` files (DebugLines, Gizmo, GridAnalytic, SceneGrid); restored.

## Verified

- The gust golden inspected beside the steady one: the cards stand a little differently (the gust strengthens the sway
  at t = 1.5 s); the steady and tree goldens unchanged.

## Not done

- Godot's visibility-range fade margins on every `GeometryInstance3D` and impostors on `Tree3D` (G8e.5's larger items;
  `TreeScatter` already cross-fades and draws impostors).
- A person should watch the Forest's meadow in the wind: gusts are a motion, not a still.

## State changes

- milestones G8e: in the status cell, after "G8e.5 Ez Tree foliage and impostors (0172)", add "with grass gusts
  ([ADR 0278](../decisions/0278-gusts-on-the-simple-wind.md))".
