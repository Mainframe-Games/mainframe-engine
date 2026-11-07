# Plan: G3.10 async navigation queries

**Status:** in progress
**Docs:** [Navigation proposal, later phases](../../docs/design/future/navigation.md) (§ Threading, Testing, task 5),
[Navigation](../../docs/design/navigation.md) (maps and sync points, agents, navigation meshes),
[ADR 0228](../decisions/0228-navigation-server-agents-and-sources.md), [ADR 0261](../decisions/0261-navigation-meshes-2d.md)
**Started:** 2026-11-07

## Goal

- `NavigationMap2D.QueryPathAsync(in query, NavigationPath2D result)` returns a `NavigationPathRequest` (a struct:
  `IsCompleted`, `Status`, `Cancel`). Requests run on .NET thread-pool workers (`IThreadPoolWorkItem`s cached per worker,
  each with its own grid and navmesh search scratch) and are delivered — copied into `result` — at the map's next sync,
  which first waits for every request submitted before it. Delivery is therefore always the next sync, whatever the
  thread timing: deterministic.
- No snapshots: the map's grids and mesh change only at syncs, after the wait, so workers read the live data between
  syncs (a change from the proposal's copy-on-write snapshots: same guarantees, no copies, 0 B with edits).
- `NavigationAgent2D.PathMode` (`ProjectDefault`, `Immediate`, `Async`): an async agent keeps following its old path until
  the new one arrives, then raises `PathChanged`.
- `project.mfproj` `navigation` section: `workers` (0 = automatic) and `asyncAgents` (the agents' project default);
  format bump with a migration only if an older engine would misread the file.
- 0 B per tick once warm with async agents; profiler scope for the wait.

## Not in scope

- Async bakes; async queries for 3D (G3.11 reuses the same machinery if it fits).

## Steps

- [ ] `GridSearch` owns the per-query state (`End`, `Expanded`); `AStarGrid2D` queries take a scratch (internal). Verify:
  the existing grid tests and golden maze unchanged.
- [ ] `NavigationQueryWorkers` (request slots with their own path buffers, worker items, a lock-guarded queue, the
  sync's wait and delivery), `QueryPathAsync`, `NavigationPathRequest`. Verify: `NavigationAsyncQueryTests` — delivery
  exactly at the next sync, never before; same points as `QueryPath`; many requests across workers bit-identical over
  runs and with 1 vs 4 workers; cancel; requests on a map whose region changes in between see the old map; grids and
  navmeshes; 0 B steady state.
- [ ] Agents: `PathMode`; following the old path until delivery; `PathChanged` on delivery. Verify: agent tests; the
  allocation gate with async agents.
- [ ] Project settings section, `GameHost` wiring of the worker count and default. Verify: `ProjectSettings` round
  trip and migration tests.
- [ ] Docs, ADR 0262, log, milestones; gates; PR.

## Done when

- `NavigationAsyncQueryTests` and the async allocation case pass in `just test`.
- All gates pass locally; the PR is open with Auto-fix on.

## Open questions

- None that change what is built.
