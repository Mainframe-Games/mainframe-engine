# ADR 0162 — Procedural forest audio

- **Date:** 2026-10-08
- **Status:** accepted
- **Milestone:** Gameplay toolkit G8d (G8d.13 audio, subset), forest slice wave 3 lane K
- **Spec:** docs/design/future/forest-showcase.md (Audio), docs/design/future/water.md (Audio); current state in
  docs/design/forest.md#audio

## Context

The proposal plans the Forest's audio from CC0 recordings (Freesound) through `AudioPlayer`/`AudioPlayer3D`, buses and
the engine's Freeverb. Brogan chose CC0 + procedural, and this lane was asked for **no downloaded audio**: an ambient bed
driven by the wind, a stream that follows the `River3D` with falls, birds in the canopy and footsteps per surface, all
at 0 B per frame and checked numerically (nobody in the loop can listen). The engine already has what playback needs
(M7: pooled voices, spatial attenuation and panning, buses with effects, `AudioStream.FromSamples`, the ZzFX port,
`AudioStreamGenerator`, the manual null device). `River3D` has no falls and does not play audio itself yet.

## Decision

- **Pre-synthesised buffers, not generators.** Every sound is synthesised once (seeded noise, biquads, damped sines, ZzFX)
  into an in-memory `AudioStream` (`ForestSoundBank`, built per group on first use, ≈ 17 MB, ≈ 0.5 s Debug). Loops are
  folded seamless by an equal-power crossfade of a 1 s tail; their gust envelopes are sums of sines at whole multiples
  of the loop frequency, so they repeat with the loop. Run-time variety comes from cheap parameters: two bed layers of
  different lengths (24 s and 17 s, repeating together every 408 s), the wind strength on level and pitch, the voices'
  pitch randomness, random variants, positions and levels. An `AudioStreamGenerator` would need a producer thread or
  per-frame pushes for the same result; buffers cost no CPU after load and nothing per frame.
- **Game code, not engine code.** Everything lives in `Examples/Forest/Forest/Src/Audio` (`ForestSynth`, `BirdSynth`,
  `ForestSoundBank`, `AmbienceAudio`, `StreamAudio`, `BirdSongs`, `FootstepAudio`, `ForestAudio`, `ForestRandom`,
  `Biquad`, `OnePole`, `SignalTools`). No engine file changes. `StreamAudio` implements water.md's River3D audio
  behaviour (nearest point, half-width push toward the listener, ≤ 30 m/s, flow-scaled level) outside `River3D`, so the
  engine can absorb it later without the Forest changing behaviour; falls are positions (or
  `StreamAudio.FindSteepPoints`) until `River3D` has falls.
- **Birds are ZzFX.** Five made-up species from ZzFX notes (sine whistles with slides and pitch jumps, ZzFX's repeat and
  tremolo for trills, `tan` knocks for the woodpecker), four songs and two calls each, mixed per song at their start
  times. A pool of six `AudioPlayer3D`s, Poisson timing (mean 4 s), 10–40 m away in the canopy, no species or variant
  twice in a row; an optional pine-density function quietens songbirds and favours the woodpecker.
- **Footsteps are physical-ish recipes.** A heel and a softer toe impact per step, each from the surface's ingredients
  (low thump, filtered noise, grains, resonant modes, band-pass sweeps, bubbles), six variants per surface; unknown
  names map to the nearest set (`leaf_litter` → leaves, `path` → dirt, …) or `default`.
- **Buses and mix.** The Forest's layout gains Ambience (−7 dB), Water (−8 dB) and Foley (−7 dB, light reverb). Levels
  are set by RMS (loops) and peak (one-shots) in synthesis and checked through the engine's offline path: the whole mix
  walking by the stream is ≈ −26 dBFS RMS with peaks ≈ −9 dBFS. `ForestSettings.Audio` holds linear 0–1 bus volumes
  over the layout's levels.
- **Wiring.** `ForestAudio.Attach(root, river, player, fallPositions)` or a `ForestAudio` node in the scene (finds the
  first river and player). It is not added to the test scene: the content lane owns the scene.
- **Testing without ears.** Unit tests pin determinism, loop seams, RMS/peak, frequency bands (energy through
  4th-order low/high-passes), bird register (zero crossings), the emitter maths, the bird schedule and 0 B per frame;
  `ForestAudioRenderTests` render each part through `AudioServer` on the manual null device with the Forest's bus
  layout into `Examples/Forest/artifacts/audio/*.wav` (`just forest-audio`) and check RMS and peak windows.

## Consequences

- No third-party audio: nothing to license or list in `NOTICE.md`, nothing in LFS.
- Procedural sounds are more stylised than recordings. The recipes are tuned by numbers only; Brogan's listening notes
  are the real test, and any part can later swap its stream for a CC0 recording without changing the nodes.
- A scene load synthesises the bank once per process (≈ 0.5 s Debug); a loading screen can call
  `ForestSoundBank.Shared.Preload()`.
- Not built: the proposal's zone crossfade of two beds, a wading loop, `River3D`'s own audio, falls from `River3D` data.
