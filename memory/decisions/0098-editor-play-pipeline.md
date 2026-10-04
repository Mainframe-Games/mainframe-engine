# ADR 0098 — Out-of-process Play: PlayService, shortcuts and Output streaming

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (E4 editor side; lane m10c-projects)

## Context

Play must run the game in its own process (a crash never takes the editor down; no static state leaks between runs)
through the editor link (ADR 0092), support several instances (server + client), show game logs and build errors in the
Output panel, and stay responsive while `dotnet build` runs.

## Decision

- **`PlayService`** (main-thread API, background work only queues): builds through `IGameBuilder` (`dotnet build
  -v:minimal -p:GenerateFullPaths=true`, MSBuild diagnostics parsed and de-duplicated), launches through `IGameLauncher`
  (the launcher's apphost, else `dotnet <dll>`, with `--editor-port` and `--scene <uid|path>`), owns one
  `EditorLinkServer`, and drains link messages, process output and build results in `Update()`. A hello matches the
  instance by process id, else the oldest launching one. Exit code 0 or an editor-requested stop is *Exited*,
  anything else *Crashed*; Stop sends the command and kills after 3 s (at once when never connected).
- **`PlayController`** (editor glue): F5 play main scene, F6 play the open scene (saving it; an untitled scene asks for
  a file), Shift+F5 another instance, F7 pause/resume, F8 stop, Ctrl/Cmd+Shift+B build & reload. Play saves the open
  scenes that have a file (the game reads them from disk) and builds the solution (else the launcher project). Godot's
  play keys win: the RmlUi debugger moves from F8 to F9.
- **Output:** game log entries arrive as Output lines of category `game` (or `game·Category`), with the instance label
  when several run; their caller info opens in the code editor. Process stdout/stderr is shown only until the game
  connects (or when it crashes before connecting). Build diagnostics are `build` lines pointing at file:line.
- **Running instances** are chips in the toolbar (status icon, label; a menu: pause/resume, reload scene, stop, clear).

## Consequences

- Every Play runs an incremental build (~1 s for the template game when nothing changed); the build output change
  also reloads the editor's copy of the game code.
- The game's console output is not duplicated once its log streams over the link.
