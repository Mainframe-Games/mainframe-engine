---
summary: >-
  main's Mac-only gate failures: five moltenvk goldens left stale by #157/#164 (re-recorded, two missing ones added),
  a TAA self-check whose floor lines left empty rows at 640×480 (test fix), and a surface-shader lock that listed the
  build's deleted temp files because slangc's depfile names /private/var paths (code fix).
---

# 2026-11-05: Stale moltenvk goldens, TAA floor check, surface-shader lock

Branch `fix/stale-goldens-shader-lock`. No plan (a small fix). Found while shipping G13 2D paths: the same failures, with
the same pixel counts, on a clean `origin/main` (5e9b8fa5) on the Mac (Apple M5, MoltenVK 1.4.1, slangc 2026.1).

## What changed

- **`SurfaceShaderBuild` lock (code bug).** `AShaderBuildsEveryPassAndItsLayoutAndALockThatCoversItsModules` failed
  at `IsUpToDate` right after `Build`. Not `ContentFiles`: slangc's `-depfile` names files by their real path
  (`/private/var/folders/…`), while `Build` filtered its own temp entries against `Directory.CreateTempSubdirectory`'s
  `/var/folders/…`. Every generated entry file (`mf-<guid>.fragment.slang`, ten-plus) went into the lock as
  `../../../../../../private/var/…`, then the temp folder was deleted, so the lock pointed at missing files and was
  always stale. `Build` now compares and relativises dependencies against real paths (`SurfaceShaderBuild.RealPath`,
  every symbolic link resolved), and writes its entry files under the temp folder's real path. New
  `RealPathResolvesLinkedFoldersAsSlangcsDepfileDoes` runs without slangc (CI has none, which is why CI never saw it).
- **`TaaInAViewAccumulatesWithoutJitteringTheOverlay` (test bug).** Its floor-lines check (added in #157) probes a
  patch of 24 one-pixel debug lines 0.01 apart; at scale 1 (320×240, where #157 was verified, on an RTX 3070 Ti) they
  cover every row, but at the Mac's canonical scale 2 (640×480) they span 26 rows and leave two empty, and the probe
  at z = 1.425 lands on row 397, one of the gaps. All 24 lines drew, identically in both frames: nothing jittered. The
  patch now draws two lines per step, 0.005 apart. No golden involved (the TAA run captures none).
- **moltenvk goldens re-recorded (stale after intended changes)**, each inspected against its old golden:
  - `editor_frame0051`, `editor-viewport-tools_frame0040`, `editor-filesystem_frame0040`: #164's analytic grid
    (distance fade, thicker axes), 3 pt gizmos with solid heads, whole selection outlines (#157/#164's line depth
    pull). `editor-filesystem` also shows #162's versioned start line (`Mainframe Editor v0.0.0-dev started.`) and
    the template's `Shaders` folder (#160/#161). Nothing else moved.
  - `physics-joints_frame0090`, `subviewport-post_frame0008`: collider outlines and floor grid lines whole where the
    old frames had them dashed in the surfaces they lie on (#164's 8-pixel depth pull; #164 re-recorded only the
    `lavapipe` copies).
  - New: `gizmos_frame0010`, `grid-analytic_frame0005` (#164 added them for `lavapipe` only); they match the lavapipe
    frames apart from resolution.
- **lavapipe** (from `just render-tests-linux`, a `CI=true` build): `auto-exposure-histogram_frame0005` added (missing
  since ADR 0177; the patch reads 186 as on moltenvk). Re-recorded although within tolerance: `gizmos_frame0010` (its
  golden had the yellow debug lines dotted in the box and floor they lie on, 122 px off today's whole lines) and
  `editor-filesystem_frame0040` (recorded before #162: only the start line differs, 134 px).
- Docs: `shaders.md` (depfile real paths), `testing.md` (self-checks hold at both scales).

## Decisions

- Only the five failing goldens and the two missing ones are committed for `moltenvk`: `just golden-update` rewrote
  35, and the others were restored (they matched within tolerance).

## Dead ends

- The brief's suspicion (`ContentFiles` with an absolute temp path) was wrong; printing the lock showed the temp entries.
- The first worktree was deleted before the commit; the PNGs came back from the shared LFS store by their staged oids,
  the text edits were re-applied, and the branch was rebuilt on `main` after #173 (2D paths), then re-verified.

## Verified

- Before the rebuild, on 5e9b8fa5: `just test-render` 229/229 with every frame compared to a golden; `just test`
  3285 + 1646 + 464; `just render-tests-linux` 229/229 (its one frame without a golden, `auto-exposure-histogram`,
  now added). The shader tests with slangc 2026.1 (57, with the new one).
- On 844f4485 (after #173): `just test-render` 229/229, every frame compared to a golden; `just test` 3308 + 1661 +
  464; `just render-tests-linux` 229/229, every compared frame has a golden; `just build`, the Release build,
  `format-check`, `shaders-check` and `check.sh` clean.

## Not verified

- `editor_frame0051` and `editor-viewport-tools_frame0040` on lavapipe stay as recorded (12 and 5 px off today's
  frames).

## Next

- A rendering change verified off a Mac leaves `moltenvk` stale: its author should hand the Mac re-record on (noted in
  `current-state.md`).
