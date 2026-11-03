# Plan: Thick, shaded editor transform gizmos

**Status:** done, see [log](../logs/2026-11-04-editor-gizmo-quality.md)
**Docs:** [Editor → viewport](../../docs/design/editor.md) (gizmos, view resolution, Preview Post-Processing) ·
[Scene grid](../../docs/design/scene-grid.md) · [Post-processing → Sub-viewports](../../docs/design/post-processing.md#sub-viewports)
(ADR 0169: overlay drawn after the effects) · [Colour pipeline](../../docs/design/color-pipeline.md) ·
[Shaders](../../docs/design/shaders.md#writing-shaders) · [Testing](../../docs/design/testing.md)
**Started:** 2026-11-04

## Goal

The editor's transform gizmo (3D and 2D) looks like a professional editor's: shafts and rings with a real width in
pixels, anti-aliased, solid shaded cone and cube heads, a clear hover and active state, and hit areas that match what
is drawn. Collision shapes and other debug lines stay as they are (owner, 2026-11-04: "the collision lines are fine").

## How it is drawn today

`TransformGizmo.Draw` / `TransformGizmo2D.Draw` add 1-px segments to `SceneViewport.OverlayLines` (a `DebugLines`
line list, no depth test). Shafts are 1–3 parallel 1-px lines (`ThickLine`), cones and cubes are wireframes, rings are
two 1-px circles. 3D views draw the lines through `DebugLinesRenderer` (HDR scene pass, or after post in a
post-processed view); 2D views turn them into a canvas line batch (`CanvasFrame.AppendLines`).

## Design

- **`GizmoDrawList`** (engine, `Src/Rendering/Gizmos/`), a new immediate-mode list on `SceneViewport` (`Gizmos`, next to
  `OverlayLines`), drawn after the overlay lines, no depth test, in submission order (painter's). Two primitives in one
  vertex stream:
  - `Line(a, b, color, widthPixels)` / `Polyline` / `Ring`: world-space segments expanded in the vertex shader into
    screen-space quads `width + 2` px wide (6 vertices per segment; the vertex carries the other end, a side sign and
    the width; `frame.viewport` gives the pixel size). The fragment shader fades the outer pixel by the distance to
    the centre line: anti-aliased at any width, no MSAA.
  - `Triangles` / `Cone(tip, axis, height, radius)` / `Box(...)` / `Disc` / `Sphere`: solid meshes tessellated on the CPU
    with flat shading computed on the CPU (a head-light from the camera's upper left, ambient 0.55), so the shader has
    one path; back faces culled on the CPU (convex shapes), so no depth buffer is needed.
  Grows on demand, then 0 B per frame (like `DebugLines`).
- **`GizmoRenderer`** (internal, beside `DebugLinesRenderer`): one triangle-list pipeline per target, the same three
  cases as debug lines (HDR scene pass with sRGB→linear, after-post display output with a spec constant), per-frame-slot
  dynamic vertex buffers. Shaders `Assets/Shaders/Gizmos/Gizmo.vk.{vert,frag}.slang`. 2D views: `CanvasFrame` appends
  the list as canvas triangles (the CPU expands lines with a 1-px feather, as `ScreenGizmoBatch.Line` does).
- **`TransformGizmo` look** (all sizes in points × `PixelScale`, so DPI-aware; constants public for tests):
  - shafts 3 pt (`LineWidth`), hovered 4.5 pt; rings 3 pt, hovered 4.5 pt;
  - translate: solid cones (16 segments); scale: solid cubes; centre: a solid screen-facing disc (translate) or cube
    (scale) with a thin dark outline;
  - colours: the current red/green/blue, slightly brighter; hover = the axis colour lightened toward white and wider;
    active (dragging) = yellow, and the other axes dimmed to 35 % alpha while dragging;
  - rotate rings: the half facing away from the camera at 35 % alpha (it reads as a sphere, like Unreal's), plus a
    faint grey screen-space outer ring;
  - painter's order: the three handles are drawn far to near by view depth of their tip (stack-only sort of 3).
- **Picking**: `HitTest` uses the drawn shapes: distance to the shaft ≤ `width/2 + 5` pt, inside the projected head
  (distance to the head centre ≤ its on-screen radius + 3 pt), rings ≤ `width/2 + 5` pt with the front half preferred
  over the back half when both are in range.
- **`TransformGizmo2D`** gets the same widths, solid arrow heads (filled triangles), solid scale squares and a thick
  rotation ring, through the same list.
- **Pivot cross / multi-gizmo**: the pivot cross (`DrawGizmo3D`/`DrawGizmo2D`) moves to the list at 2 pt.

## Not in scope

- Collision shapes, joints, selection boxes, icons, paths, navigation: they stay `DebugLines` (1 px).
- New gizmo features: plane handles (XY/XZ/YZ squares), screen-space rotation ring, view-axis rotation, a combined
  "universal" gizmo. (Easy follow-ups on the same list; ask if wanted.)
- Anti-aliased silhouettes of the solid heads (they are rasterised without MSAA; the heads are small).
- `moltenvk` goldens: no Mac here (human check).

## Steps

- [x] **Branch.** `feature/editor-gizmo-quality` from `origin/main`, in a separate worktree (this checkout is shared with
  another session on `fix/editor-start-version`). Verified by: `git worktree list`.
- [x] **`GizmoDrawList`.** Lines, polylines, rings, cones, boxes, discs, the CPU shading and back-face culling; on
  `SceneViewport`, cleared with the overlay lines. Verified by: unit tests (vertex counts, culled faces, widths stored,
  0 B after warm-up with `AllocationGate`).
- [x] **Shaders + `GizmoRenderer`.** `Gizmo.vk.vert/frag.slang` (quad expansion, AA fade, display-output path),
  `just shaders`, commit only these `.spv` + `shaders.lock`. Hook into `RenderServer.DrawLines` and `DrawAfterPost`, and
  `CanvasServer` for 2D. Verified by: `just shaders-check`; a render test scene (`gizmo-lines`): a 3 px and an 8 px line
  and a cone, asserting driver-independently that the line's opaque core is the requested width ±1 px and its edge
  pixels are partly blended, no validation warnings, 0 B per frame.
- [x] **3D gizmo.** `TransformGizmo.Draw` → the list (look above), `HitTest` → the shapes above. Verified by: new
  `ViewportMathTests` cases (a click on the cone tip, on a shaft 5 pt off-centre, on the front vs back of a ring picks
  the expected handle; 2× `PixelScale` doubles the hit width) and the existing gizmo/multi-gizmo tests unchanged.
- [x] **2D gizmo.** `TransformGizmo2D` and the pivot crosses → the list. Verified by: `Viewport2DTests` (hit tests with
  the new widths) and the 2D smoke path in `EditorRenderTests`.
- [x] **Editor grid.** A `Grid3D.Style` (`Lines`, the default and today's line grid, so the render-test goldens stay;
  `Analytic`, the editor's): one large quad on the XZ plane whose fragment shader draws the grid from the world
  position with `fwidth` — minor lines every 1 m and major every 10 m, a level of detail that cross-fades to 10 m / 100 m
  as the cells shrink below a few pixels, lines `GridLineWidth` px wide (minor 1.25, major 2, axes 2.5, in view pixels
  × `PixelScale`), anti-aliased, red X / blue Z axes, faded with distance and at grazing angles. Depth-tested against the
  scene (writes no depth), drawn after post in a post-processed view like today. Verified by: a render test
  (`grid-analytic`) asserting no pixel far from a line is touched, lines near the camera are the requested width ±1 px,
  the far field fades to the clear colour without moiré (pixel variance in the far band below a bound), 0 B per frame,
  no validation warnings; the existing `sky-grid` test unchanged.
- [x] **Look check.** Editor screenshots of translate/rotate/scale, idle/hover/drag, at 1× and 2× (`just qa-editor`
  artifacts or a scripted capture), posted for the owner. Verified by: the owner.
- [x] **Goldens.** The editor window golden (and any other that shows the gizmo) changes: re-record `lavapipe` in Docker
  (`just render-tests-linux`), inspect each PNG. Verified by: `just render-tests-linux` green after re-recording.
- [x] **Docs.** `editor.md` (gizmo bullet, 2D gizmo, new list), a short "Gizmo draw list" section in a design doc
  (rendering of overlays: `editor.md` or a new `gizmos.md`, linked from AGENTS.md's structure list). Verified by:
  `check.sh` (links), `just docs-build`.
- [x] **Gates + ship** (`mf-ship`). Verified by: every gate in `.agents/workflow.md`.

Added by the owner after the plan started (2026-11-04):

- [x] **Scene icons.** Lights, cameras and audio nodes get SVG badge icons (rasterised at the view scale into a
  `GizmoIconAtlas`, on top, ringed in the light's colour), and their shapes become anti-aliased gizmo lines. Verified
  by: the QA captures and `GizmoDrawListTests.IconsUseTheirAtlasRectangle`.
- [x] **Collider lines z-fighting.** Debug lines pull their depth towards the camera in both passes. Verified by: the
  `gizmos-taa` render test (coverage only: the flicker does not reproduce on this driver) and the existing
  `SubViewportPostScene` floor check.

## Done when

- A 3D translate gizmo at 1× draws shafts whose opaque core is 3 px wide (6 px at `PixelScale` 2), with blended edge
  pixels, and solid shaded cones; scale draws solid cubes; rotate draws 3 px rings with a dimmed back half.
- Hovering a handle makes it visibly wider and lighter; dragging makes it yellow and dims the others.
- A click anywhere on a drawn shaft, head or ring picks that handle (tests above).
- The 2D gizmo draws through the same list with the same widths.
- No `DebugLines` producer other than the gizmo and pivot cross changed (collision shapes look as before).
- All gates pass, the `gizmo-lines` render test passes on lavapipe and this machine's GPU, 0 B per frame.

## Decisions at review (2026-11-04)

- Grid: added here, as an editor-only `Grid3D` style (the line grid stays the default, so render-test goldens keep).
- Widths: 3 pt / 4.5 pt hovered to start, tuned at the look check. Plane handles: later.

## Open questions (answered)

- **Grid.** The task also asked for thicker, anti-aliased grid lines with major/minor lines and distance fade. Owner:
  (a) leave the grid for now (you said the gizmo is what you mostly meant), or (b) add it here as an analytic,
  fragment-shader grid on a plane for the editor (thick AA lines at every zoom, minor/major levels that cross-fade with
  distance, coloured axes). (b) changes every render-test golden that has a `Grid3D` unless the editor uses a new
  `Grid3D` style and the line grid stays the default — which would you prefer?
- **Widths.** 3 pt shafts / 4.5 pt hovered — or thicker (Unreal's are ≈ 4 px at 1×)? Easy to tune after the look check.
- **Plane handles** (XY/XZ/YZ squares, as Unreal and Godot have): in this change or later?
