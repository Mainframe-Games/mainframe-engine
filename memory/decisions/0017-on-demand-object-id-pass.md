# ADR 0017 — Object IDs from an on-demand pass, read back without stalling

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W3 lane m3b)

## Context

The editor (M10) needs to pick objects under the mouse. There were two options:

- an `R32_UINT` attachment written by every scene pipeline (MRT in the main pass);
- a separate ID pass.

MRT would change the attachment count of the scene pass, so every scene pipeline would need a second blend
attachment: sky, grid, Spine, meshes. Games would pay the bandwidth every frame.

## Decision

- An **object-ID pass** renders into its own `R32_UINT` + depth `RenderTarget` with the `MeshObjectId` pipelines.
  It draws the same sorted draws and instance data as the colour pass; cutout materials still discard and blended
  ones write ids. Id = `NodeId` (32-bit; 0 = nothing).
- It renders only when needed:
  - the main view, on frames with pending picks;
  - a `SubViewport`, every frame when `ObjectIds` is set, or when picks are pending.
- The requested pixels are copied into per-frame-slot host-visible buffers (64 per frame). The copies complete once
  `DeletionQueue.CompletedFrame` passes their frame, about two frames later.
- The API is `RenderServer.PickAsync(x, y)` → `Task<PickResult>`, which completes on the render thread. The polling
  form is `RequestPick` + `TryGetPickResult`. Both have `SubViewport` variants.

## Consequences

- No cost for games that never pick. The frame never waits for the GPU.
- Picks are 2–3 frames late, which is fine for clicks. Hover in the editor uses `SubViewport.ObjectIds`.
- Only `GeometryInstance3D`s are pickable. Spine, the grid and the sky write nothing (id 0).
