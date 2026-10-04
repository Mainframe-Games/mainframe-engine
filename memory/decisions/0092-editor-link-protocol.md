# ADR 0092 — Editor link: loopback TCP, framed binary, game connects to the editor

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (E4 play mode, engine side; lane m10b)

## Context

Play mode runs the game out of process; the editor needs its log, its status and a way to stop, pause and reload it.
No new dependencies are allowed, and the game loop must never block on the editor.

## Decision

- The editor listens (`EditorLinkServer`, loopback, port 0 → passed as `--editor-port`); the game connects
  (`EditorLinkClient`). The game connecting means the editor can restart and the game reconnects (back-off
  100 ms → 2 s), and a crashed game simply disappears.
- Frames: `u32 length` (type + payload, ≤ 1 MiB) + `u8 type` + little-endian payload; strings are `u32` + UTF-8 (strict
  decoding). Hello (protocol version, pid, project, engine version), Log, Status, LogDropped, Goodbye; commands Stop,
  Pause, Resume, ReloadScene (optional scene), Ping. A malformed frame closes that connection.
- Game side: logs and status are queued (lock-free `ConcurrentQueue`, latest-wins status) and sent by a background
  thread; commands are received by another and polled on the game loop. The log backlog is bounded
  (`QueueCapacity`); when full, **new** entries are dropped and counted, and the count is sent once the link catches
  up — the game never waits.
- Editor side: several games at once (server + client instances), each message tagged with a `GameId`;
  `Connected`/`Disconnected` markers are local; the editor polls `TryRead` on its main thread.

## Consequences

- The editor lane (m10c) consumes `EditorLinkServer` directly; a remote scene tree can be added as new message types
  with a protocol version bump.
- No authentication: loopback only.
