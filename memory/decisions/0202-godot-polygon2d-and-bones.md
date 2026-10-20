# ADR 0202 — Godot's Polygon2D, Skeleton2D, Bone2D and RemoteTransform2D (CPU skinning)

- **Date:** 2026-10-20
- **Status:** accepted
- **Milestone:** — (a Godot-compatibility set ahead of G17)
- **Docs:** docs/design/canvas.md#polygons-and-bones-adr-0202, docs/design/future/skeleton-2d.md

## Context

Buy My Snake Oil's soft bodies (mainframe-engine#52) come from the softbody2d addon:
- a textured `Polygon2D`, about 600 vertices with UVs and internal vertices;
- one `RigidBody2D` per region;
- a `Skeleton2D` with one `Bone2D` per body, each bone driven by a `RemoteTransform2D` on its body;
- per-vertex bone weights on the polygon, so the image deforms with the physics.

The game ports the addon to C#, but it needs the node set. G17 (`skeleton-2d.md`) had reserved `Skeleton2D` for the
engine's Spine-like skeletal node and ruled out Godot's bone-node model.

## Decision

- **Ship Godot's full set under Godot's names** (Brogan, 2026-10-20): `Polygon2D`, `Skeleton2D`, `Bone2D`,
  `RemoteTransform2D`. G17's node becomes **`SkeletonSprite2D`** (beside its `SkeletonSprite3D`). The G17 proposal,
  animation graph, skeleton editor and milestones are renamed, and G17's open question 5 is closed.
- **`Polygon2D` follows `polygon_2d.cpp`:**
  - outline or per-polygon triangulation, with internal vertices dropped when there are no polygons;
  - UVs in texture pixels through offset, rotation and scale;
  - vertex colours, else `Color`;
  - bones as `BonePaths` (relative to the skeleton) and `BoneWeights` (one array per bone), plus Godot's bone methods;
  - per vertex, the four heaviest weights, normalised. Snake Oil's weights sum to 3 and skin like normalised ones.
    Unweighted vertices stay put.
- **Skinning is on the CPU** in `OnDraw`, using Godot's maths: in skeleton space, Σ wᵢ (boneᵢ · restᵢ⁻¹). At the
  bench's size, four polygons of 600 vertices and 25 bones cost about 0.15 ms per frame. GPU skinning remains G17's
  job.
- **Redraws are pushed, not polled.**
  - A `Bone2D` tracks its global transform. A change asks its skeleton's polygons to redraw, and bone additions and
    removals make them re-resolve their bones.
  - `RemoteTransform2D` pushes in the frame's transform notifications. Those run after physics writes its poses and
    before the canvas server draws, so a polygon shows the bodies where they are this frame.
- `Skeleton2D` is a bone container only. Godot's modification stack (look-at, CCDIK, FABRIK) is not supported; the game
  aims its bones itself.
- `DrawTriangles` no longer allocates. `CanvasDrawList.AddVertices` gains an overload that builds the vertices in place
  from spans.

## Consequences

- Godot 2D rigs port node for node. Steady skinned redraws allocate nothing.
- Two "skeleton" concepts exist: Godot's bone nodes here, and G17's data-driven `SkeletonSprite2D` later. Each is
  documented as the other's sibling.
- There are no editor gizmos for bones yet; the debug view shows the polygon as drawn.
