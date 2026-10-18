# ADR 0196 — Adaptive music: baked stems, one director, audio-thread scheduling

- **Date:** 2026-10-18
- **Status:** proposed
- **Milestone:** —
- **Docs:** docs/design/future/adaptive-music.md

## Context

The music editor (ADRs 0145–0148, 0192) renders a song to one `.ogg`, which a game plays and loops on an
`AudioPlayer`. Games want FMOD-style adaptive music: sections, layers that follow parameters, and beat-quantized
transitions and stingers. VST3 plugins exist only on the developer's machine, and games never host them (the music
editor's non-goal). Running the song engine itself in games would need every instrument and effect at run time, and
plugins could never come along.

## Decision

- **Games play baked stems.** The editor bakes an adaptive song into one stem per layer per section, plus a tail stem
  (reverb and delay after the section end), and an `.mmusic` manifest. The manifest is a new extension with a new
  `mus` UID prefix, which stays stable across bakes. Stems are content-addressed `.ogg` files in `<Song>.stems/`,
  rendered again only when their input hash changes. A bake commits the manifest last, so it is atomic.
- **Stems are rendered in isolation, and tails are one-shots.** Each (section, layer) is rendered from a clean start,
  with notes cut at the section end and the tail rendered on past it. At run time one rule covers loop seams, `then`
  continuations and section-end transitions: the next pass or section starts at the seam, and the tails play over it.
  Mid-section exits fade the outgoing stems instead of using a tail.
- **Master inserts are not baked** in adaptive songs, because the sum they act on no longer exists. Glue processing
  belongs on the game's Music bus.
- **One `MusicDirector` in the engine** (`MainframeEngine/Src/Audio/Music/`, pure C#) owns parameters and seek,
  curves, rule selection and quantization, with one `MusicGrid` rounding of beats to frames. The editor's live
  `SongEngine` preview runs it with no lookahead, and games run it on the game thread with a lookahead, so both put a
  transition on the same bar.
- **The game thread decides and the audio thread executes.** The director schedules actions at exact frames of a
  music clock (frames at the manifest's rate, stopped while paused). A pre-created `MusicVoice` per bus
  (`AudioBusInfo.MusicVoices`) carries them out sample-accurately: lanes that share one read position, linear fades,
  per-lane layer gain and an SVF filter. It reports what happened through the event ring. A streamed lane that is not
  buffered in time makes the whole transition miss, and it is rescheduled at the next quantum, never half-applied.
  Authored continuations run on the audio thread by themselves.
- **Curves drive only what runs on a baked stem**: layer volume, and a layer low-pass or high-pass cutoff. The filter
  moves from the editor's Phase 1 DSP into `MainframeEngine/Src/Audio/Dsp/` and both sides use that copy.
- **The data is additive.** It is an `adaptive` block in `.msong` plus a `layer` field on tracks, with no format bump;
  an older editor keeps them through `Extra`.

## Alternatives considered

- **Live song playback in games.** It sounds the most like the editor, but it needs the whole song engine and every
  instrument and effect at run time, cannot include VST3 at all, and costs far more CPU on low-end and mobile devices.
- **Stems on ordinary pooled voices, scheduled against the device clock** (Web Audio's `start(when)`). This reuses
  the voices, but pause, stealing and pool size would all break a music group's alignment, and every schedule would
  have to be re-anchored after a pause. A dedicated voice with its own music clock keeps the group atomic.
- **Decisions on the audio thread.** It would give the tightest timing, but it puts rule logic, parameter smoothing
  and string-keyed data on the real-time thread, and the editor preview could no longer share the code as plainly.
  Scheduling ahead costs at most one quantum, and only when the next one falls inside the ~100–150 ms lookahead.
- **One multichannel file per section.** It needs one decoder per section and aligns the layers by construction, but
  the engine downmixes sources wider than stereo, and silent layers would still cost channels. This is left as an open
  question.
- **The render-twice seamless fold** that the music editor uses for loops. It is wrong the first time a section is
  entered from another section, and it does not cover transitions; isolated renders with tail one-shots handle both.

## Consequences

- An adaptive song costs disk space and bake time in proportion to sections × layers. Incremental bakes keep
  iteration cheap, and parameter, curve and rule edits re-render nothing.
- Live preview and the bake differ by design at mid-section exits (live releases notes into real effect tails; baked
  fades). The editor offers a Baked audition through the real runtime.
- The editor link goes to version 5: music watch and parameter commands, and a `Music` state message.
- Built-in effects other than the layer filter cannot follow game parameters until they run in the engine. That is an
  open question in the design.
