# ADR 0117 — 2D point lights and gradient textures

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM4 / E6 (the game's `docs/porting.md`)

## Context

The game lights its crew members (a torch) and placed buildings with Godot `PointLight2D`s over a `CanvasModulate`
night tint; both light textures are radial `GradientTexture2D`s built in code, and the intro sweeps a linear one. The
canvas (ADR 0111) had no lights and no generated textures.

## Decisions

1. **`PointLight2D : Node2D`** with Godot's names: `Enabled`, `Texture`, `TextureScale`, `Offset`, `Color`, `Energy`,
   `BlendMode` (add, sub, mix), `RangeItemCullMask` against `CanvasItem.LightMask`, `RangeZMin/Max`. `ShadowEnabled`
   is kept for ported scenes but shadows, normal maps, `height` and `DirectionalLight2D` are not implemented.
2. **Godot's formula:** in the fragment stage, after the canvas modulation, each light maps the target-pixel vertex
   through `inverse(canvas × light global × texture rect)` to its texture UV; outside [0, 1) it adds nothing; otherwise
   `light = texture × colour × (alpha × energy) × the item's own colour`, blended into the colour (add: `+= rgb × a`).
   Unshaded materials skip lights as they skip the modulation.
3. **One light block per frame:** up to eight lights (`CanvasFrame.MaxLights`, warned once beyond) in a per-frame-slot
   UBO plus eight combined samplers (set 1 of the default canvas layout, set 2 of a canvas-shader layout); each batch
   carries a light bitmask in its push constants (`canvas_pc.lights.x`), computed per item from light mask and z, and a
   different mask breaks a batch. A canvas's lights only light its own items.
4. **`Gradient` + `GradientTexture2D`:** Godot's sampling (sorted points, exact hits, linear or constant, clamped) and
   fills (linear, radial, square, conic; repeat none/repeat/mirror; RGBA8, no HDR), pixels rebuilt lazily after a
   change. `Texture2D` is no longer sealed so a generated texture is a `Texture2D` everywhere (sprites, lights,
   shaders) and saves with a scene as a sub-resource.

## Consequences

- Canvas shaders compiled before this change are stale (`just canvas-shaders`): the push block grew to 112 bytes.
- A ninth light in one frame is dropped; raise `MaxLights` (shader constant too) when a game needs more.
