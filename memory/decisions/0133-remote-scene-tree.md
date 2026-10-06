# ADR 0133 — Remote scene tree over the editor link

- **Date:** 2026-10-06
- **Status:** accepted (the Crash Site Defense port's E18, Godot parity)

## Context

Godot's Scene dock switches to a Remote view while a game runs from the editor, showing the running game's tree.
The port needs it to debug what the game builds at run time (chunks, players, buildings), which the edited scene never
shows (ADR 0127: game code does not run in the editor).

## Decisions

1. **Protocol version 2.** A `RequestTree` command and a `Tree` message: `u8 truncated, i32 count, count × (i32 depth,
   str name, str type)`, depth-first from the root. At most 3000 nodes, names and type names cut to 48 characters, so
   the worst case (three-byte UTF-8) stays under the 1 MiB frame. The type is the C# short name (the registry's name).
2. **Pull, not push.** The editor asks about once a second, only while the Remote view is shown; the game answers on its
   main thread (`GameSession.SnapshotTree`), and the client sends the latest snapshot next to the status (a newer one
   replaces an unsent one). An idle game with the view closed does no work.
3. **Read-only, names and types only.** No remote selection or remote inspector yet (Godot's remote inspector would need
   property values over the link; later if needed).
4. **UI:** a Local | Remote segmented switch in the Scene panel while any instance is connected, a chip per instance
   when there are several, expand state per node path (root and its children start expanded), back to Local when the
   last game stops.

## Consequences

- A version-1 game treats `RequestTree` as a malformed frame and drops the link (it reconnects); the hello's version
  warning in Output already says to rebuild the game.
- A remote inspector can extend the same request/reply pattern.
