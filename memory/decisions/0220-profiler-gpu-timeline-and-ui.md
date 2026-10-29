# ADR 0220 — Frame profiler: GPU lane aligned to submit, in-pass zones, no freeze key, DOM timeline

- **Date:** 2026-10-28
- **Status:** accepted
- **Milestone:** G10 Frame profiler (Gameplay toolkit)
- **Docs:** docs/design/profiler.md (GPU zones, Dev overlay section, Editor Profiler tab)

## Context

The profiler shows GPU zones under the CPU lanes, in the overlay and in the editor. Placing them on the CPU's time axis
needs either calibrated timestamps or an approximation. Apple GPUs (MoltenVK) sample timestamps at encoder boundaries,
so zones inside one render pass are not measured where they appear to be. The proposal also left open whether games
need a key to freeze the profiler, and whether the editor's timeline waits for G1.6's timeline canvas.

## Decision

- **Question 3, no calibrated timestamps.** `VK_KHR_calibrated_timestamps` / `VK_EXT_calibrated_timestamps` are not used.
  The CPU time domains they offer do not line up with `Stopwatch` reliably across macOS/MoltenVK, Linux and Windows,
  and the extension is not available everywhere the engine runs. Each frame's first GPU timestamp is placed at that
  frame's submit time on the CPU clock, the rest following by the timestamp period. That is enough for a frame view
  (which pass is slow, and how the GPU frame relates to the CPU frame that submitted it). The lane is always labelled
  "aligned to submit", in the editor and in exported traces ("GPU (aligned to submit)").
- **MoltenVK encoder boundaries: a marker kind, not a per-zone flag.** Zones that begin or end inside a render pass are
  markers of kind `GpuInPass` (`Tonemap`, `Overlay`, the detailed per-overlay-renderer zones). `GpuProfiler` reports
  `SamplesAtEncoderBoundaries` when `VK_KHR_portability_subset` is present, and the editor link sets an
  encoder-boundaries flag on every frame from such a GPU. The editor's timeline then shows in-pass zones with "≈" and a
  tooltip saying why; the overlay prefixes `GpuInPass` zones with "≈". Pass-level zones (whole render passes, compute
  and blit work) measure correctly and are the default; in-pass detail is opt-in (`DetailedGpuZones`).
- **Question 5, no freeze key.** The overlay has a Pause button and Freeze on spike (`FreezeOnSpike` pauses once the
  spike frame's GPU data is in); the editor has Pause and auto-freeze. F12 stays the overlay's only key; F5–F9 are taken
  in the editor and F8 is the RmlUi debugger in games.
- **Question 6, the editor timeline is a DOM pool first.** The timeline draws a reused pool of RmlUi elements, capped at
  1 500 bars (the widest are kept), positioned in percent of the view; the wheel zooms and dragging pans. If G1.6's
  timeline canvas lands, the Profiler tab switches to it; nothing outside `ProfilerPanel` depends on the pool.

## Alternatives rejected

- **Calibrated timestamps where present, submit alignment elsewhere:** two code paths and two meanings of the GPU lane
  for a small gain in precision; the label would change between machines.
- **The GPU lane on its own time axis:** loses the relation between a CPU frame and the GPU work it submitted, which is
  what the frame view is for.
- **A per-zone "approximate" flag on the wire:** the kind already says where a zone sits; the per-frame flag says
  whether this GPU makes that approximate.
- **A freeze key in games:** no free key that does not collide with the editor's or the debugger's.
- **Waiting for G1.6's canvas:** would tie G10 to G1's ordering for a view the pool draws well enough at the cap.

## Consequences

- GPU bars line up with the CPU frame to within the gap between submit and the GPU starting; absolute GPU start times are
  not exact.
- On Apple GPUs in-pass zone durations are approximate, and the UI says so.
- A frame with more than 1 500 timeline bars hides its narrowest ones until zoomed in.
