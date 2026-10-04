# ADR 0034 — Null audio device and never failing startup

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M7

## Context

CI runners and servers have no audio device; a native library may be missing on an unusual platform; audio must
never stop a game (or the render tests) from starting. Unit tests need deterministic, sample-exact output.

## Decision

- `AudioServer.Create` never throws for device or layout problems: it tries the default playback device through
  SoundFlow and, on no device / `DllNotFoundException` / init failure, logs one warning and uses the **null device**.
  `Engine` additionally wraps creation so an unexpected exception only disables audio.
- The null device is the same SoundFlow graph driven without miniaudio: SoundFlow components need an `AudioEngine`
  instance, so `NullAudioEngine` subclasses it with no devices. Modes: `Realtime` (a background thread renders
  10 ms blocks at wall-clock pace and discards them — positions advance, `Finished` fires, streams decode) and
  `Manual` (`AudioServer.RenderNullDevice(frames, capture)` renders on the caller's thread and can capture the mix).
- `AudioDeviceMode`: `Auto` (default), `Null` (render tests: silent, deterministic environment), `NullManual`
  (unit tests and benchmarks).

## Consequences

- Unit tests measure real mixer output (levels, panning, ramps, resampling, loops) on every OS without audio
  hardware or natives (WAV/OGG paths are managed; MP3/FLAC tests load SoundFlow's miniaudio natives, which the
  package ships for all CI runners).
- The same code path runs with and without a device, so CI exercises the production mixer.
