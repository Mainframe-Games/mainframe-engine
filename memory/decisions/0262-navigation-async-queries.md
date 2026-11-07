# ADR 0262 — Async navigation queries: worker threads on the live map, delivered when the next sync has waited for them

- **Date:** 2026-11-07
- **Status:** accepted
- **Milestone:** G3b (part: G3.10)
- **Docs:** docs/design/navigation.md#async-queries-g310, memory/plans/2026-11-07-navigation-async-queries-g3-10.md

## Context

G3.10 asked for opt-in async path queries: `NavigationMap2D.QueryPathAsync` returning a struct handle, thread-pool
workers with their own scratch, results delivered at the next sync (so they stay deterministic), agents that keep their
old path until the new one arrives, and a `project.mfproj` section. The proposal had workers search immutable,
copy-on-write snapshots of each grid, allocating a new snapshot on every tick a grid changed. G3.9 added navigation
meshes, which a grid snapshot does not cover.

## Decision

- **No snapshots: the sync waits first.** Every map sync starts with `NavigationQueryWorkers.Complete()`: it runs what no
  worker has taken yet on the main thread, waits for the workers, and copies each finished result into its caller's path
  in submission order. Only then does the sync apply cell edits, bakes, moves and region changes. A map changes only at
  its syncs, so between two syncs workers can read the live grids and mesh while the main thread queries them too (each
  with its own `GridSearch`/`NavMeshSearch`; the grid's per-query state moved into `GridSearch`). A result depends only on
  the map at submission and the query, and delivery is always the next sync whatever the threads did.
- **Dedicated worker threads, not the .NET thread pool.** Up to `QueryWorkerCount` background threads per server
  (default a quarter of the processors, 1–4), started on first use, each with its own scratch, sleeping on a monitor
  between batches and looping over a lock-guarded list of reused request slots that own their result paths. 0 workers
  runs every request at the sync on the main thread, with the same results. Handles are structs carrying a ticket. The
  proposal's cached `IThreadPoolWorkItem`s allocate nothing per request, but on Windows the pool's global queue
  allocates a new segment (4 KiB) as work items flow through it: CI's allocation gate caught 4192 bytes per window.
- **Agents: `PathMode`** (`ProjectDefault`, `Immediate`, `Async`) instead of a bool, so `navigation.asyncAgents` can be
  the project default. For a map change or a push off the path an async agent keeps its old path until delivery; for a
  new target it stands still for that tick, because the old path leads to the old target and its end would count as
  arriving or finishing.
- **`project.mfproj` format 7** with an empty 6 → 7 step, like 4 → 5 for `export`: an older editor rejects the file
  instead of dropping the `navigation` section when it saves.

## Ruled out

- **Copy-on-write snapshots** (the proposal): allocations on every changing tick, a second copy of every grid, and a
  snapshot design for navmeshes too; waiting at the sync gives the same isolation for free.
- **Delivering whenever a worker finishes:** results would depend on thread timing (an agent could get its path in this
  tick or the next), losing determinism.
- **The .NET thread pool** (the proposal's choice): its global queue allocates segments as items pass through it on
  Windows, so a steady stream of requests is not 0 bytes there.

## Consequences

- Async queries cost the main thread only the copy and any wait at the sync; requests made in `OnPhysicsProcess` overlap
  the physics step and the rest of the tick.
- A sync can block until the slowest submitted query finishes: a game that submits many long queries in one tick waits
  for them at the next sync.
- Nothing may change a map outside its sync; future map edits must keep going through the pending queues.

## Revisit when

- Profiles show the sync's wait as the bottleneck (deliver two syncs later, or let long queries span syncs with an
  explicit "not ready" status).
- A platform without threads needs a different worker (WebGPU, M11): 0 workers already runs everything at the sync.
