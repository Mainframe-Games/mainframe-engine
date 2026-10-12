# ADR 0190 — Gameplay recording via an ffmpeg child process

- **Date:** 2026-10-12
- **Status:** accepted
- **Milestone:** —
- **Docs:** docs/design/recording.md

## Context

Promo footage for games built on the engine needs clean, frame-accurate captures (live play and
deterministic offline renders) that a trimming tool can cut using what the engine knew: input,
loading, pause, markers. The engine is MIT and ships Spine and Steam redistributables, so it cannot
take on GPL code, and games must not inherit any encoder obligations.

## Decision

- **ffmpeg as a separate program, found on the user's machine.** Raw frames go to its stdin; audio
  goes to a float WAV; a second run muxes. The engine never links, bundles or P/Invokes an encoder.
  Rejected: linking libav* (LGPL obligations in every game, native build matrix); bundling ffmpeg
  (licence depends on the build; x264 builds are GPL); a named pipe for audio (per-OS plumbing for no gain).
- **Hardware H.264 first, probed before use**, then libx264/libopenh264, as a high-bitrate
  intermediate with a one-second GOP.
- **Frames from a per-frame-slot readback buffer read after BeginFrame's existing fence wait**, so
  recording adds a copy, never a stall. There are two buffers (one per frame in flight), not the three the
  first design assumed.
- **A `.mfcap.json` sidecar** (format `mainframe-capture`, version 1) that the engine owns and
  Cutback reads; a shared fixture pins it in both repositories.
- **The audio tap sits after the mixer's fault guard and takes whole blocks**, so the recording is exactly what
  the device plays and a full ring can be repaid with silence of the same length.
- **The WAV is written by a streaming writer and fixed to the video's exact sample count at Stop**, in both modes.

## Consequences

- Recording requires ffmpeg; without it `--record` fails with an install hint.
- Shipped games contain no encoder; `--record` is inert unless used.
- A future in-editor Clips tab reuses the same sidecar and the same process boundary.
- An offline `--record` run whose recording fails exits non-zero; a live run logs the error and keeps playing.
- A window resize ends a recording, and `--record-scale` needs a display large enough for the scaled window
  (see the known issues in docs/design/recording.md).
