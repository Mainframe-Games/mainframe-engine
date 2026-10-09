# ADR 0180 — The Forest's pause menu: live settings from one option table, cameras, settings.json

- **Date:** 2026-10-10
- **Status:** accepted
- **Milestone:** G8 forest showcase (G8d follow-up)
- **Spec:** docs/design/forest.md#pause-menu

## Context

Brogan asked for a settings menu on Escape, for graphics and audio, "just a bunch of stuff to play around with", plus
buttons that start the fly-over and the static reference shots. Until now Escape only released the mouse, and
`ForestSettings` (`settings.json`) held the FOV, the look settings and the audio volumes, with nothing to edit them.

## Decision

- **One option table.** `ForestOptions.All` lists every setting as a `ForestOption`: key, label, page and group, kind
  (slider, Off/On switch, choice), range and step, readout, tooltip, "greyed out when", and `Get`/`Set` on a
  `ForestWorld` (the scene's sun, environment, profile, lens, probes, terrain, player, audio, readout and the renderer
  behind `IForestDisplay`). The menu's RML, its data bindings, the defaults, the persistence and the tests all come
  from the table, so a new option is one entry.
- **Live, through runtime properties.** Every option sets an existing runtime property (the renderer's
  anti-aliasing, upscaler, render scale and VSync; the sun's shadow properties; the environment's fog and wind; the
  `PostProcessProfile`; the lens; the controller; the audio buses) except the ground cover, which had no runtime path:
  the one engine change is `Terrain3D.FoliageDensityScale` and `FoliageDistanceScale` (0–1), applied in
  `TerrainFoliage3D.UpdateDistances` without a rebuild.
- **Defaults are the scene's.** `ForestWorld.Capture` records each scene option's value before saved settings apply;
  Reset restores them, and `settings.json` stores only the graphics options that differ (`Graphics` map), so a later
  change to the Forest's look (another lane's shadow defaults) still reaches players who never touched that row.
  Relative rows (haze, fog density, glow, wind) are percentages of the look.
- **Saved settings apply in play only.** Screenshots, benchmarks, autowalks and menu QA keep the project's look.
- **The world keeps running.** The menu disables the controller (and the free camera) instead of pausing the tree,
  so wind, water, birds and a fly-over keep moving while settings change.
- **Cameras.** `ForestCameras` owns the view: player, a looping arc-length fly-over along the benchmark spline, the
  reference shots with their photo lenses, a free camera. Each switch resets the TAA history.
- **The sun moves without a re-bake.** The probes stay baked for the morning sun; the group is labelled "lighting
  bake approximate".
- **Escape belongs to the menu.** The menu sees input before the UI (`InputBeforeUi`) and the controller's own
  release-on-pause is turned off (`ReleaseMouseOnPause`).

## Consequences

- 0 B per frame while closed (tests, and the autowalk/benchmark run with it attached and hidden).
- The menu is authored in dp for 1920 × 1080 with one `@media (max-width: 1599dp)` narrow layout; ADR 0181's
  reference-resolution scaling keeps the 1080p layout on every 16:9 window.
- RmlUi specifics found on the way: `data-visible` keeps the element's space (use a class with `display: none`), and a
  box-shadow texture larger than the window is clipped and stretched.
- `settings.json` gains `Graphics` and `Audio.Muted`; older files load unchanged.
