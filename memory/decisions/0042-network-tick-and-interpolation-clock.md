# ADR 0042 — `MultiplayerApi` as a frame server with its own network tick; client interpolation clock

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M5 (W3 lane m5-repl)

## Context

Replication must run inside the M2 server pattern, separate from rendering, and decide between the physics tick and
its own rate. Clients need a render time for interpolation.

## Decision

- `MultiplayerApi : IFrameServer`, registered in `SceneTree.Servers` (`Engine` registers one as
  `Engine.Multiplayer`; `MultiplayerApi.Attach(tree)` elsewhere). It **receives** at `SceneTree.ProcessFrame` (start
  of the process step, so RPCs/snapshots apply before nodes process; the frame delta comes from the new
  `SceneTree.ProcessDeltaTime`) and **sends** in `IFrameServer.Process` (end of frame, after deferred frees and
  transform sync).
- **Own fixed tick** (`TickRate`, default 30 Hz): an accumulator; one capture + one snapshot per client when at least
  one tick elapsed (several elapsed ticks advance the counter but send once). Independent of the 60 Hz physics tick
  so bandwidth is tunable without touching simulation.
- **Client clock:** advances with frame time at the server's rate and is pulled 10 % toward each snapshot tick; an
  error over one second snaps it (and clears buffers). Interpolated members render at `estimate − InterpolationDelay`
  (0.1 s), extrapolate along the last velocity for at most `MaxExtrapolation` (0.25 s) when data is late, and hold
  when a newer snapshot proved them still (hold samples are inserted when a still member starts moving).
- Lifecycle timeouts use the same accumulated frame time (deterministic in tests).

## Consequences

- Deterministic tests with a manual clock; a stalled frame on the client shows briefly ahead of the data and is
  corrected by the next snapshots.
- A game that wants snapshots aligned to physics can set `TickRate = PhysicsTicksPerSecond`.
