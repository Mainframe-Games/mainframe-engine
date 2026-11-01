# ADR 0231 — Neon: a detailed city without lights, and a menu over UserSettings

- **Date:** 2026-11-01
- **Status:** accepted
- **Docs:** docs/design/neon.md

## Context

Neon's city read as flat boxes: black walls, large plain windows, bare roofs and empty streets, behind a strong glow, film
grain and heavy rain. The request was a city at the level of a AAA game, a lighter rain, no grain, and a themed settings
menu with many display options like the Forest's. The city cannot have real lights: about 820 towers, and ADR 0218
already keeps every light in the apartment.

## Decision

- **Fake the light, in the materials.** `facade.gdshader` draws interior-mapped rooms, recessed panes, wall materials and
  what the street's neon would do to a wall (`city_light`); `street.gdshader` traces the reflected ray to an imaginary wall
  of shops (`city_mirror`), soft shapes only; `spill.gdshader` lays pools of light on the ground. The glow threshold goes
  up to 1.6, over the windows and under the neon, so the detail stays visible.
- **Static level of detail.** The player never leaves the apartment, so a tower's mesh is chosen once, by its distance
  from it (`CityLayout.DetailRadius`), not per frame.
- **Props are not part of towers.** Towers are stretched up per instance, which would squash anything on them, so roof
  clutter and street furniture are a kit of their own (`props.gltf`), placed unstretched by the layout from a separate
  random stream: the towers and the traffic do not move when props change.
- **Storeys follow the kit.** The facade divides the kit's stretched `LEDGE` span into whole storeys, and window columns
  divide its `BAY`, so geometry and the procedural grid always agree.
- **`NeonMenu` is the Forest's design over the engine's `UserSettings`**, not the Forest's own JSON file, which predates
  it: window, audio and input are its values, everything else goes in a `neon` section, only while it differs from the
  scene's default. Key rebinding stays the engine's `SettingsMenu`, in a themed document.

## Consequences

- Close windows cost a few more instructions per pixel; `detail` (Window interiors) and the Low preset turn them off.
  About 75 fps at 1080p on the development Mac with the whole city in view.
- Lights on the city would still need a clustered renderer; this look is not a substitute for moving lights.
- The balcony's drips stop while the eye is out there: materials have no distance fade.
