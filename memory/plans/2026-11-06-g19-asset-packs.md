# Plan: G19 phase 2, `.mfpak` asset packs

**Status:** done
**Docs:** [Protected exports](../../docs/design/protected-exports.md) (§ Pack format (phase 2), Content file system,
Scenarios → Phase 2, Testing), [Game export](../../docs/design/game-export.md), [ADR 0239](../decisions/0239-protected-exports.md),
[Asset pipeline → Content file system](../../docs/design/asset-pipeline.md)
**Started:** 2026-11-06

## Goal

Exports put the game's and the engine's `Assets/` and `project.mfproj` in one `Assets.mfpak` beside the executable,
and `GameHost` mounts it. The format leaves room for phase 3 (flags, nonces, key check) so it doesn't change again.

- **Format v1:** a 64-byte header (`MFPK`, version, flags, index offset and lengths, index nonce, key check), entries
  sorted by path, then a Brotli index at the end. Stored entries (`.ogg`, `.wav`, `.png`, `.jpg`, `.ktx2`, video,
  anything Brotli doesn't shrink) are 16 KB aligned and seekable; the rest are Brotli and decoded whole on open.
- **SteamPipe:** entries are laid out in 1 MB pages. An entry that would cross a page boundary and is smaller than a
  page starts on the next page, and larger entries start on a page boundary. A changed or inserted asset then shifts
  later data by whole 1 MB chunks, which SteamPipe matches (Valve's uploading guide: 1 MB padding alignment,
  per-asset compression, one table of contents).
- **Engine:** `PackFormat`, `PackWriter`, `PackReader` and `PackContentSource` in `Src/Core/Content/`. Reads use
  `RandomAccess` on one shared handle, which is thread-safe and allocates 0 B per read.
- **Export:** the `pack` (default true) and `loose` preset keys, the editor's Export dialog rows, the validator
  (loose glob problems), and the stage step (write the pack, delete what it packed).
- **GameHost:** mounts `Assets.mfpak` from `ContentPaths.BaseDirectory` before it reads `project.mfproj`, in the
  windowed and headless paths.
- **Checks:** a `ContentLoadBenchmarks`, `just export-smoke` (the Demo exported packed, its screenshots compared with
  loose ones), the template smoke's export checks updated for the pack, and the docs.

## Not in scope

- Encryption, the pack key, project format 6 (phase 3). NativeAOT (phase 4).
- Cooking (KTX2, binary scenes).

## Steps

- [x] Format, writer, reader, source. Verified by: `PackWriterIsDeterministic`, `PackRoundTripsAStagedFolder`,
  `StoredEntriesAreAlignedAndSeekable`, a page-layout test (`InsertingAnAssetShiftsLaterEntriesByWholePages`), bad
  magic or version fails at open, 0 B reads (`AllocationGate.SmallestWindow`).
- [x] Mounted-content tests run over a pack too. Verified by: `MountedContentTests` as theories over the memory
  source and a pack built in the test (UI, fonts, catalogs, streamed audio, glTF, Spine).
- [x] Preset keys and stage. Verified by: format round trip of `pack`/`loose`; `PackFalseKeepsLooseAssets`,
  `LooseGlobsShipBesideThePackAndStillLoad` (runner tests over a fake publish); the validator rejects bad loose globs.
- [x] GameHost mounting. Verified by: `ExportsATemplateGameForThisMachineAndRunsIt` extended (pack present, no
  `Assets/` or loose `project.mfproj`, runs headless, exits 0); `just template-smoke`.
- [x] Editor dialog rows. Verified by: `ExportEditorTests` for the Pack row and the Loose list.
- [x] Benchmark and export smoke. Verified by: `ContentLoadBenchmarks` runs; `just export-smoke` screenshots match.
- [x] Docs and decision. Verified by: `check.sh`; the proposal's phase 2 ticked with "Changes from the proposal, phase
  2"; `game-export.md` rows; `asset-pipeline.md`; ADR for the page layout.

## Outcome

Done (ADR 0247). Changes from the plan: the page layout became path-hash anchors (the first layout re-flowed every
later page; `InsertingAnAssetShiftsEntriesAfterTheNextAnchorByWholePages`); `mf-export --pack-list`/`--pack-extract`
were added for the template smoke; `just export-smoke` compares 8 of 12 scenes (the other 4 vary run to run). Log:
[2026-11-06 G19 asset packs](../logs/2026-11-06-g19-asset-packs.md).

## Done when

- The tests above pass with the gates, an exported template game ships `Assets.mfpak` and no `Assets/` folder and
  runs, and the proposal's phase 2 task is ticked.

## Open questions

- Brotli or a zstd/LZ4 package: the benchmark gives the numbers; the owner decides (no package added).
