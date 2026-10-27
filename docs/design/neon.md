# Neon example

The Neon example game (`Examples/Neon`). This page so far only holds the audio phase's notes; the rest of the design
is still to be written. For how to run it, see [the example's README](../../Examples/Neon/README.md#audio).

## Audio (phase N5)

All of Neon's audio is synthesised at load (`Neon/Src/Audio/`), as in the [Forest](forest.md#audio). The README
lists the sounds and the API. These are the decisions behind it.

- **Indoor and outdoor are two loops, not a moving filter.** `AudioEffect` parameters only apply when a bus layout
  is applied ([Audio, Known issues](audio.md#known-issues)), so a bus low-pass cannot follow the player. Each bed
  therefore has an outdoor loop and a pre-muffled indoor loop. They are crossfaded at equal power by *openness*, which
  is 0 inside the apartment's box and 1 on the balcony, eased over 0.35 s. The two loops are uncorrelated, so equal
  power keeps the loudness steady through the crossfade.
- **Outdoor emitters are muffled with the voice's distance low-pass.** This applies to city events, fly-bys, and the
  train and airships once the city registers them with `RegisterOutdoor` (or creates them with `CreateLoop`).
  - The distance low-pass is the only filter that moves live, so `NeonAudio` sets each emitter's `MaxDistance` just
    past its current distance. It then aims `LowPassAtMaxDistance` so that the engine's linear interpolation lands on
    the target cutoff: about 800 Hz inside, 9 kHz outside.
  - The margin is 4 % of the distance past the unit size, plus twice the emitter's last frame of travel. It keeps the
    emitter inside its max distance, where it is never silenced.
  - The margin also sets the lowest cutoff reachable this way, about 800 Hz.
  - A proper per-voice or per-bus live filter in the engine would remove this workaround.
- **The music uses adaptive music, with stems rendered at run time.**
  - Each song is a `MusicManifest` built in code (`AdaptiveMusic.FromManifest`). It has one 8-bar looping section,
    four layers with a low-pass each, and a `muffle` parameter whose curves drive the cutoffs and volumes.
  - The runtime streams stems from files, so the synth writes them once to `$TMPDIR/neon-music`, named by song, seed
    and `NeonMusic.StemVersion`. Bump `StemVersion` when a recipe changes. No audio is committed.
  - Tempos give a whole number of frames per beat at 48 kHz: 100 and 90 BPM.
  - Each pass's release tails and echoes are added onto the start of the loop (`SignalTools.WrapTail`), so the loop
    point is seamless.
- **The stereo is placed by hand.** Adaptive music is not positional, so `NeonMusicPlayer` sets the level from the
  distance to the `Stereo` socket (inverse, 3 m unit size, at most −24 dB). It sets `muffle` from the listener's room:
  - none in the open-plan rooms;
  - 0.25 on the loft;
  - 0.5 to 0.9 behind a door, depending on whether it is open.
- **Hooks are events on the interactables:**
  - `DoorBase.Toggled(bool open)` when a door starts to move, and `DoorBase.Settled(bool open)` when it comes to rest.
    The hinged door's thud plays on `Settled(false)`.
  - `LightSwitch.Switched(bool on)` fires only when the player flips the switch, so lights changed from code stay
    silent.
  - `FrontDoor.Rattled` already existed.
- **0 B per frame** is gated by `NeonAudioTests.FramesAllocateNothing` with the apartment, footsteps, doors, switches,
  city events, fly-bys and the stereo all running.
