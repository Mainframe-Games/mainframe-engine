# ADR 0114 — Godot's runtime rules for game hosts: timing, offline authority, user args, skew

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM1 / G7a (the game's `docs/porting.md`, rows E13 E17)

## Context

The first world render of the port compared against Godot showed behaviour the engine did differently: the first frame's
delta (748 ms of startup) reached `Camera2D` smoothing and threw the camera across the map; an offline game was not its
own server, so server-stepped logic did not run; the game's `++` flags were parsed by the host (`--screenshot`); sheared
cast shadows lost their skew when set as a `Node2D.Transform`; the editor wrote `.meta` files for shader build outputs.

## Decisions

1. **Process delta excludes dropped physics time** (Godot `Main::iteration`): `SceneTree.Tick` subtracts the time it
   clamps (`MaxFrameDelta`) and the backlog it drops (beyond `MaxPhysicsStepsPerFrame`) from the delta process callbacks,
   timers and frame servers see (`ProcessDeltaTime`). The backlog drop keeps the sub-step remainder.
2. **Offline is its own authority:** `MultiplayerApi.IsServer` is true unless the API is a client (Godot's offline peer).
3. **`++` separates the game's arguments:** `GameHostOptions.UserArgs` / `GameHost.UserArgs` hold everything after the
   first `++`, never parsed by the host (Godot's `OS.get_cmdline_user_args`).
4. **Nodes can ask the host:** `SceneTree.Quit(code)` and `SceneTree.CaptureFrame(callback)` (needs `--frame-capture`,
   implied by `--screenshot`; the capture is the frame being built, a frame later than Godot's viewport read).
   `ProjectSettings.Version` ("version") and `GameHost.Project` expose the game's version.
5. **`Node2D.Skew`** (radians; `SkewDegrees` serialized) and `Transform2D.Skew` / the skewed `FromTrs` follow Godot; a
   transform assigned to `Node2D.Transform` is kept exactly and only decomposed for the getters.
6. **Shader build outputs are not assets:** `x.gdshader.vert.spv`, `.frag.spv` and `.spvlock` get no `.meta`.

## Consequences

Tests that advanced time with one huge frame now step in normal frames (Camera2D, editor autosave). Sandbox's NetBox
runs its server logic offline too.
