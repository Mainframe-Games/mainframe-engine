---
summary: >-
  The editor builds a project's game code before opening its scenes when the sources are newer than the build (a pull
  or branch switch); a failed build still loads the previous one. Fixes the Forest failing to open after G6.4's LOD rename.
---

# 2026-11-07: The editor builds stale game code before opening scenes

Branch `fix/editor-builds-stale-game-code`. No plan (small fix). As built:
[Editor → Projects](../../docs/design/editor.md) (the `ProjectService` item).

## What changed

- **`ProjectService.Open`:** when the game library's build output is older than its `.cs` sources, it builds first and
  then loads (as for a missing build) instead of loading the old build and logging "Build & Reload to use them". When
  that build fails, the previous build loads and `NeedsRebuild` stays set.
- **Tests:** `CodeReloadTests.OpeningAProjectBuildsGameCodeOlderThanItsSourcesBeforeOpeningScenes`,
  `AFailedBuildOfStaleGameCodeStillLoadsThePreviousBuild` (a callback `IGameBuilder`).
- **Docs:** editor.md, the `ProjectService` item.

## Decisions

- A failed build of stale code falls back to the previous build rather than opening with missing nodes: the scenes keep
  their game types, and the build errors and "rebuild needed" say what is wrong.

## Dead ends

- None.

## Verified

- The cause: `Loading 'Assets/Scenes/forest.mfscene' failed: grass_medium_01 has no mesh node
  'grass_medium_01_mid_b_LOD0'` came from a Forest.dll built before #189 renamed the clutter variants (the importer
  merges `_LOD0` nodes into `<name>`, ADR 0245); the editor loaded it because it loaded.
- Repro in the real editor: the Forest built with the old name, the source restored, then `Tests/QA/forest-open.qa`:
  "building Forest.csproj first", the rebuilt dll has the new name, the Forest opens with 0 errors and 0 warnings and
  reopens twice.
- Both new tests fail without the change. `just build`, the Release `-warnaserror` build, `just format-check`,
  `just test` (engine 3699, editor 1786, video 464) pass.

## Not verified

- `just test-render`, `just shaders-check`: no rendering or shader change.

## Next

- None.

## State changes

None.
