# ADR 0122 — Godot's 2D audio listener and linear pan

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM6 / E14 (the game's `docs/porting.md`)

## Context

`AudioPlayer2D` claimed Godot's model but panned equal-power around the active `Camera2D`'s position over a fixed
`PanDistance2D` (960). Godot's `AudioStreamPlayer2D::_update_panning` (no `AudioListener2D`) hears from the view
centre and pans linearly with a project-wide 0.5 strength, so a sound in the middle of the screen plays at 0.5 per
channel. The port's positional sounds were ~6 dB louder than the game's mix in Godot.

## Decisions

1. **Listener = the root viewport's view centre**: `CanvasTransform⁻¹ · (visible size / 2)` (camera offset, smoothing
   and zoom included), read once per audio frame with the canvas transform and visible size.
2. **Godot's formula verbatim**: distance in canvas units, silent beyond `MaxDistance`, gain
   `(1 − d/MaxDistance)^Attenuation`; pan = screen x offset from the centre ÷ visible width, clamped to ±1, ×
   `PanningStrength` × `AudioServer.PanningStrength2D` (Godot's `audio/general/2d_panning_strength`, 0.5) × 0.5 + 0.5,
   clamped to [0, 1]; left = 1 − pan, right = pan.
3. **`AudioServer.PanDistance2D` is removed** (the visible width replaces it); `AudioServer.CanvasTransform2D` and
   `ScreenSize2D` are new; `AudioPlayer2D.Spatialize` takes the emitter, listener, canvas transform and screen size.

## Consequences

- Every 2D sound is 6 dB quieter in the centre than before and pans less (Godot's defaults); games tuned in Godot
  sound the same. `AudioListener2D` is not implemented (the game does not use one).
