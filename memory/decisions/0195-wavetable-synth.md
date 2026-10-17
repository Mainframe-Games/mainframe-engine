# ADR 0195 — A built-in wavetable synth for Music mode

- **Date:** 2026-10-18
- **Status:** proposed (draft; decide with the design doc's open questions)
- **Milestone:** —
- **Docs:** docs/design/future/wavetable-synth.md

## Context

Music mode's only built-in instrument is ZzFX, a sample player of one sound-effect line rendered per pitch. Every
richer sound needs a VST3 instrument through the `mfplughost` helper, so a song made on one machine plays silence on
another and in CI, and a new user has no real instrument. The Logic-style DAW plan (Phase 2) calls for a modern
wavetable synth. Phase 1 adds the pieces it can stand on: `BuiltinParam` descriptors, the lock-free `BuiltinEffect`
parameter hand-off, `Effects/Dsp/` (SVF, biquad, LFO, note divisions, smoothers, oversampler), `EffectContext`, the
built-in effects and a descriptor-driven plugin window (ADR 0193).

## Decision

- **An in-process C# instrument**, `WavetableSynth : IInstrument` in `MainframeEngine.Editor/Src/Music/Synth/`,
  selected as `InstrumentDescriptor.Builtin = "wavetable"`. Working display name "Mainframe Wavetable"; our own
  design, no third-party name, wavetables, presets or artwork.
- **The patch is a new additive `patch` object** on the instrument descriptor (`params` floats by id, `tables`,
  `lfoShapes`, `macros`, `matrix`, `fx`), not `Params`, which is ZzFX's string. No song format bump: older editors keep
  it in `Extra` and play the track silent.
- **v1 engine:** 2 wavetable oscillators (≤ 256 × 2048-sample frames) with one warp each (sync, bend, PWM, mirror,
  FM/AM/RM), unison 1–16, sub, noise, 2 filters (SVF, ladder, comb, formant; serial/parallel), 4 envelopes, 4 LFOs
  (tempo sync, drawn shapes), a 32-route mod matrix, polyphony ≤ 32 with mono/legato/glide, and an FX chain of
  Phase 1 built-in effects.
- **Anti-aliasing by octave mip-maps** built by FFT, 11 levels with shrinking lengths (≈ 3.1 frames of memory), the
  level chosen so harmonics stay under `max(fs − 20 kHz, fs/2)`: nothing audible aliases from the table itself; FM
  and sync get optional 2× oversampling.
- **Control rate of 16 frames** for envelopes, LFOs and the matrix, with per-sample linear ramps on every audio-rate
  consumer; modulation sums in each target's normalized range.
- **Threading:** parameter values go through Phase 1's volatile-write + version hand-off; structure (routes, tables,
  LFO shapes, FX) as an immutable `SynthProgram` swapped at block boundaries; wavetables built by a worker-side cache.
  Nothing allocates or locks on the render thread; integer phases and seeded per-voice RNGs make offline renders
  deterministic.
- **Content:** factory wavetables and factory presets generated in code (nothing binary in the repository); `.wav`
  wavetable import reading the `clm ` chunk's frame size; user presets as `.mres` `WavetablePreset` resources, a type
  in the engine core so the generator registers it (the editor has no generator).
- **UI:** the Phase 1 plugin window at a larger size with Osc / Matrix / FX tabs, a modulation strip of draggable
  sources, drag-to-assign onto knobs with coloured depth arcs, and `EditorCanvas` views for the wavetable, filters,
  envelopes and LFOs.
- **Editor-only in v1;** games keep playing rendered `.ogg` files.

## Alternatives considered

- **Bundle a third-party synth through the VST3 host:** still machine-dependent and absent in CI; licences of
  capable open-source synths (GPL) do not fit the MIT engine.
- **A native DSP library** (C++ through a shim like `mfrmlui`): faster per voice, but a second native build for the
  helper-free path, harder to test, and the Phase 1 DSP is already C#. The CPU targets are reachable in C# with
  integer phases, control-rate modulation and SIMD over unison lanes.
- **Store the patch in `Params` as a string** (as ZzFX does): unreadable diffs, and no tolerant per-id reading.
- **Per-sample modulation everywhere:** several times the cost for no audible gain at a 3 kHz control rate with ramps.
- **Full-length mip levels** (2048 samples at every level): about 23 MB per 256-frame table instead of 6.5 MB.
- **Presets as plain JSON files:** simpler, but the user chose `.mres`, which also lets a future runtime synth load
  them as resources.

## Consequences

- `IInstrument.Process` gains Phase 1's `EffectContext`; the ZzFX and VST3 instruments ignore it.
- The engine core gains one resource type (`WavetablePreset`) used only by the editor until a runtime synth exists.
- Songs and CI renders get a real instrument without plugins; render tests and the allocation gate cover it.
- The CPU worst case (16 voices × 16 unison × 2 oscillators) is budgeted at ≤ 40 % of a core; larger stacks may
  miss deadlines, shown by a per-instrument CPU meter.
- Open: the product name, the controller path (mod wheel, pitch bend, aftertouch), matrix routes into FX parameters,
  a user-wide preset library, runtime use in games.
