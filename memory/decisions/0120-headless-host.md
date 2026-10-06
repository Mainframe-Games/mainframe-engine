# ADR 0120 — Headless host for dedicated servers

- **Date:** 2026-10-06
- **Status:** accepted
- **Milestone:** port of Crash Site Defense (Godot 4.7) — PM3 / E13 (the game's `docs/porting.md`)

## Context

The game ships a dedicated server (`godot --headless ++ --server`, `just run-server`). `Engine` always creates an SDL
window and a Vulkan renderer (`--hidden` only hides the window), so a server needed a display and a GPU.

## Decisions

1. **`--headless` selects a separate host, not an `Engine` mode:** `HeadlessHost` owns a `SceneTree`, the servers a
   server needs (`MultiplayerApi`, `PhysicsServer3D`/`2D`, `AudioServer` on the null device when audio is enabled)
   and the same `GameSession`; `Engine` stays window-bound. Godot's flag name, so the game's command lines carry over.
2. **A simple paced loop:** `window.maxFps`, else the physics tick rate (60); no catch-up after a long update.
   `--fixed-fps` and `--max-frames` behave as in `GameHost`.
3. **Canvas draws still run** (`FlushCanvasRedraws` each update): Godot calls `_draw` headless, and an unflushed
   queue would keep freed items alive.
4. **Ctrl+C and SIGTERM are a close request** (`SceneTree.CloseRequested`, then quit) rather than an abrupt kill.
5. **No inert UI:** RmlUi models need a native context, so `UiDocument.CreateDataModel` still throws headless; game
   UI checks `GameHost.IsHeadless` (Godot's headless display server readies Controls; the port's UI does not).

## Consequences

- A dedicated server runs on a machine without a display, GPU or sound card (CI, Linux boxes).
- The Linux server build (E19) can drop the Vulkan/SDL natives later; today they are still shipped, just not loaded.
