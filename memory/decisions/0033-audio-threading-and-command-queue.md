# ADR 0033 — Audio threading: batched lock-free command ring, audio-thread drain, streaming thread

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M7

## Context

The proposal had nodes enqueue commands and `AudioServer.Flush()` apply them to SoundFlow once per frame, noting that
SoundFlow graph edits take a lock. Requirements for M7: no locks on the audio callback path, zero allocation per
command and per frame, decoding/file I/O off both the game and the audio thread.

## Decision

- **Game thread** never edits a live SoundFlow graph. Every change becomes an `AudioCommand` (value type: voice/bus
  index, generation, mix parameters, loop points, one object reference for the sound or a new graph) appended to a
  pending array. `AudioServer.Process` (frame server, after transform sync) recomputes playing voices and
  `Flush()` publishes the whole batch into a **single-producer/single-consumer ring** (`SpscRing<T>`, power-of-two
  array, padded 64-bit head/tail, acquire/release, one tail store per batch). Parameter updates are coalesced per
  voice per batch and only sent on change; a full ring leaves the remainder pending, in order (never dropped).
- **Audio thread**: `AudioMixRoot` (a SoundFlow component in the device's master mixer, or pumped by the null
  device) drains the ring at the start of every block, applies commands to engine-owned state (voice sources,
  `SpatialSmoother`, `BusProcessor` targets, SoundFlow `Play`/`Pause` flags), mixes, then posts `VoiceFinished` /
  `GraphRetired` to a second SPSC ring (retried each block until there is room). Commands carry the voice's
  generation; stale ones are ignored.
- **Voice ownership** stays on the game thread (allocation, polyphony, stealing by priority → gain → age), so it
  can decide synchronously and nodes see consistent state; the audio thread only executes.
- **Layout changes** build a complete new graph on the game thread and swap it in with one command; the old graph is
  disposed when the audio thread reports it retired.
- **Streaming thread** (`AudioStreamer`, started on first use): per voice an `AudioStreamChannel` with a seqlocked
  request and an SPSC float ring (32 768 frames); generations let a voice skip the previous sound's leftovers; the
  producer loops by seeking its decoder and closes files on stop requests.
- The audio callback is guarded: an exception becomes silence plus a fault flag the game thread logs once.

## Consequences

- No locks or allocations in engine code on the audio path; verified by a unit test that renders on the measuring
  thread and by the render-test allocation gate (game thread).
- Commands issued during a frame reach the audio thread at that frame's end (plus one device period): the proposal's
  "batched once per frame" latency. Tools without a tree call `Flush()` themselves.
- Positions reported to the game thread (`GetPlaybackPosition`) are as of the last audio block.
