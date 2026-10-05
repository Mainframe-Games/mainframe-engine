# ADR 0110 — 2D is Y-down, as in Godot

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM1 / E1 (the game's `docs/porting.md`)

## Context

Engine 2D was Y-up (`Node2D`, Box2D gravity −980, `CharacterBody2D.UpDirection` +Y, `Camera2D` looking down −Z, the
editor's 2D view). Godot, screen space, RmlUi and every coordinate of the game being ported (hex grid, saves, world
generation, UI) are Y-down. Engine 2D had almost no users yet (no 2D renderer), so this was the cheapest moment to
switch. Decided with Brogan in the port plan (D9).

## Decision

1. **2D space is Y-down engine-wide.** `Transform2D` maths is unchanged (axis-agnostic, Godot's formulas); positive
   rotation is clockwise on screen.
2. **Physics 2D defaults:** `PhysicsSettings2D.Gravity` = (0, +980) px/s², `CharacterBody2D.UpDirection` = −Y.
   Projects that wrote an explicit `physics.2d.gravity` keep it.
3. **`Camera2D` and the editor's 2D view look along +Z at the z = 0 plane from behind it, with up = −Y.** That is a
   rotation, not a mirror (a mirrored projection would flip winding for every pipeline). 3D visuals drawn by a 2D
   camera therefore show their back faces; 2D content is meant for the canvas renderer (ADR 0111), which has no culling.
4. The editor's 2D screen ↔ world helpers, grid (now drawn at z = +1, behind the plane), gizmo axes (Y arrow points
   down) and rotation drags follow.

## Consequences

- 2D physics, editor 2D and serialization tests were mirrored (y → −y) rather than pinned to an explicit Y-up setting,
  so they document the new defaults.
- Existing 2D content authored Y-up appears upside down; there was none outside tests.
