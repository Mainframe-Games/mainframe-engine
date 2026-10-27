# Neon Demo

A first-person walk round a two-bedroom industrial loft about 20 floors up in a rainy neon city: a double-height
living room, a mezzanine with the master bedroom, steel stairs, a bathroom, a computer room and a balcony. You can open
doors and flip the lights, but you can never leave. It is a game project of its own, built against this checkout's
engine like [the Forest](../Forest/README.md), and **not** part of `MainframeEngine.slnx`.

**Status: phase N1, a graybox.** The apartment is a flat-coloured model built by a Blender script; you can walk
through every room, up the stairs to the loft and bedroom 2, and out onto the balcony. Doors (E) open and shut, the
balcony's glass door slides, the front door stays locked, and each room's switch turns its lights and fixtures off
and on. The city, rain, traffic, the working computer, the shower, the art and the audio come in later phases.

## Run

```bash
just neon                                   # Release; Esc releases the mouse, a click captures it again
just neon ++ --view -2.2,3,4.6,-55,-18      # stand somewhere: feet x,y,z, yaw (0 = towards the front door), pitch
just neon ++ --open-doors                   # every door open (and --lights-off <Room>: Living, Bath, Computer, ...)
just neon-test                              # Neon.Tests: interactables, a walk through every room, containment (no GPU)
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
- `Neon/Src/World/` — `NeonScene` (the scene builder: `--write-scenes`), `NeonApartment` (loads the model and turns its
  named nodes into doors, lights, switches, colliders and sockets), `FallSafety` (respawns a player who falls out).
- `Tools/apartment/build_apartment.py` — the apartment, headless in Blender (seeded; `--preview DIR`, `--verify DIR`).
  Its docstring lists the naming conventions the game reads.
- `Assets/Scenes/neon.mscene` — written by `dotnet run --project Examples/Neon/Neon.Desktop -- --write-scenes
  Examples/Neon/Assets/Scenes`; a test checks it matches the builder.
