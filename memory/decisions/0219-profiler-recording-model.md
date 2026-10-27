# ADR 0219 — Frame profiler: recording model, always-on pass timing, no external sink

- **Date:** 2026-10-28
- **Status:** accepted
- **Milestone:** G10 Frame profiler (Gameplay toolkit)
- **Docs:** docs/design/profiler.md

## Context

The engine measured a frame as a few totals and four hand-timed GPU passes (shadows, SSAO, auto exposure, volumetric
fog), each with its own timestamp pool. There was no scope API, no frame history, no trace export and no editor view.
G10 adds a recorder for CPU scopes on any thread, GPU zones, counters and spikes, shown in the F12 overlay and an
editor tab and exported as Chrome traces. The proposal left open whether to forward to an external profiler, whether
pass-level GPU timing stays on when nothing records, what exported games ship, and whether node callbacks are
attributed automatically. Building it also had to settle which threads record and who turns recording on.

## Decision

- **Consumers turn recording on.** `Profiler` is static and process-wide (like `Log`). It records only while at least one
  consumer holds it (`Acquire`/`Release` tokens): the overlay's Profiler section while expanded and the overlay visible,
  the editor link's streamer between `ProfilerStart` and `ProfilerStop`, the editor's own tab when profiling the editor,
  and `--profile`/`--profile-trace`. `Paused` freezes the history without releasing anyone. Otherwise a scope is one
  static load and a branch, and the `AppContext` switch `MainframeEngine.Profiler.Disabled` makes `IsAvailable` a static
  readonly `false`, so tier-1 code drops the branch. The first `Acquire` preallocates every ring; nothing allocates per
  frame afterwards.
- **Named threads only.** The main thread always records; any other thread records only after
  `Profiler.SetThreadName(name)`. Unnamed thread-pool threads record nothing. The scene loader's worker is the "Loader"
  lane while it runs and releases it with `SetThreadName(null)`; a released lane is reused by the next thread of the
  same name. The audio callback is opt-in (`ProfilerOptions.AudioThread`). Background events are drained at each frame
  end and belong to the frame in which they ended. Staging rings: 32 768 events on the main thread, 8 192 on others; a
  full ring drops and counts.
- **Question 1, external profiler: none now (option A).** The built-in overlay, editor tab and Perfetto traces cover
  the frame view without a dependency. If a bridge to `dotnet-trace`/PerfView/`dotnet-counters` is wanted later, it is
  the .NET built-ins (option D: an `EventSource` mirroring markers, `System.Diagnostics.Metrics` for counters), fed from
  the same frame records at `EndFrame`. A Tracy native shim (B) and a community Tracy binding from NuGet (C) are
  rejected for now. G10.8 (the sink) is not built.
- **Question 2, pass-level GPU zones are always on by default.** `ProfilerOptions.GpuTimings = true`: the renderer writes
  timestamps around every pass and post effect (about 15–40 a frame) whether or not anything records, from one shared
  query pool read back after each slot's fence without waiting. The engine's existing GPU numbers
  (`ShadowSystem.LastGpuMilliseconds`, the post effects' `LastGpuMilliseconds`, `RenderServer.*GpuMilliseconds`, the dev
  overlay's Shadows panel, `ForestBenchmark`) become read-throughs to those zones, so they keep working with nothing
  recording, and the four ad-hoc pools are gone. A marker's GPU ms sums every view's zones of the frame.
  `DetailedGpuZones` (in-pass zones, one per overlay renderer) is off by default: on MoltenVK they collapse onto pass
  boundaries ([ADR 0220](0220-profiler-gpu-timeline-and-ui.md)). `ShadowSystem.LastCpuMilliseconds` stays a
  `Stopwatch`, since CPU scopes record only while held.
- **Question 4, exported games keep the profiler available.** The switch is not set by default, so a player's trace can
  be attached to a bug report and costs nothing until something acquires it. G5 export presets may set
  `MainframeEngine.Profiler.Disabled` per preset. Whether F12 (and so the overlay's section) is reachable in shipped
  builds is a separate policy, decided with G5.
- **Question 7, no automatic node-level attribution.** `OnProcess`/`OnPhysicsProcess` calls are not wrapped in per-type
  scopes: a scope per node per frame costs more than the frame phases it would explain. The engine marks its frame
  phases, servers and passes; games add markers to their own hot paths (`static readonly ProfilerMarker`, or
  `Profiler.Scope("literal")`).

## Alternatives rejected

- **Record whenever available** (no consumers): every frame would pay for scopes nobody reads, and the history would
  need storage from the first frame.
- **Record every thread that opens a scope:** thread-pool work would fill lanes (64 at most) and allocate a buffer per
  pool thread the first time it ran engine code.
- **Tracy (B/C):** a native build per platform and pinned client/GUI versions (B), or an unverified binding for AOT and
  trimming (C), for features the frame view does not need yet.
- **GPU timing only while recording:** the existing GPU readouts would then read 0 unless the profiler was held, or keep
  their own pools; the measured cost of the pass-level timestamps did not justify either.
- **Per-type node scopes (Godot's script profiler):** a measurable cost on every node, for numbers a marker in the
  game's own code gives more precisely.

## Consequences

- Every frame writes the pass-level timestamps and their debug labels even with nothing recording.
- Game code that runs on its own threads must name them to appear in the profiler.
- An external tool needs a later decision and a sink; the recording API does not change for it.
- Exported games carry the recorder; switching it off is a per-preset G5 option.
