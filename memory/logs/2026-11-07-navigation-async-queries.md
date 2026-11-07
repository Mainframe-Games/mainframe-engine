---
summary: >-
  G3.10 (ADR 0262): async navigation queries — QueryPathAsync on thread-pool workers over the live map, delivered at the
  next sync after it waits for them; agents' PathMode; project.mfproj format 7 navigation section; 0 B.
---

# 2026-11-07: Async navigation queries (G3.10)

Branch `feature/navigation-async` (stacked on `feature/navigation-polygons`, #196). Plan:
[G3.10 async navigation queries](../plans/2026-11-07-navigation-async-queries-g3-10.md). As built:
[Navigation → Async queries](../../docs/design/navigation.md#async-queries-g310).

## What changed

- **Engine:** `NavigationQueryWorkers` (reused request slots, `IThreadPoolWorkItem` workers with their own
  `NavigationQueryScratch`, `Complete()` at every map sync: run what is left, wait, deliver in submission order),
  `NavigationPathRequest`, `NavigationMap2D.QueryPathAsync` and a scratch-taking query core; `AStarGrid2D` queries can
  run on a caller's `GridSearch` (`End`/`Expanded` moved into it); `NavigationPath2D.CopyFrom`;
  `NavigationServer2D.QueryWorkerCount`, `AsyncAgentsByDefault`, `PendingQueries`, scope `Navigation async wait`;
  `NavigationAgent2D.PathMode` (`NavigationPathMode`), `IsPathAsync`, `IsPathPending`; `project.mfproj` format 7 with a
  `navigation` section (`workers`, `asyncAgents`) applied by `GameHost` and `HeadlessHost`. Navmesh paths now clear
  their `Cells` (they kept stale cells from an earlier grid query in a reused path).
- **Demo:** Navigation 2D's panel toggles Async paths; es translation.
- **Tests:** `NavigationAsyncQueryTests` (8, with the async allocation case), a `ProjectSettingsTests` case, format
  expectations at 7, a Demo test.
- **Docs:** navigation (section, types, tests, changes, profiler), project-and-gamehost (the section, format 7),
  proposal trimmed, demo, AGENTS.md, README, landing page.

## Decisions

- [ADR 0262](../decisions/0262-navigation-async-queries.md): no snapshots — the sync waits for the workers before it
  changes the map; thread-pool workers; `PathMode`; format 7.

## Dead ends

- A first test compared `Cells` of navmesh paths in a reused `NavigationPath2D`: the immediate query kept the cells of
  an earlier grid path. Fixed in the map (navmesh points have no cells), not in the test.

## State changes

For the state sync (ADR 0280); this PR leaves `docs/milestones.md` and `memory/context/current-state.md` as on `main`.

- milestones G3b: after G3.9's entry add "G3.10 async queries ✅ (ADR 0262: `QueryPathAsync`, agents' `PathMode`,
  `project.mfproj` format 7 `navigation`)"; "G3.10–G3.11 ⬜" becomes "G3.11 ⬜".
- state: the 2D navigation entry in What exists gains "async path queries (G3.10, ADR 0262)"; the project format line, if
  the state file names one, becomes format 7 (`navigation` section).

## Verified

- See the PR body.

## Not verified

- Speed-ups on a quiet machine: no async benchmark was recorded (the machine was busy with parallel sessions).

## Next

- G3.11 3D grid + `NavigationAgent3D`; G3.12 3D navmesh baking on DotRecast.
