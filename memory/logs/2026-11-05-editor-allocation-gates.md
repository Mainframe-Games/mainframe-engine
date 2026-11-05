---
summary: >-
  The editor tests' last single-window 0-byte gates (video timeline, Transcript tab, adaptive tab and Sections lane,
  adaptive engine blocks) now measure through AllocationGate.SmallestWindow, after the timeline gate reported 2688 B
  on CI's macos-14 runner on a PR that does not touch video code.
---

# 2026-11-05: Editor allocation gates through AllocationGate

Branch `fix/editor-allocation-gates`. No plan (a small test fix).

## What changed

- `TimelineAllocationTests.SteadyFramesWithATimelineTabAllocateNothing` failed on CI (run 42005072790, #176's macos-14
  job) with 2688 B over its 100 steady frames: the parallel-load noise `AllocationGate`'s remarks describe (under one
  8 KB quantum, never seen locally). It measured one window.
- It and the other editor gates that measured one window now go through `AllocationGate.SmallestWindow` (up to three
  windows, the smallest kept), each window keeping its frame count: `ClipToolsTests` (Transcript tab, 100 frames),
  `AdaptiveEditorTests` (adaptive tab, 100 frames; the Sections lane, 20 draws), `AdaptiveLiveTests` (1000 adaptive
  blocks).
- The helper was already linked into the editor tests (#100); the other editor gates already take the smallest of
  several hand-rolled windows and are unchanged.

## Verified

- `just build` (0 warnings), `just format-check`, the editor tests (1685 passed, 5 skipped: plugin integration), `check.sh`.
