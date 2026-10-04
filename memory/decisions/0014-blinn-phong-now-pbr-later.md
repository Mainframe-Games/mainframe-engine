# ADR 0014 — Blinn-Phong now, PBR later

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W3 lane m3b)

## Context

The proposal asked when to move to PBR. The renderer already lit everything with Blinn-Phong: shapes, Spine, and
the `lights.glsl` shared by both. The colour pipeline (M3a) made lighting linear and HDR with ACES. A PBR model
(metallic/roughness, image-based lighting) needs environment prefiltering, a BRDF LUT and tuned shadows (M4).

## Decision

`StandardMaterial3D` ships with Blinn-Phong (`ShadingMode.BlinnPhong`) and `Unshaded`. Its parameters are:

- albedo colour and texture;
- a tangent-space normal map (ADR 0016);
- specular strength (default 0.3) and shininess (default 32), which reproduce the old shape shader exactly;
- emission colour, energy and texture;
- alpha mode (opaque, cutout, blend);
- cull mode and double-sided;
- UV transform.

`lights.glsl` gains `shadeLightsBlinnPhong(base, N, pos, specular, shininess)`. `shadeLights`, used by Spine,
keeps its constants.

PBR comes later as a new `ShadingMode` on the same material type, as Godot does. It will be a separate proposal,
after Shadows v2 (M4) and with image-based lighting.

## Consequences

- The existing render goldens (lit shapes, multi-light, Spine) still match after the port to `MeshInstance3D`.
- glTF PBR materials import approximately: base colour, normal, emission, alpha and double-sided only.
- Material names follow Godot (`StandardMaterial3D`). PBR will add properties rather than a new type.
