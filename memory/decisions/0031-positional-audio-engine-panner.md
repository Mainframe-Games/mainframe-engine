# ADR 0031 — Positional audio: engine panner in `SpatialSmoother`, not SoundFlow's `SurroundPlayer`

- **Date:** 2026-10-05
- **Status:** accepted (deviation from the M7 proposal, which named `SurroundPlayer` as the panner and asked to verify it)
- **Milestone:** M7

## Context

The proposal (`docs/design/future/audio.md`, now `docs/design/audio.md`) planned each positional voice as a
`SurroundPlayer` with a one-speaker `SurroundConfiguration` at the origin, `ListenerPosition` set from the emitter's
listener-space direction, `RolloffFactor = 0`, engine attenuation through `Volume`, and listed items to verify:
whether panning is interpolated and whether recalculation allocates. Measured on SoundFlow 1.4.1 with exactly that
configuration (scratch harness rendering `SurroundPlayer.Process` directly, stereo output, 0.5 DC input):

| Source direction | VBAP L / R | EqualPower L / R |
|---|---|---|
| −90° (left) | 0.354 / 0.000 | 0.353 / 0.000 |
| 0° (front) | **0.354 / 0.000** | 0.177 / 0.177 |
| +10° | **0.000 / 0.354** | 0.155 / 0.199 |
| +90° (right) | 0.000 / 0.354 | 0.000 / 0.353 |

- **VBAP (the default) hard-switches** between speakers: its pair test requires the barycentric weights to sum to
  ≤ 1, which for unit vectors only holds exactly on a speaker, so it falls back to "nearest speaker" — a centred
  source plays only on the left. It also allocates its factor arrays on every dirty update (≈9 KB the first time).
- **EqualPower/Linear allocate on every audio block** (136 B per voice per callback: factor arrays, speaker layout,
  angles), i.e. continuous GC pressure from the audio thread, and are recomputed per block without interpolation.
- Panning is never interpolated (block-sized steps), and the in-place channel mapping reads source frames from the
  same buffer it writes wider output frames to, which corrupts any source narrower than the output layout.

## Decision

- Keep the proposal's model — emitter projected into listener space (`(x, −z)` plane, front = +y, full 3D
  distance for attenuation, elevation not panned), engine attenuation curves applied as gain, SoundFlow rolloff out
  of the picture — but do the panning in the engine's per-voice `SpatialSmoother` modifier on a plain pooled
  `SoundPlayer`.
- Pan law: `a = (pan + 1)·π/4`, `L = min(1, √2·cos a)`, `R = min(1, √2·sin a)` — centre plays at unity on both
  channels (so a positional sound in front matches a non-positional one), hard pans put unity on one channel, no
  channel is ever boosted.
- Gain, both pan gains and the distance low-pass coefficient are smoothed per sample (one-pole, ~10 ms): no zipper
  noise and no allocation. Positional sources are folded to mono before panning.
- Output is stereo; multichannel layouts are left to miniaudio's upmix until a surround panner is needed.

## Consequences

- Correct, smooth, allocation-free panning today; tested (projection, pan law, end-to-end levels, zipper bound).
- No 5.1/7.1 object panning (the proposal's "stereo through 7.1" goal is deferred). Revisit `SurroundPlayer` if a
  later SoundFlow fixes VBAP and its allocations — the projection code is shared, only the final gain stage changes.
