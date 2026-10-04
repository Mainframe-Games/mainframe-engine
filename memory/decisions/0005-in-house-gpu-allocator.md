# ADR 0005 — In-house GPU allocator, upload and deletion queues

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M3 (W2 lane m3a)

## Context

Every buffer and image called `vkAllocateMemory` (dozens for the shadow maps alone; drivers cap the
count at `maxMemoryAllocationCount`, often 4096), uploads waited on `vkQueueWaitIdle`, and `Dispose`
waited on `vkDeviceWaitIdle`. The proposal's open question: an in-house allocator or a VMA binding. VMA
is a native library plus a binding package — a new dependency, which the plan forbids unless agreed.

## Decisions

1. **In-house `GpuAllocator`, no VMA.** 64 MiB blocks per memory type, best-fit free list with
   alignment, `bufferImageGranularity` padding between linear and optimal neighbours and coalescing;
   requests over 32 MiB get dedicated memory; memory types chosen from a usage enum (required /
   preferred / avoided flags) with fallback when a heap is exhausted; host-visible blocks persistently
   mapped (coherent memory only, so no flush bookkeeping). ~400 lines, fully unit-tested through a fake
   device. Not thread-safe: render thread only (asserted in Debug).
2. **Deletion by frame number, not slot index.** Objects released between a submit and the next acquire
   must wait for the last submitted frame, which a slot index cannot express; a monotonically increasing
   frame number collected after each slot-fence wait can.
3. **Uploads recorded at the start of the next frame's command buffer** (staging ring, batched
   barriers), so load-time and `OnUpdate` resources cost no queue wait. Work created mid-frame is
   submitted once with a fence (`SubmitAndWait`) — never `vkQueueWaitIdle`.
4. **Wrapper names `GpuBuffer`/`GpuImage`/`GpuTexture`** instead of the proposal's `Vk*`: the codebase
   aliases `VkBuffer = Silk.NET.Vulkan.Buffer`, and a `MainframeEngine.VkBuffer` type would break every
   such file (CS0576), including node code owned by another lane.
5. **Non-goals kept:** no defragmentation, aliasing or async compute.

## Consequences

- The multi-light scene uses 3 `VkDeviceMemory` objects (render test asserts ≤ 16).
- Node-side shapes still use raw allocations until the materials/mesh rewrite adopts the wrappers.
- If profiling ever shows the free list as a bottleneck (alloc+free ≈ 0.3 µs next to 256 live
  allocations), a TLSF or buddy allocator can replace `FreeListBlock` behind the same API.
