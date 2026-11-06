---
summary: >-
  G8d close-out: the forest-mini render test (engine assets only; terrain, six Ez Tree trees, grass, a carved stream,
  probes, physical sky, height and volumetric fog, histogram auto exposure, staggered PCSS shadows, TAAU) with goldens on
  moltenvk and lavapipe and a 0 B walk gate; the G8d frame-budget run on a busy machine (not met); the forest-showcase
  proposal moved into forest.md and removed.
---

# 2026-11-07: Forest goldens and the G8d budget run (G8d.15)

Branch `feature/forest-goldens` (stacked on `feature/river-tool`). As built: [Forest → Tests](../../docs/design/forest.md#tests),
[Forest → Performance](../../docs/design/forest.md#performance).

## What changed

- Render tests: `ForestMiniScene` (`forest-mini` in the host's scene table, TAAU 0.75 unless overridden) and
  `ForestMiniTests` (golden at frame 90 with validation and self-checks; 0 B over 300 frames walking round the glade
  after a 180-frame warm-up (on lavapipe without sun shadows), `DOTNET_TieredCompilation=0`). Goldens: `Goldens/moltenvk/` and `Goldens/lavapipe/`
  `forest-mini_frame0090.png`.
- Forest: `ForestClutter`'s scanned grass, moss and nettle variants name the merged LOD nodes (`grass_medium_01_mid_b`, not
  `…_LOD0`): G6.4 (#176) merges `_LODn` siblings at import, and the Forest had failed to load its scene since.
- Docs: `forest.md` gains the 2026-11-07 performance run, Budgets, Rendering phases, Decisions and Changes from the
  proposal, and the `forest-mini` test; `testing.md`; `milestones.md` (G8d ✅, the budget shortfall on G8e's row);
  `docs/design/future/forest-showcase.md` removed and every link to it repointed (docs, Forest code comments, ADR spec
  lines).

## Measured

`just forest-bench` on the Apple M5 with another session's render tests and Steam running (load 3.3–5.2): 1080p (3D at
0.75, TAAU) p50 25.5 ms, p99 33.6 ms; 2560 × 1316 native p50 38.5 ms, p99 51.4 ms; ≤ 1 038 / 1 065 draws; 0 B; load
14.9–16.7 s. Neither budget is met; `benchmark-baseline.json` is unchanged (busy machine).

## Not done

- A quiet-machine bench and baseline; why the draws rose from ≤ 692 to ≤ 1 038 and why the prepass costs nearly as
  much as the main pass.
- The G8d.9 CI step for `Forest.Tests`, `build/forest-smoke.sh`, self-contained player zips, butterflies.

## State changes

- milestones G8d: 🚧 → "✅ G8d.1–G8d.8 (physical sky, height fog, auto exposure, FXAA and the LUT, PCSS, shafts, contact shadows, TAA), the project, controller, valley, vegetation and props, audio, look and reference shots, `--benchmark` and `--autowalk`, the release zip and the editor's example picker, and `forest-mini` goldens on both drivers (G8d.15); not built: a CI step for `Forest.Tests`, `build/forest-smoke.sh`, self-contained player zips, butterflies".
- milestones G8e: 🚧 → "🚧; the frame budget is not met (2026-11-07, busy machine, Apple M5: 1080p with TAAU p50 25.5 ms, p99 33.6 ms; 2560 × 1316 native p50 38.5 ms, p99 51.4 ms; [Forest → Performance](../../docs/design/forest.md#performance))" (superseded by the objects log's G8e entry).
- state: the Forest entry in What exists gains "G8d shipped with the `forest-mini` render golden"; Next steps 2 replaces "forest goldens and frame budget" with "the Forest frame budget: G8e, see `docs/design/forest.md#performance`".
