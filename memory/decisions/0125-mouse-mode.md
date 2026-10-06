# ADR 0125 — Mouse mode

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Driving Range Simulator (Godot 4.7) — PM2 / E1 (the game's `docs/porting.md`)

## Context

A first-person game captures the mouse for mouse look and frees it for menus (Driving Range toggles it with Tab and
gates movement on `Input.mouse_mode == CAPTURED`). Game code had no way to set the cursor mode: only the engine's
`Engine.InputContext` reaches Silk's `ICursor.CursorMode`, and nodes have no handle on the host.

## Decisions

1. **`MouseMode`** with Godot's names and ordinals (`Visible`, `Hidden`, `Captured`, `Confined`, `ConfinedHidden`);
   **`InputState.MouseMode`** holds it and **`Input.MouseMode`** forwards to the current state (Visible outside a running
   engine; setting it there is ignored).
2. **The engine applies it** (`InputState.MouseModeChanged` → `InputRouter.ApplyMouseMode`, on real changes only):
   `Captured` → `CursorMode.Raw` (SDL relative mode), `Hidden`/`ConfinedHidden` → `CursorMode.Hidden`, else `Normal`.
3. **Relative motion survives capture**: Silk's SDL mouse accumulates `xrel`/`yrel` into its position in Raw mode
   (checked in Silk.NET.Input.Sdl 2.23.0's `SdlMouse`), so `InputRouter`'s position delta is the relative motion. Each
   mode change resets the last position, so switching never produces a jump.

## Consequences

- Games capture and release the mouse with one line, as in Godot; the game UI already ignores a Raw mouse.
- Test: `InputMapTests.MouseModeIsGodotsAndNotifiesTheEngineOnChange`. The SDL path itself is exercised by games (the
  render host's synthetic SDL events do not switch modes).
- Known issues: Confined modes do not confine yet; no focus-loss release.
