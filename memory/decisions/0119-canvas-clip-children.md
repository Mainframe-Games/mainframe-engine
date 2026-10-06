# ADR 0119 — Canvas clip children as group targets

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM4 / E2 (the game's `docs/porting.md`)

## Context

The game's `Silhouettes` redraws a crew member behind an occluder as a flat tint, clipped to the occluder's pixels
with `CanvasItem.ClipChildren = AndDraw`. Godot implements clip children as a canvas group: it copies the target to
a back buffer, draws the owner and its children there, and composites the result with a screen-texture shader. The
engine's canvas had no back buffer or screen texture.

## Decisions

1. **One transparent target per group, keyed by owner**, sized like the pass it belongs to (main canvas or a 2D
   sub-viewport) and evicted like sub-viewport targets. The group pass runs before the viewport's pass that samples it.
2. **Clipping by blend, not stencil or shader:** the owner draws normally; every other member draws with the new
   `CanvasBlendMode.Atop` (the fragment output is premultiplied through `CANVAS_FLAG_PREMULTIPLY`; colour =
   src × dst alpha + dst × (1 − src alpha), alpha = dst alpha). Members keep their own materials and shaders.
3. **Composite = a premultiplied, unshaded quad** over the owner's drawn bounds, uv = target pixels ÷ pass size. The
   group is already modulated and lit, so the composite adds neither.
4. **The frame is culled first**, then the passes are emitted (a pass is a contiguous batch range), from pooled
   per-canvas lists (no per-frame allocation).
5. **Groups do not nest:** an owner inside a group is an ordinary member (Godot draws nested groups recursively; the
   game does not nest them).

## Consequences

- `ClipChildrenMode.Only` draws the owner like `AndDraw` (one RGBA target cannot hold the mask and a result whose
  alpha is the children's); nothing uses it yet. `ShowBehindParent` members of a group do not show.
- One extra render pass and target per active group; the game has at most one per occluded crew member.
