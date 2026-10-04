# ADR 0071 — One shadow atlas, six shadow samplers

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M4 (W4 lane m4)

## Context

M1 bound 15 shadow samplers: 4 directional, 7 spot and 4 point maps. All 15 were allocated up front, about 116 MiB.
MoltenVK allows 16 samplers per stage, so only 7 of the 8 spot lights could cast (`MaxShadowSpot = 7`), and the
material had a single sampler left (ADR 0019).

## Decision

- **Atlas:** spot lights and secondary directional lights render into **one atlas** (`sampler2DShadow`), one tile
  per light.
  - Tiles are allocated by a quadtree (buddy) allocator over power-of-two squares, largest first, which never
    fragments.
  - Over budget, the largest tiles are halved first; requests are dropped only at the minimum tile size.
  - The atlas grows to the smallest power of two that holds every tile, up to `MaxAtlasSize` (4096). It does not
    shrink while it is in use, is re-packed only when the requests change, and is released without users.
- **One render pass:** the atlas renders in a single render pass (one clear, one store), with a viewport and scissor
  per tile. On tile-based GPUs (MoltenVK), a render pass per tile would load and store the whole atlas each time.
  PCF taps are clamped inside their tile.
- **Shadow set:** b0 uniforms (std140 `ShadowUBO` with per-light shadow codes), b1 the cascade array, b2 the
  atlas, b3 four point cubes. That is **6 samplers**. Every map uses one immutable comparison sampler: MoltenVK has
  no mutable comparison samplers.
- **Point lights:** cubes are kept (up to 4), each created at its light's resolution.
- **Lazy maps:** every map is created when first needed and re-created when its size changes. Unused bindings point
  at 1×1 placeholders. A slot's descriptor set is rewritten at its next use.

## Consequences

- `MaxShadowSpot` = `MaxSpot` = 8: every spot light casts. The fragment stage uses 7 of MoltenVK's 16 samplers.
  ADR 0019's single material sampler could now become several, if needed.
- Memory follows the lights. The Sandbox uses 86 MiB (64 MiB of it the four 2048² cascades); a scene without lights
  uses none.
- Changing a light's resolution re-packs the atlas, or re-creates a cube, on that frame: a one-off cost.
