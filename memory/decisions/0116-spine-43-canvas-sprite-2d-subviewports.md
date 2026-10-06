# ADR 0116 — Spine 4.3, a canvas SpineSprite, and 2D sub-viewports

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM2 / E11, E7 (the game's `docs/porting.md`)

## Context

The game's crew rig is a Spine 4.3 binary `.skel` drawn by spine-godot's 2D `SpineSprite` inside a transparent 2D
`SubViewport` (the "Rig"), whose texture two `Sprite2D`s show (the body and its shadow: one skeleton per character).
The engine had spine-csharp 4.2 (JSON only in practice), a 3D-only `SpineNode` and 3D-only sub-viewports.

## Decisions

1. **spine-csharp 4.3:** `Plugins/Spine` (fork `Mainframe-Games/spine-csharp`, branch `4.3`) carries upstream
   `spine-runtimes` 4.3 sources unmodified (Unity/Mono colour files left out, the net8.0 project kept). The test and Demo
   SpineBoy are upstream's 4.3 export (PMA atlas).
2. **`SpineGeometry`** walks a skeleton's applied draw order like spine-godot's `update_meshes`: inactive bones and empty
   slots skipped, region/mesh sequences, skeleton × slot × attachment colour, clipping attachments. The 3D
   `SpineRenderer` and the canvas sprite share it.
3. **`SpineSprite : Node2D`** (+ `SpineSkeletonDataResource`, `SpineAnimationMix`): spine-godot's update order (state
   update → apply → skeleton update → world transforms with `Physics.Update`), `UpdateMode` Process/Physics/Manual,
   `TimeScale`, animation signals, `GetGlobalBoneTransform` in spine-godot's Y-down bone frame. The skeleton is computed
   Y-up and mirrored at draw time instead of setting spine's global `Bone.yDown`, which would flip the 3D `SpineNode`.
   Like spine-godot's 2D sprite, a premultiplied atlas is blended as it is (mix). Non-normal slot blend modes draw with
   the sprite's material (logged once) until a rig needs them.
4. **2D sub-viewports:** `SubViewport.Disable3D` views are drawn by the canvas server into their own RGBA8 target, as
   extra passes of the one `CanvasFrame` (children before parents, then the main canvas), cleared to transparent with
   `TransparentBg`; `GetTexture()` is a `Texture2D` backed by that target (`Texture2D.FromViewport`), sampled like any
   other. `UpdateMode` Once/Disabled keep the last image. 3D sub-viewports are unchanged (and skip 3D when 2D-only).

## Consequences

Godot's double alpha weighting when a transparent viewport texture is drawn with mix blending is reproduced on purpose
(parity). Lavapipe and MoltenVK goldens for `canvas` and the Spine scenes were re-recorded.
