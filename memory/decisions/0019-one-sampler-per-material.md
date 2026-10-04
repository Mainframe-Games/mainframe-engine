# ADR 0019 — One sampler per material (separate images)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W3 lane m3b)

## Context

MoltenVK limits a shader stage to 16 samplers (`maxPerStageDescriptorSamplers`). The shadow set already uses 15:
4 directional, 7 spot and 4 point. `limits.json` reserved one sampler for "1 material texture". A material binds
up to three textures: albedo, normal and emission.

## Decision

- Material set 2 binds one `VK_DESCRIPTOR_TYPE_SAMPLER` (b1) and three `SAMPLED_IMAGE`s (b2–b4). The shader
  combines them with `sampler2D(texture, materialSampler)`.
- The sampler (filter, wrap, anisotropy) is that of the material's first bound texture, in the order albedo,
  normal, emission. A material without textures uses a linear/repeat default.
- Empty slots bind 1×1 fallbacks (white sRGB, flat normal); flags in the parameter UBO tell the shader to skip
  them.

## Consequences

- The fragment stage stays at 16 samplers on MoltenVK, with up to three material textures. Sampled images have a
  separate, much larger limit (128).
- All textures of one material share the sampler state; per-texture wrap modes within a material are not possible.
  Shadows v2 (M4, atlas) frees samplers and can revisit this.
