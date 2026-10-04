# ADR 0032 — OGG Vorbis via NVorbis

- **Date:** 2026-10-05
- **Status:** accepted (resolves the proposal's open "OGG Vorbis" decision; default from the M0–M10 plan)
- **Milestone:** M7

## Context

SoundFlow's miniaudio codecs decode WAV, MP3 and FLAC but not OGG. The options were `SoundFlow.Codecs.FFMpeg`
(LGPL v2.1+, large FFmpeg natives per platform, also Opus/AAC) or NVorbis (MIT, fully managed, Vorbis only).
NVorbis was approved as a dependency in the plan.

## Decision

- Reference `NVorbis` **0.10.5** (latest stable 0.10.x), pinned exactly. On net10.0 its netstandard2.0
  dependencies (`System.Memory`, `System.ValueTuple`) are in-box.
- `VorbisDecoder` wraps `VorbisReader` behind the engine's `AudioDecoder` (memory decode and streaming alike; first
  logical stream; `ClipSamples = false`). Decoded output was verified bit-identical to ffmpeg's decoder on the test
  file.
- **Seeking:** NVorbis 0.10.5's `SeekTo` is not sample-accurate — measured landing 576–1600 samples past the target
  depending on the target and the file's block sizes — and `SeekTo(0)` on a fresh reader throws (and one probe run
  hung). The engine never calls it: a backward seek reopens the reader on the rewound file stream, a forward seek
  decodes and discards up to the target. Loops back to the start cost a header parse.
- WAV is decoded by the engine's own managed `WavDecoder` (works with no natives, so unit tests on any CI runner);
  MP3/FLAC use SoundFlow's miniaudio codecs.

## Consequences

- No LGPL obligations and no extra natives; OGG works identically on every platform.
- No Opus/AAC; a loop point or seek deep into a long OGG costs decoding up to it on the streaming thread.
- Lossy test assets are checked with tolerant frequency/level assertions; WAV and FLAC exactly.
