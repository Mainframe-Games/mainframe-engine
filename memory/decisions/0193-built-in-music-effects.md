# ADR 0193 — Built-in music effects, in process, staged with VST3 chains

- **Date:** 2026-10-18
- **Status:** accepted
- **Milestone:** —
- **Docs:** docs/design/music-effects.md

## Context

Song inserts could only be VST3 plugins (ADR 0147), hosted out of process by `mfplughost`. A song needed third-party
plugins installed for an EQ or a compressor, played as pass-through on machines without them, and every insert cost a
share of the plugin-host round trip. The music editor is meant to feel like Logic Pro, which ships its own EQ, dynamics,
delays, reverbs and modulation effects. `IEffect.Process` had no tempo or song position, so nothing could sync to the beat.

## Decision

- **Built-in effects in C#, in process** on the song's render thread (`Music/Effects/`): Channel EQ, Filter,
  Compressor, Limiter, Noise Gate, Beat Delay, Reverb, Chorus, Flanger, Phaser, Tremolo/Auto-pan, Saturation,
  Distortion, Bitcrusher, Utility. No new dependency.
- **Same insert list as VST3:** `PluginInsert` gains `builtin` and `params` (additive JSON, no format bump; unknown ids
  and keys kept, an unknown effect plays as pass-through).
- **Descriptor-driven:** `BuiltinParam`/`BuiltinEffectInfo` describe every parameter once; the file format, the plugin
  window, the menus and the tests all read them.
- **Lock-free parameter push:** the snapshot builder keeps one instance per insert and calls `Apply` on every build;
  the render thread picks changes up by version at the next block. Smoothing in the effects; zero allocation.
- **Bypass crossfades inside the effect** and keeps its latency, so delay compensation is constant.
- **`IEffect.Process(stereo, in EffectContext)`** carries sample rate, tempo, transport and song position.
- **Staged chains:** runs of built-ins run in place; runs of VST3 inserts at the same depth share one round trip.

## Alternatives considered

- **Ship built-ins as VST3 plugins** of our own through `mfplughost`: native code per platform, a round trip per block,
  and no tempo-synced state we control.
- **Reuse SoundFlow's modifiers** (the engine's bus effects): they allocate on the audio thread (the reason the engine
  wrote its own reverb), have no tempo, and the music editor keeps SoundFlow out of its engine.
- **Run built-ins after every VST3 insert** (no interleaving): simpler, but the chain order the user sees would lie.
- **Drop bypassed built-ins from the chain** (as VST3): clicks when toggled, and the delay compensation changes.

## Consequences

- Songs with only built-in effects play and render identically everywhere, with no plugin host.
- A built-in placed between VST3 inserts adds a plugin-host round trip per block (documented).
- Parameter ids and choice orders are a file contract.
- The plugin window (`PluginWindow`) and, later, the Browser list effects from `BuiltinEffects`.
- Runtime playback in games is unchanged (rendered audio); the effects are editor-only for now, but pure C# if a
  runtime player (adaptive music, ADR 0196) ever wants them.
