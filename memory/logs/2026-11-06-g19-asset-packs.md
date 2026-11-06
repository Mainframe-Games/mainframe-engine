---
summary: >-
  G19 phase 2: exports ship Assets/ and project.mfproj in one Assets.mfpak (.mfpak v1: Brotli text, stored seekable
  media, path-hash anchors so SteamPipe patches stay small, ADR 0247), GameHost mounts it, preset keys pack and loose,
  mf-export --pack-list/--pack-extract, just export-smoke. Encryption (phase 3) and NativeAOT (phase 4) remain.
---

# 2026-11-06: G19 phase 2, `.mfpak` asset packs

Branch `feature/g19-asset-packs`. Plan: [G19 asset packs](../plans/2026-11-06-g19-asset-packs.md). Design:
[Protected exports](../../docs/design/protected-exports.md) (phase 2 ticked, "Changes from the proposal,
phase 2"), [Asset pipeline → Packs](../../docs/design/asset-pipeline.md#packs),
[Game export → Packed content](../../docs/design/game-export.md#packed-content).

## What changed

- **Engine (`Src/Core/Content/`):** `PackFormat` (header, codecs, `Place`/`IsAnchor`), `PackWriter` (deterministic;
  `GameFiles` of a staged folder), `PackReader` (one handle, `RandomAccess`, 0 B stored reads and span lookups),
  `PackContentSource` (`MountBeside`). `GameHost.MountExportPack()` runs before the project file is read.
- **Presets:** `pack` (default true) and `loose` in `ExportPreset`, the format writer and reader (no format bump: new
  keys default), the validator (`loose-outside-assets`, `loose-without-pack`), the editor's Export dialog (General ›
  Pack Content, Content › Loose chips; `ExportGlobList`).
- **Export:** the stage step packs the stage and deletes what it packed (`ExportRunner.PackContent`);
  `mf-export --pack-list` / `--pack-extract`.
- **Checks:** `PackTests` (10), the mounted-content tests as theories over a real pack (14), runner tests for the packed
  default, `pack: false` and `loose`; the integration export is packed and its headless run logs
  `Content from Assets.mfpak`; `build/template-smoke.sh` checks the archive holds only the pack and extracts it;
  `ContentLoadBenchmarks`; `build/export-smoke.sh` (`just export-smoke`).
- **Docs:** asset-pipeline (Packs), game-export (rows, Packed content, validation, CLI, CI, testing), editor (dialog),
  protected-exports, milestones (G19 🚧), AGENTS.md, README, landing page.

## Decisions

- [ADR 0247](../decisions/0247-pack-anchors-for-steampipe.md): entries start pages at path-hash anchors (about one
  per 16 MB, plus every entry of 1 MB or more). The first layout ("never cross a page") re-flowed every later page after
  an insert; the test caught it.
- The writer lives in the engine, not the export library (tests, benchmarks and mobile's cook use it).

## Verified

- `just build`, the Release build (0 warnings), `just format-check`, `just shaders-check`, `check.sh`.
- `just test`: engine 3396 passed, video 464 passed. Editor: 1692 passed, 1 failed:
  `SongBakeTests.TheBakeSongFlagBakesIntoTheSongsProject` fails in the full run every time, with "Plugin host connection
  lost … during hello" (the native plugin-host helper, which this branch does not touch), and passes alone.
- `just test-render`: 234 passed.
- `just template-smoke`: passed (packed export, extracted and checked, run headless).
- `just export-smoke`: 8 Demo scenes match their loose screenshots exactly (RMSE 0). Physics, particles and tiles
  differ between two loose runs as well, so they run from the pack and are checked for errors only.

## Benchmark

`ContentLoadBenchmarks` (M5 MacBook, nothing else running): PNG decode 2.79 → 2.85 ms (+2 %), UI text 14.2 → 8.5 µs,
200 KB scene text 103 → 125 µs (+21 %, ~22 µs, the decoded copy). Over the 10 % target only for Brotli text; recorded
in the proposal's open question for the owner.

## Not done

- Phase 3 (AES-256-CTR, the pack key, project format 6) and phase 4 (NativeAOT).
- The human checklist: ILSpy/agent pass over a packed export, a Steam depot patch-size check, load-time feel.
- `docs/images/editor-export.png` still shows the dialog without the Pack Content row.
- Brotli versus a zstd/LZ4 package: owner decision; no package added.
