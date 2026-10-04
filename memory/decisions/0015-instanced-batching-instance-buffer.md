# ADR 0015 — Instanced batching with a per-frame instance buffer

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W3 lane m3b)

## Context

The proposal had push constants carry the model matrix: one `vkCmdDrawIndexed` per object. M3 requires
10 000 `MeshInstance3D`s at ≥ 60 fps with zero managed allocations per frame. On MoltenVK, 10k draws per pass, plus
the same again for every shadow pass, cost tens of milliseconds of CPU time.

## Decision

- Per-instance data goes into a per-frame-slot, persistently mapped **instance buffer**. Each instance is
  `MeshInstanceData`: the model matrix and the object id, 80 bytes. The vertex shader reads it at binding 1 with
  instance rate. The push-constant range stays declared but unused by mesh pipelines.
- Each frame and view:
  - draws are culled against the camera frustum;
  - opaque draws sort by pipeline → material → mesh → surface, transparent ones back to front;
  - the instances are written in sorted order;
  - each run of equal (pipeline, material, mesh, surface) is **one instanced draw** (`firstInstance` = offset).
- Shadow casters are batched the same way (cull mode, mirror, mesh surface) through new instanced pipelines in
  `ShadowSystem`.
- Pipelines are shared through a state-hash `PipelineStateCache`. Its key is the shader set, vertex layout, alpha
  mode, effective cull, mirror flag, depth write and render pass.
- Mirrored instances (negative determinant) use a clockwise-front pipeline variant. They are sorted into their own
  runs.

## Consequences

- 10k instances with one mesh and material take 2 colour draws and 2 shadow draws per light pass. The scene runs at
  the display rate (8.3 ms per frame on an M5) and allocates 0 B per frame.
- CPU cost is linear in instances: ≈0.14 ms to build and sort 10k, and ≈0.05 ms to write their instances.
- The instance buffer grows by doubling. A frame that outgrows it switches buffers mid-frame; draws already
  recorded keep the old buffer, through the deletion queue.
- Per-instance custom data (tint, …) needs a wider `MeshInstanceData` (and a new vertex layout id).
