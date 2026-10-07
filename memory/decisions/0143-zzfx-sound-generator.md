# ADR 0143 — ZzFX as the engine's sound generator

- **Date:** 2026-10-08
- **Status:** accepted (proposal [sound-designer.md](../../docs/design/future/sound-designer.md), phase 1: engine)

## Context

Games need small sound effects (coins, lasers, jumps, UI blips) long before anyone records or buys audio, and
hand-written sample loops (the Demo's click blip) do not scale. ZzFX (Frank Force, MIT) is a tiny, well-known synth
whose web designer copies a sound as one line of 21 numbers (`zzfx(...[,,925,.04,.3,.6,1,.3,,6.27,-184,.09,.17])`), so
a port gives the engine a generator and a large existing vocabulary of sounds.

## Decision

1. **Port ZzFX 1.4.0's `buildSamples` line for line** (`Src/Audio/Synthesis/Zzfx.cs`, MIT notice in the header,
   `THIRD_PARTY_NOTICES.md`): maths in `double`, 44.1 kHz mono, ZzFX's master volume 0.3 baked in, no clamping, sounds
   capped at 10 s. Float parameters widen through their shortest decimal form so results match the browser.
   Reference vectors from the pinned JavaScript (`build/zzfx-reference.mjs`) keep the port honest (within 1e-5).
2. **Randomness is per-play pitch, not baked in.** As `ZZFXSound` does, samples are generated with randomness 0 and
   every play varies the playback rate. This becomes `AudioStream.PitchRandomness` on every stream (default 0; 0.05
   for ZZFX sounds), applied by `AudioServer` at each voice start from a server-owned xorshift (no allocation).
3. **`ZzfxStream` is an `AudioStream` subclass** with one `[Export]` per parameter (ZzFX defaults, so `.mres` files
   store only what differs). `AudioStream.GetSource()` gains a `private protected virtual CreateSource()` hook (the file
   branch is the base implementation; `AudioSource` is internal, so only engine types can be generators) and
   `Invalidate()` becomes `private protected`. Every player and `PlayOneShot` accepts a ZZFX sound unchanged.
4. `ZzfxParameters` owns the line format (parse/format) and `ZzfxPresets` the random recipes, so the editor's
   designer (later phases) is a UI over engine code. `WavWriter` exports 16-bit PCM / 32-bit float WAV.

## Consequences

- No scene or resource format change; existing `.mscene`/`.mres` files load as before (`PitchRandomness` defaults to 0).
- ZzFX's `release` parameter is `ZzfxStream.ReleaseTime` because `Resource.Release()` already exists; the line format
  and `ZzfxParameters.Release` keep ZzFX's name.
- Changing a `ZzfxStream` parameter re-synthesises (and allocates) on the next play; it is not for per-frame changes.
- Node is needed only to regenerate the reference vectors, never for `just test`.
