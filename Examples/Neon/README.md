# Neon Demo

A first-person walk round a two-bedroom industrial loft about 20 floors up in a rainy neon city: a double-height
living room, a mezzanine with the master bedroom, steel stairs, a bathroom, a computer room and a balcony. You can open
doors and flip the lights, but you can never leave. It is a game project of its own, built against this checkout's
engine like [the Forest](../Forest/README.md), and **not** part of `MainframeEngine.slnx`.

**Status: phase N1, a graybox.** The apartment is a flat-coloured model built by a Blender script; you can walk
through every room, up the stairs to the loft and bedroom 2, and out onto the balcony. Doors (E) open and shut, the
balcony's glass door slides, the front door stays locked, and each room's switch turns its lights and fixtures off
and on. The synthesised [audio](#audio) is in (phase N5): rain and the city that change when you step outside,
footsteps, doors, switches, the fridge and the PC fans, and the stereo's music. The city, visible rain, traffic, the
working computer, the shower and the art come in later phases.

## Run

```bash
just neon                                   # Release; Esc releases the mouse, a click captures it again
just neon ++ --view -2.2,3,4.6,-55,-18      # stand somewhere: feet x,y,z, yaw (0 = towards the front door), pitch
just neon ++ --open-doors                   # every door open (and --lights-off <Room>: Living, Bath, Computer, ...)
just neon-test                              # Neon.Tests: interactables, a walk through every room, containment, audio (no GPU)
just neon-audio                             # render the soundscape to artifacts/audio/*.wav to listen to, and check its levels
just neon-art                               # rebuild the apartment's glTF in Blender (Tools/apartment/build_apartment.py)
```

Run it with `dotnet run --project Examples/Neon/Neon.Desktop -c Release`. Game flags go after `++`: `--no-capture`
(do not capture the mouse), `--view`, `--open-doors`, `--lights-off <Room>`.

## Controls

WASD / left stick to walk, mouse / right stick to look, Shift to sprint, C or Ctrl to crouch, Space to jump, E / gamepad
X to use what you look at (a door, a light switch) within 2.2 m.

## Layout

- `Neon/Src/Player/` — `FirstPersonController` (the Forest's, without wading) and `IFootstepSurface`.
- `Neon/Src/Interaction/` — `IInteractable`; the `Interactor` (one ray a frame from the camera, 2.2 m, on the world and
  interact layers) and its `InteractPrompt`; `Door`, `SlidingDoor`, `FrontDoor` and `LightSwitch`.
- `Neon/Src/Audio/` — the synthesised soundscape (see [Audio](#audio)).
- `Neon/Src/World/` — `NeonScene` (the scene builder: `--write-scenes`), `NeonApartment` (loads the model and turns its
  named nodes into doors, lights, switches, colliders and sockets), `FallSafety` (respawns a player who falls out).
- `Tools/apartment/build_apartment.py` — the apartment, headless in Blender (seeded; `--preview DIR`, `--verify DIR`).
  Its docstring lists the naming conventions the game reads.
- `Assets/Scenes/neon.mscene` — written by `dotnet run --project Examples/Neon/Neon.Desktop -- --write-scenes
  Examples/Neon/Assets/Scenes`; a test checks it matches the builder.

## Audio

Every sound is synthesised in C# at load, as in the Forest: there are no recorded files. Game code only uses the
`AudioServer`, the audio nodes and `AudioStream.FromSamples`.

- **`NeonSynth`** (`NeonSynth.cs`, `.OneShots.cs`, `.Primitives.cs`) — seeded, deterministic recipes from noise,
  filters and band-limited oscillators. Loops are seamless: noise is crossfaded across the seam and tones run a whole
  number of cycles per loop. Loops are levelled by RMS and one-shots by peak.
  - The rain outside: a hiss, a body, a splash and drop grains, with tinks on the railing.
  - The rain inside: a low-passed roar, glassy patter on the panes, drips and a trickle.
  - The city's drone: a rumble, a 55 Hz hum, a traffic wash and distant cars. The indoor version is the same through
    two 320 Hz low-passes.
  - Distant sirens (wail, yelp, hi-lo), horns and a PA chime with street echoes, and a flying car's pass (mono, for
    Doppler).
  - The elevated train's rumble and clickety-clack, and an airship's throbbing engines.
  - The shower, a tap, the fridge, PC fans with a rack's whine, keyboard clicks.
  - Doors (latch and creak, thud and latch), the balcony's slider, the light switch, and the front door's buzz with a
    "denied" two-tone.
  - The TV's static and channel blip.
  - Footsteps on concrete, metal (the grate and the stairs), tile, wood and rug, six variations each.
- **`NeonSoundBank`** — every `NeonSound` as a cached `AudioStream`: arrays of variations, footsteps by surface, and
  each sound's default bus and falloff.
- **`NeonMusic`** — the stereo's two original synthwave loops, *Night Drive* (100 BPM, A minor, four on the floor)
  and *Rain Signal* (90 BPM, D minor, half-time with an arpeggio). Each has 8 bars and four stems: drums, bass, pads
  and lead.
  - **They play through the engine's adaptive music** (`AdaptiveMusicPlayer`, ADR 0196) rather than as plain loops.
    Each song is a manifest built in code: one looping section, a low-pass on every layer, and a `muffle` parameter
    whose curves close the filters and soften the lead.
  - The stems are rendered once into a cache folder (`$TMPDIR/neon-music`), because the runtime streams stems from
    files. Nothing is committed, and the stems are reused while their song, seed and version match.
  - Adaptive music is not positional, so `NeonMusicPlayer` places it: it sets the level from the distance to the
    `Stereo` socket and sets `muffle` from the listener's room and the doors in between.
  - Also here: the TV ad's jingle, a 7.5 s loop.
- **`NeonAudio`** — a root node in the scene, with generated children:
  - **Beds.** Rain and city loops, outdoor and indoor, crossfaded at equal power by *openness*. Openness is 0 inside
    the apartment's box (x −7..7, y 0..6, z −5.5..5.5) and 1 on the balcony and beyond. The open slider lets some of
    the outside in near it.
  - **City events.** Distant horns, sirens and PA chimes every ≈ 9 s. Flying cars pass the balcony with Doppler every
    ≈ 30 s.
  - **Muffling outdoor emitters.** Bus effects are fixed while a layout plays, so city events, fly-bys and outdoor
    emitters that interactables create are muffled indoors through each voice's distance low-pass.
  - **Footsteps.** They play on the Bathroom bus in the bathroom.
  - **Machines and the stereo.** The fridge hum at `Furn_Fridge`, PC fans at the two racks, and the stereo.
  - **Hooks.** Doors (`DoorBase.Toggled`/`Settled`), the slider, the front door's `Rattled` and the switches'
    `Switched`.
- **Buses** (`Assets/Settings/AudioBusLayout.mres`, referenced from `project.mfproj`):

  | Bus | Level | Effects |
  |---|---|---|
  | Ambience | −8 dB | — |
  | Rain | −7 dB | — |
  | Foley | −6 dB | a light reverb |
  | Bathroom | −6 dB | a tiled room's reverb |
  | Music | −8 dB | — |
  | Tv | −9 dB | — |

  The SFX and UI buses come from the engine's defaults.

**For interactables** (0 B per call):

```csharp
NeonAudio.PlayAt(NeonSound.KeyClick, worldPosition);                    // a one-shot from the pool (volumeDb, bus optional)
var shower = NeonAudio.CreateLoop(NeonSound.Shower, showerNode);        // an AudioPlayer3D the caller Play()s / Stop()s
var tap = NeonAudio.CreateLoop(NeonSound.Tap, tapNode, NeonAudio.BathroomBus);
var stereo = NeonAudio.Current?.Music;                                  // the stereo: Play(), Next(), Stop(), Toggle(), SongChanged
```

**Tests:**
- `NeonSynthTests` and `NeonMusicTests` check determinism, seamless loops, levels and headroom, each sound's band, and
  the music manifest.
- `NeonAudioTests` check the wiring, the indoor/outdoor mix, the hooks, rooms and the stereo, and 0 B per frame.
- `NeonAudioRenderTests` (`just neon-audio`) render mixes through the engine's offline path into `artifacts/audio/`,
  and every raw sound into `artifacts/audio/sounds/`.
