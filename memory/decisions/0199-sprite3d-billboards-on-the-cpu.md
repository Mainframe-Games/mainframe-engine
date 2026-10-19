# ADR 0199 — Sprite3D billboards on the CPU

- **Date:** 2026-10-20
- **Status:** accepted
- **Milestone:** —
- **Docs:** docs/design/materials-and-meshes.md#billboards-adr-0199, docs/design/future/rendering-features.md

## Context

Buy My Snake Oil's 3D areas are paper cut-outs: every prop and NPC is a `Sprite3D` with Godot's `billboard = 2`
(Y-billboard), lit, alpha-cut and casting shadows under the area's sun. NPCs flip to face their walk direction
(mainframe-engine#55). The G6.3 plan (`rendering-features.md`) puts `BillboardMode` on `StandardMaterial3D` and builds
the basis in the vertex shader, which has three costs:
- About nine vertex shaders would need the change: colour, extended-stream, prepass, ID, and the 2D and point shadow
  casters, each opaque and cutout.
- Vertex-stage specialization isn't wired up: `PipelineBuilder` specializes the fragment stage only.
- Shadow passes can't see the camera, because the frame UBO isn't bound there.

The plan therefore left billboards without shadows, which Snake Oil needs for parity.

## Decision

- **`Sprite3D.Billboard`** (`BillboardMode`: `Disabled`, `Enabled`, `FixedY`; Godot names), plus **`FlipH`/`FlipV`**.
  The flips are UV flips: the private material's `UvScale`/`UvOffset`, which every colour, prepass, ID and cutout
  shadow shader already applies.
- **Billboarding is done on the CPU in `MeshRenderer.Prepare`.** It runs per view, with that view's camera.
  `Billboard.Face(model, mode, view)` keeps the node's position and its basis lengths (Godot's `billboard_keep_scale`,
  as `SpriteBase3D` sets it), and takes the rotation from the camera. For `FixedY` that is world up and Godot's
  `cross(up, back)` facing, with the camera's right vector when looking straight down.
- **The billboarded matrix travels with the items.** `MeshDrawItem.Model` and `ShadowCasterItem.Model` carry it, and
  the four instance writers use it: colour, previous-frame motion, prepass and shadow casters. Culling uses its
  bounds.
- Shadows therefore come from the quad as the view that collected the casters sees it, which is the main view. That
  is Godot's `MAIN_CAM_INV_VIEW_MATRIX` behaviour. Motion vectors are exact, because `MotionHistory` stores the
  billboarded matrix.
- A virtual `GeometryInstance3D.RenderBillboard` is how a node asks for it, so `Label3D` (mainframe-engine#56) can
  share it.

## Consequences

- No shader, pipeline or lock changes. Billboarded sprites keep their pipelines, and each sprite remains one draw.
- `StandardMaterial3D` still has no `BillboardMode`, so `MeshInstance3D`s and particles can't billboard. G6.3 can add
  the material mode later, reusing `Billboard.Face` for non-instanced draws and moving to the vertex shader for
  instanced particles.
- Two prepassed views of one world in the same frame share one motion history per node. A billboard's previous matrix
  is then the last view's, the same limitation as before.
- The editor's selection box is the node's AABB in its own rotation, not the billboarded quad's.
