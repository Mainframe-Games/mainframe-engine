# ADR 0004 — Renderer stabilization (M1) choices

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M1 (W1 lane A)

## Context

M1 fixed the correctness bugs found while documenting M0 (multi-light shadows — GitHub issue #2 —,
swapchain-image-count changes, double depth remap, Spine without shadows, small engine fixes). The
proposal left two open questions and a few implementation choices.

## Decisions

1. **Per-pass light matrix: dynamic-offset UBO ring (proposal option B)**, not push constants. One
   host-mapped buffer of `MaxFramesInFlight × 35` slots, each `max(256, minUniformBufferOffsetAlignment)`
   bytes (`UniformRing`); set 0 of the shadow layouts is `UniformBufferDynamic`. No dependency on
   `maxPushConstantsSize > 128`, caster code unchanged, and it scales to cascades (M4).
2. **Frame slots, not swapchain images.** `IVulkanContext.FrameSlot` + `const MaxFramesInFlight = 2`;
   all per-frame resources are keyed by slot. Command buffers are per slot (allocated once), fences
   and acquire semaphores per slot, render-finished semaphores per image (recreated when the image
   count changes). `imagesInFlight` was dropped: the slot fence already protects every slot resource.
3. **Reversed-Z: deferred.** The double remap is removed (depth is the plain [0, 1] System.Numerics
   projection everywhere). Reversed-Z would change every depth compare/clear and the grid shader; it is
   a precision optimisation for M3/M4 when large scenes exist.
4. **Validation: build configuration default + option override.** `EngineOptions.EnableValidation`
   defaults to `EngineOptions.DefaultEnableValidation` (true in Debug engine builds, false in Release);
   tests and tools set it explicitly.
5. **No-shadow rendering: renderer-owned fallback set 2** (`ShadowFallback`): same layout, 1×1 maps
   cleared to 1.0, matrices mapping every point to depth 2 (lit). Lit pipelines always declare sets
   0-2 (+3 for Spine textures), so one shader variant serves both cases.
6. **Shadow culling made explicit:** `FrontFace = Clockwise, CullMode = Back` in the unflipped shadow
   passes — identical rasterisation to the previous `CCW + cull Front`, which was mislabelled as
   front-face ("Peter Pan") culling. Real front-face culling would drop single-sided casters (Spine,
   quads); revisit with Shadows v2.
7. **Minimised window:** the engine stops rendering and sets `IWindow.IsEventDriven` (SDL_WaitEvent)
   until restored; the renderer never spins waiting for a non-zero extent.
8. **Spine:** `SetAnimation` replaces (new `QueueAnimation` appends); update order is
   AnimationState.Update → Apply → Skeleton.Update → UpdateWorldTransform; CPU/GPU vertex storage
   grows by doubling instead of a fixed 8192 cap; atlas pixels are released after upload.
   `SpineScale` default stays 0.02 (`SpineNode.DefaultSpineScale`); the Sandbox/test scenes' `0.001`
   overrides, which never applied before, were removed so the visible size is unchanged.

## Consequences

- Goldens re-recorded on MoltenVK (grid fade near the horizon changed with the depth fix; Spine now
  starts on `walk`); lavapipe goldens are still to be recorded from CI.
- `ShadowSystem.RenderShadows` must be called at most once per frame.
- The engine's validation default differs between Debug and Release; anything that gates on
  validation must set `EnableValidation = true` explicitly (render-test host does).
