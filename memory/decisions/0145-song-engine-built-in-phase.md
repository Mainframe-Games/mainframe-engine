# ADR 0145 — Music editor phase 1a: song model, C# song engine, WAV render, AudioStreamGenerator

- **Date:** 2026-10-08
- **Status:** accepted (proposal [music-editor.md](../../docs/design/future/music-editor.md), delivery 1 without the UI)

## Context

The music editor proposal keeps everything musical in C# (song, sequencing, mixing, rendering) and leaves plugins,
MIDI input and Vorbis encoding to a native helper in later phases. The first phase must be useful and fully testable
with no helper: the built-in ZzFX instrument, audio clips, live playback and a render the game can loop.

## Decision

1. **`AudioStreamGenerator` (engine, Godot's API).** Each play gets its own `AudioStreamGeneratorPlayback` ring: the
   stream's cached source is a template that `AudioServer.Play` swaps for a per-play `AudioGeneratorSource`, so one
   stream can play on several voices and `GetGeneratorPlayback(handle)` maps a voice to its ring. The ring is its own
   SPSC float ring (not `SpscRing<float>`) so `ClearBuffer()` can work from the producer side (a "discard up to" index
   the consumer honours). The voice reads it with the stream path's linear interpolation; underruns play silence.
2. **`.msong` is self-describing** like scenes: the `sng_` UID lives in the file (`AssetDatabase.IsSelfDescribing`,
   `AssetUid.SongPrefix`), no `.meta`. The reference fixer scans songs (render output paths) and writes them in the
   song writer's style (project-file layout). Clips have no type discriminator: objects with `file` are audio clips.
   Unknown fields (plugin descriptors, inserts, any object) round-trip through `[JsonExtensionData]`; plugin state is
   an opaque base64 string.
3. **Engine shape:** UI thread builds immutable `SongSnapshot`s (frames, sorted notes, decoded/resampled clips, ZzFX
   pitch banks generated off the render thread) through a `SongSnapshotBuilder` that keeps stable per-track slots and
   instrument instances; the render thread swaps them at block boundaries. Per-track runtime state is preallocated
   (64 slots), transport/preview commands use a lock-free queue, so steady-state blocks allocate nothing (tested).
   `IInstrument`/`IEffect` carry `LatencyFrames` for the plugin phase; PDC is not applied yet (all latencies are 0).
4. **ZzFX instrument semantics:** a note plays the line at the note's frequency (the line's own frequency is
   replaced), velocity / 127 as gain; a note-off releases the voice over 30 ms (sounds shorter than the note end by
   themselves), stop/seek fade in 5 ms. 16 voices per track, oldest stolen.
5. **Render writes WAV until the encoder exists:** 32-bit float to a temp name, then moved over the output; any other
   requested extension is written as `.wav` with a warning. Seamless loops render the loop twice and keep the second
   pass (meta `loopStart`/`loopEnd` 0); a non-seamless loop renders from 0 to the loop end (intro + loop) with the loop
   region in seconds; without a loop, 0 to the last clip end + tail. `AssetDatabase.WriteMeta` (new) writes the `.meta`.

## Consequences

- Games get Godot's procedural-audio path; the editor's song tab (next) only needs the `SongDocument`/`SongPlayer`/
  `SongRenderer` API.
- `AudioStream.DecodedSamples` exposes memory-loaded samples to tools (the editor does not see engine internals).
- A song's render hash is pinned on quantised samples (1/4096) so libm last-bit differences across OSes cannot break it.
