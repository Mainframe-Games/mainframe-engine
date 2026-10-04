# ADR 0016 — Normal maps without vertex tangents

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W3 lane m3b)

## Context

Tangent-space normal maps need a tangent frame. Storing tangents per vertex would grow the vertex from 32 to
48 bytes. That breaks the shadow pipelines' 32-byte stride, and every mesh source (generators, importers) would
have to produce MikkTSpace-compatible tangents.

## Decision

- The standard vertex stays 32 bytes: position, normal, UV.
- `include/material.glsl` reconstructs the tangent frame per pixel from screen-space derivatives of the world
  position and UV:
  - dP/du and dP/dv are solved with the 2×2 inverse of the UV Jacobian, so they keep their sign under mirrored UVs
    and the flipped viewport;
  - both are orthogonalised against the interpolated normal.
- Maps follow the OpenGL/glTF convention: +Y points up in the image, towards decreasing v.

## Consequences

- No tangent data anywhere. Normal maps work on any mesh with UVs.
- The result can differ slightly from baked MikkTSpace tangents, mostly at UV seams and on low-poly curved
  surfaces. It is acceptable for Blinn-Phong (ADR 0014). Revisit with PBR if asset fidelity requires it.
- The derivative cost is only paid when a normal texture is bound (uniform branch).
