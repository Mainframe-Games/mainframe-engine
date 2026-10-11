# ADR 0186 — 32 2D lights per frame, culled, sharing texture slots

- **Date:** 2026-10-11
- **Status:** accepted
- **Milestone:** Crash Site Defense art polish
- **Docs:** docs/design/canvas.md#lights-adr-0117

## Context

ADR 0117 allowed eight `PointLight2D`s per frame, one combined image sampler each, collected in tree order with no
culling: a 2D game with a camp of fires, torches and lamps across a large world ran out, and lights far off screen used
up the slots.

## Decision

- **Culling.** A light whose texture rect (in target pixels) misses the drawn area is left out of the frame.
- **32 lights** (`limits.json` `MAX_CANVAS_LIGHTS`), still one bit each in a batch's `uint` light mask, so the push
  constants and batching are unchanged; the light block grows to 2 KB.
- **Texture slots.** Lights share `MAX_CANVAS_LIGHT_TEXTURES` = 8 sampler slots, one per distinct texture
  (`CanvasFrame.LightTextures`; `flags.y` is a light's slot), which keeps the per-stage sampler count where it was for
  MoltenVK. A light whose texture would be a ninth distinct one is skipped with a warning, as is a 33rd light.
- **Canvas shader locks follow the includes.** `CanvasShaderBuild.IncludeRevision` (now 2) is part of a `.gdshader`'s
  lock hash, so a change to `canvas.slang`/`canvas_lights.slang`/`limits.slang` makes every compiled canvas shader stale
  (`mf-shaders check` fails) instead of silently keeping the old light block.

## Consequences

- Render test `canvas-many-lights` (32 lights in a grid, 12 culled off-screen lights added first) and its golden; unit
  tests for culling, slots and the cap.
- Games with committed `.gdshader` SPIR-V rebuild it once (`mf-shaders build <folder>`).
