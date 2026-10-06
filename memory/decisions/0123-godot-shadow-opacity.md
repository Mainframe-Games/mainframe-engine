# ADR 0123 — Godot's shadow opacity

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM1 / E5 (the game's `docs/porting.md`)

## Context

The Driving Range sun sets `shadow_opacity = 0.45`: its shadows are soft-grey, not dark. The engine had no such
setting, so every shadow was full strength. In Godot (`Light3D::PARAM_SHADOW_OPACITY`, default 1;
`scene_forward_clustered.glsl` and `scene_forward_lights_inc.glsl`) the sampled shadow factor becomes
`mix(1.0, shadow, opacity)` before it attenuates the light's diffuse and specular terms, for directional, omni and spot
lights alike, after the cascade fade.

## Decisions

1. **`Light.ShadowOpacity`** (0..1, clamped, default 1) and **`Light3D.ShadowOpacity`** (exported in the Shadow group,
   range 0..1): a fully shadowed point keeps `1 - ShadowOpacity` of the light.
2. **Packed into free UBO slots**: directional `color.w`, spot `outerPad.y`. The lights UBO keeps its size (1200 bytes).
3. **Shader**: `lights.glsl` applies `shadow = mix(1.0, shadow, opacity)` right after `dirShadow` / `spotShadow`, so the
   cascade fade (already inside `sampleCascades`) comes first, as in Godot.
4. **Point lights ignore it**: their 32-byte entry has no free slot. Widening it to 48 bytes would change the UBO size
   for a feature no current game uses on point lights.

## Consequences

- With the default 1 the shader computes `mix(1, s, 1) = s` exactly: every existing golden is unchanged.
- Render test `ShadowTests.ShadowOpacityLightensTheUmbraLikeGodot` (scene `shadow-opacity`): the umbra at 0.45 is
  lighter than at 1, darker than the lit floor, and 0 leaves no shadow; golden `shadow-opacity_frame0008`.
- Known issue: point-light shadow opacity (documented in `docs/design/lighting.md`).
