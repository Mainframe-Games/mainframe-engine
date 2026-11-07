# ADR 0277 — The glow map is the post tonemap's set 1, a ring per tonemap pass

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G8e (G8e.4 rest)
- **Docs:** docs/design/color-pipeline.md (Glow map), memory/plans/2026-11-07-glow-map.md

## Context

ADR 0168 shipped G8e.4's cinematic post chain without Godot's `glow_map` (lens dirt) because the post tonemap's set had
no binding to spare in its design. The tonemap set binds the HDR scene, seven glow levels, the adapted luminance and the
light shafts (10 sampled images of the stage's 16), and both of its sets are written once and never rewritten, so a
texture that changes when a profile is edited needs its own descriptor management.

## Decision

- **`PostProcessProfile.GlowMap` (`Texture2D?`) and `GlowMapStrength` (0.8)**, Godot's names and default, in the Glow
  group and `PostProcessSettings`; the gathered glow becomes `lerp(glow, glow × map(uv), strength)` (Godot's formula),
  in `gatherGlow`, so it applies in every blend mode and both glow qualities.
- **Set 1 of the tonemap pipeline layout, one combined image sampler** (`GlowMapSets`), not binding 4 of set 0: the
  map changes independently of the glow chain, and its own small ring (`MaxFramesInFlight + 1` sets, the colour grade's
  LUT pattern) writes a changed map into a slot no frame in flight binds. The map uploads through `TextureGpu` (sRGB,
  its import sampling); without a map, or until it uploads, the set binds a 1 × 1 white texture and the push block's
  `glowMapStrength` is 0. Both tonemap paths (the main view, `PostTonemapPass` for sub-viewports) own a ring.
- **Stretched over the screen**, as Godot does: no aspect-preserving fit.

## Ruled out

- **A fifth binding in set 0:** set 0's two sets are never rewritten (the shafts' set exists so they need not be); a
  map edit would have needed a third copy per map or update-after-bind.
- **A separate lens-dirt effect after the tonemap:** the dirt should multiply the glow, not the image; outside the
  tonemap pass the glow is no longer separate.

## Consequences

- One more sampled image in the tonemap pass (11 of 16) and one descriptor-set bind per frame; 0 B per frame.
- Every existing golden is unchanged (no map → strength 0); `glow-map` adds one on both drivers.

## Revisit when

- A game wants the dirt to keep its aspect on wide screens: add Godot-style fit modes then.
