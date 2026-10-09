# ADR 0176 — Load the pipeline cache in the background on MoltenVK, one file per application, with a size cap

- **Date:** 2026-10-09
- **Status:** accepted
- **Milestone:** Forest vertical slice, wave 6 (lane PC: startup stall fix)
- **Spec:** docs/design/gpu-resources.md#pipeline-cache

## Context

On macOS (MoltenVK 1.4.1, Apple M5) the editor and every game stalled for 15–22 s between "Logical device created"
and "PipelineCache: Loaded N bytes" whenever the persisted cache was 4 MB or more (8 ms with no file, ~0 s at 3.4 MB).

Measured in this lane:
- The time is all inside `vkCreatePipelineCache` with initial data (file read: about 1 ms). A `sample` profile of the
  stall has every main-thread sample in `MVKPipelineCache::readData` → `MVKShaderLibrary::compileLibrary` →
  `MVKMetalCompiler::compile`, waiting on the Metal compiler service. MoltenVK's source agrees: reading the cache
  constructs an `MVKShaderLibrary` per entry, and that constructor compiles the cached MSL with `newLibraryWithSource`,
  one at a time.
- Metal's own shader cache hides this when it has seen the source: the Forest's own 1.1 MB file loads in 2–7 ms.
  But that cache is per executable and evicts, so a file another program wrote, or one holding shaders from earlier
  builds, compiles from scratch: 1.1 MB in 2.1 s, 4.2 MB in 10.5–11.3 s (fresh executable identity, sync load).
- The file only grows. MoltenVK keys entries by SPIR-V hash and never drops one; the engine loaded everything, merged
  in the run's new entries and saved the union. Recompiling the engine's shaders three times took the Forest's file
  0.72 → 1.87 → 3.03 → 4.17 MB. One file was also shared by every program (editor, games, Demo) and every worktree.
- What the file buys on MoltenVK is small: it stores translated MSL, not Metal pipeline states. Warm, the Forest's
  70 pipelines take ~190 ms with the file and ~290 ms without (SPIRV-Cross translation).

## Decision

- **MoltenVK loads the file on a background thread.** `PipelineCache` creates the live `Handle` empty and starts a
  `PipelineCacheLoad` thread (below normal priority) that calls `vkCreatePipelineCache` with the data. The next
  `CreateGraphicsPipeline` after it finishes merges it into `Handle` with `vkMergePipelineCaches` (on the
  pipeline-creating thread, which owns the destination's external synchronisation) and destroys the seed cache at once
  (it never backed a pipeline). A load still running at `Save` is left out: the file then holds only this run's
  pipelines, so a file too slow to load prunes itself to the working set. `Dispose` waits for it (the device must
  outlive the call) and discards it. Other drivers keep the synchronous load: they parse lazily and it costs
  milliseconds.
- **One file per application:** `pipelines-<entry assembly>-<vendor>-<device>-<driver>-<uuid>.bin`. The old shared
  file is deleted when found.
- **A size cap bounds the file.** A file over 4 MiB (MoltenVK; about four times the Forest's working set) or 64 MiB
  (other drivers, which store binaries) is not loaded; the run then saves only what it created, which drops the stale
  entries. Each shader edit costs at most one run without the file after the cap is hit.
- **Save only on change:** same length as loaded on MoltenVK (its data is append-only, but merges reorder it), same
  SHA-256 elsewhere.
- The load/merge/save logic runs against an internal `PipelineCache.IDriver`, so it is unit-tested with a fake
  driver (round trip, cap, no rewrite, background load not blocking, dispose waits, rejected data).

## Alternatives considered

- **Skip the file on MoltenVK (b).** Simplest and never stalls, but every start pays the SPIRV-Cross translation
  (~100 ms over the Forest's first frames) and loses the background warm-up of Metal's cache.
- **Cap or prune only (c).** Bounds the stall but does not remove it: the stall is per KB of cache that Metal has not
  compiled, and a cold Metal cache (another executable, an OS update, eviction) makes even a working-set-sized file
  take seconds. MoltenVK has no API to remove entries, so pruning means "do not load", which the cap does.
- **MoltenVK configuration (d).** No `MVK_CONFIG_*` setting makes `readData` lazy or skips the compile.
- **Prewarm-only (load in the background, never merge, save only the run's own cache).** Keeps the file exactly the
  working set, but it relies on Metal's cache being shared between the two compile paths, and in our measurements it
  was not (shaders compiled when their pipelines were created compiled again from scratch when the file was loaded),
  so the loaded data could be wasted work.

## Consequences

- Startup never waits on the cache on MoltenVK: the constructor takes 2–6 ms with a 1.1–4.2 MB file (it was
  10.5–11.3 s with the stale 4.2 MB file). With a warm Metal cache the background load finishes in 13–25 ms, before
  the first pipeline, so warm pipeline creation is unchanged.
- With a cold Metal cache and a large file the background thread compiles for seconds; pipelines created meanwhile
  translate and compile their own shaders, and a very short run waits for the load in `Dispose`.
- `LoadedBytes` now means bytes handed to the driver (it drops to 0 if the driver rejects them later); `IsLoading`
  and `LoadsInBackground` are new.
