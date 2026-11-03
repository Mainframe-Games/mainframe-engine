---
summary: >-
  Editor gizmos are anti-aliased lines with a width in pixels, shaded solid heads and SVG scene icons (a new engine
  GizmoDrawList); the editor grid is an analytic, zoom-following Grid3D style; debug lines no longer z-fight surfaces.
---

# 2026-11-04: Thick, shaded editor gizmos, SVG scene icons and an analytic grid

Branch `feature/editor-gizmo-quality`. Plan: [Thick, shaded editor transform gizmos](../plans/2026-11-04-editor-gizmo-quality.md).
Design: [Gizmos](../../docs/design/gizmos.md), [Scene grid → Analytic style](../../docs/design/scene-grid.md#analytic-style).

## What changed

- Engine: `GizmoDrawList` (lines with a width in pixels, expanded to quads in the vertex shader, anti-aliased with round
  caps; CPU-shaded, back-face-culled cones and boxes with anti-aliased silhouettes; discs, triangles; icons from a
  `GizmoIconAtlas`), drawn by `GizmoRenderer` (`Shaders/Gizmos/Gizmo`) from `SceneViewport.Gizmos` (depth-tested) and
  `OverlayGizmos` (on top), in the scene pass, after post, and on 2D canvases (`CanvasFrame.AppendGizmos`).
- `TransformGizmo`: 3 pt shafts and rings (4.5 pt hovered), solid cones/cubes, a centre disc, rings with faded far halves
  and a silhouette, a sector showing the turn while dragging, yellow active handle with the others faded, axes that face
  the camera fade out; picking follows the drawn shapes (`GrabMargin` 5 pt beyond shafts, heads and rings, near ring
  halves first). `TransformGizmo2D` and the pivot crosses draw through the same list.
- Scene icons: six SVG badges (`MainframeEngine.Editor/Assets/gizmos/`) rasterised by ThorVG at the view's pixel scale,
  ringed in the light's colour (selection colour when selected), on top; light directions, spot cones, ranges and camera
  frusta as 1.5 pt anti-aliased lines.
- `Grid3D.Style` (`Lines`, the default, unchanged; `Analytic`, the editor's): per-pixel minor/major lines with widths in
  points, ×10 level steps that cross-fade with zoom, per-direction crowding fade, distance and grazing fade, X/Z axes. The
  2D editor grid is gizmo lines too (1/1.5/2 pt).
- Debug lines pull their depth 8 pixels' footprint towards the camera in both passes (was 4, after post only), against
  z-fighting with the surfaces they outline.
- QA: `Tests/QA/editor-gizmos.qa`; the QA script's `gizmo-drag` takes `hold` (capture mid-drag).

## Decisions

- One list type for lines, solids and icons, one pipeline per target: painter's order between them comes for free.
  Solids are shaded and culled on the CPU, so the shader has a single path and no per-list depth buffer is needed (only
  convex shapes; heads are drawn far to near).
- The analytic grid is a `Grid3D` style, not a replacement: the line grid stays the default, so the render-test goldens
  that contain a grid keep (owner, at plan review).
- Icons on top (like Godot) rather than depth-tested: always findable and clickable.
- Past the plan: the owner added the scene icons and the collider z-fighting during the work, so light/camera/audio
  shapes, the 2D grid and debug-line depth changed too (the plan's "only the gizmo changes" no longer holds); the solid
  heads got anti-aliased silhouette edges, which the plan had left out, after the first captures showed jagged cones.
- The editor test harness now drops the frame's debug lines and gizmos before each tick, as the render server does;
  before, the lines piled up and the idle allocation gate only passed because their growth was slow.

## Dead ends

- Lines first expanded each end in its own direction to the other end: the across-line coordinate flipped sign at the
  far end, so shafts came out striped. Both ends now work in the frame of the lexicographically first end.
- A TAA render test of lines lying on a crate and a grazing floor in the root view does not reproduce z-fighting on this
  NVIDIA driver even without the pull (lines and triangles get the same depth there); it stays as coverage. On lavapipe
  it does: CI's `physics-joints` and `physics-debug` frames show collider outlines whole where the old goldens had them
  half hidden in the faces (the floor's edge, the cubes' lower edges), and those two goldens were re-recorded from CI.
- The local Docker runs did not catch that: the shared `mainframe-linux-work` volume held newer `.spv` build outputs
  from another checkout, so the incremental shader build kept the old `DebugLines` fragment shader. Results from that
  volume are only trustworthy for shaders whose outputs are new.

## Verified

- `just build`, `just test` (engine 3 241, editor 1 811 after rebasing on #163), `just format-check`, `check.sh`, `just shaders` (only this
  branch's `.spv` kept: the Linux slangc changes other files' bytes) and `sh build/shaders.sh check`.
- New render tests on this machine (NVIDIA, no goldens for its tag): `GizmoRenderTests` (line widths, blended edges,
  shaded solids, outlines under TAA, analytic grid widths and a calm horizon, 0 B per frame for both scenes).
- `just qa-editor Tests/QA/editor-gizmos.qa` and a 2D/physics script: captures of each mode idle and mid-drag, icons,
  a selected spot light, the 2D gizmo, collider outlines, reviewed by eye.

- `lavapipe` (Docker): the gizmo tests pass; `editor_frame0051`, `editor-viewport-tools_frame0040` and
  `editor-filesystem_frame0040` changed on purpose (the analytic grid, icons, gizmo; the filesystem frame also shows the
  `Shaders` folder `main`'s template gained) and were re-recorded with the new `gizmos` and `grid-analytic` goldens,
  each inspected next to the old one.
- Release `-warnaserror` build, `just test-render` on this GPU (225 passed), `just docs-build`; after the rebase the
  lavapipe editor and gizmo tests again (19 passed).

## Not verified

- `moltenvk` goldens (no Mac): the three editor goldens change there too, and `gizmos`/`grid-analytic` need recording
  (`just golden-update` on a Mac).
- The flicker the owner saw on colliders, in their scene: the stronger pull is the fix, not reproduced here.
- HiDPI (2×) by eye: sizes follow `ViewScale`, covered by the hit-test scale test only.

## Next

- Owner look check; tune widths/sizes if wanted. Follow-ups: plane handles (XY/XZ/YZ), a screen-space rotation ring.
