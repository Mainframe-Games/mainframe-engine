---
summary: >-
  The intermittent 3096 B (and 12312 B) failure of ShowcaseRecordingTheProfilerAllocatesNothing was the UI geometry
  arena's region list and pending queue growing at a timing-dependent frame; both are now sized per chunk up front.
---

# 2026-11-04: UI geometry arena allocation flake

Branch `fix/profiler-alloc-flake`. No plan (a small fix).

## What changed

- `FreeListBlock` takes a `regionCapacity` (default 16, as before).
- `UiGeometryArena` chunks start with room for `ChunkRegions` (2048) regions, and its pending-release queue holds
  `ChunkRegions` per chunk: sized at construction and when a chunk is added (every pending range is still allocated in
  a chunk, so it never needs more).
- `FreeListBlockTests.AFragmentedBlockWithinItsRegionCapacityAllocatesNothing`; docs: `game-ui.md` (geometry),
  `gpu-resources.md` (placement row).

## Decisions

- Size from a bound instead of a bigger guess: the pending queue is bounded by the chunks' regions; the region list by
  one region per 512 B of a 1 MiB chunk (the showcase peaked between 256 and 512).

## Dead ends

- The failures were the same size on different branches (CI runs 41585997425 and 41730803454). 3096 = 128 × 24 B + 24 and
  12312 = 512 × 24 B + 24 pointed at a 24-byte element array doubling, but several structs are 24 B (`ProfilerEvent`,
  `FreeListBlock.Region`, the arena's and the descriptor-set pending tuples). Reading code did not settle it.
- Finding it: the render-test host repeated locally (`showcase --alloc 120:300 --hidden --profile`,
  `DOTNET_TieredCompilation=0`) failed about 1 run in 10 on an RTX 3070 Ti. A temporary host change turned on
  `ProfilerOptions.TrackAllocations` and printed the allocating frame's scopes; temporary `ProfilerMarker`s inside
  `UiServer.Process` and `VulkanUiRenderer` narrowed it to `context.Render` → `CompileGeometry` → `_arena.Allocate`.
  All diagnostics were reverted.
- Fixing only the region list left 3096 B in 4 of 40 runs: two sources shared the path, the region list (12312 B at
  512) and the pending queue (3096 B at 128).

## Verified

- Before: 4 of 40 local host runs allocated (3096 B). After both fixes: 40 of 40 at 0 B with eight busy-loop CPU
  burners running alongside.
- The new unit test fails with the old 16-region start (97 704 B) and passes with the fix.
- `just build`, Release `-warnaserror`, `just format-check`, `just test`, `just test-render`.

## Not verified

- lavapipe in CI over many runs (the flake's home); one CI pass is weak evidence for a 1-in-10 failure.

## Next

- `VulkanUiRenderer._pendingSets` (descriptor sets, 24 B entries, starts at 32) has the same shape; it did not show up
  in any run, so it is unchanged.
