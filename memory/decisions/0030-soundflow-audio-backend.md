# ADR 0030 — SoundFlow 1.4.1 as the audio backend

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M7 (W3 lane C)

## Context

M7 needs device output on Windows, Linux and macOS (including osx-arm64), a mixing graph for buses, effects and
decoding of common formats, without native build work of our own. SoundFlow (MIT) was agreed for M7 in the plan;
the version was fixed at 1.4.1. Its API and internals were verified from the package (reflection dump of
`SoundFlow.dll`) and the source at the package's commit (`16aa778`), not from memory.

## Decision

- Reference `SoundFlow` **1.4.1**, pinned exactly (`[1.4.1]` in `Directory.Packages.props`). Its `runtimes/`
  ship miniaudio natives for win-x64/x86/arm64, linux-x64/arm/arm64, osx-x64 and osx-arm64 (plus android, ios,
  freebsd); NuGet's RID-specific assets put them in `runtimes/<rid>/native/` of the app and SoundFlow's own
  resolver loads them, so no engine natives work is needed.
- What the engine uses: `MiniAudioEngine` + one default playback device (F32 stereo, 48 kHz, 10 ms period),
  `Mixer` per bus, `SoundPlayer` per pooled voice, `SoundModifier` for the engine's own per-voice and per-bus
  stages and for bus effects (low/high-pass, algorithmic reverb, compressor), `MiniAudioCodecFactory` for MP3/FLAC,
  `SoundMetadataReader` for their headers. All of it stays behind `AudioServer` (internal types), so replacing
  SoundFlow touches `Audio/Graph`, `Audio/Devices` and one decoder.
- Workarounds for 1.4.1 behaviour, all kept inside the audio folder:
  - Every SoundFlow component applies its constant-power pan law at the centre (×√0.5 per channel); the engine
    sets each mixer/player it creates to volume √2 → exact unity gain through any depth of buses (tested).
  - `AssetDataProvider` allocates an event-args object per read and `SoundPlayerBase` ends/disables itself at end of
    stream: voices use the engine's own `VoiceSource` provider instead (reports a live length of 0, never ends,
    resamples, loops, retargets on each play).
  - `ActiveBackend` casts miniaudio's native `ma_backend` value to SoundFlow's enum, which is offset by one (Core
    Audio shows as `WinMm`): the engine names backends by native value.
  - Graph edits take locks inside SoundFlow; the engine builds whole graphs on the game thread and swaps them in
    through its command queue instead of editing a live graph (ADR 0033).

## Consequences

- No native build or CI work for audio; one maintainer upstream is a risk (the design noted it), mitigated by the
  small, contained surface.
- Positional panning does not use `SurroundPlayer` (ADR 0031) and OGG does not use the LGPL FFmpeg codec (ADR 0032).
- SoundFlow's device callback reads its solo slot under an uncontended monitor; the engine never solos through
  SoundFlow, so it never blocks. Our own audio code takes no locks.
