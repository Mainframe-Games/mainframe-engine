---
summary: >-
  G19 phase 3: exports encrypt Assets.mfpak with AES-256-CTR and the project's committed packKey (format 6), which
  the generator compiles, masked, into a module initializer of the game; the editor and mf-export --init-pack-key save
  the key; MF_PACK_KEY overrides it. NativeAOT (phase 4) remains.
---

# 2026-11-06: G19 phase 3, encrypted packs

Branch `feature/g19-encrypted-packs`, on top of `feature/g19-asset-packs` (PR #178). Plan:
[G19 encrypted packs](../plans/2026-11-06-g19-encrypted-packs.md). Design:
[Protected exports](../../docs/design/future/protected-exports.md) (phase 3 ticked, "Changes from the proposal, phase 3"),
[Asset pipeline → Packs](../../docs/design/asset-pipeline.md#packs), [Game export → Packed content](../../docs/design/game-export.md#packed-content).

## What changed

- **Engine:**
  - `PackCipher` (AES-256-CTR, keystream from ECB through one reused `ICryptoTransform`, 0 B per read).
  - `PackKeyException` (`PackKeyMissing`, `PackKeyMismatch`) and `PackKeys` (registration, `NewKey`, `TryParse`).
  - `PackWriter.Write(…, key)` and `PackReader.Open(path, key)`, which encrypt and decrypt the entries and the index and
    check the key check.
  - `PackContentSource.MountBeside(…, key)`.
  - `GameHost.Run` runs the game assemblies' module initializers before mounting, and its log says `(encrypted)`.
- **Project:** format 6 with a top-level `packKey` (step 5 → 6 generates it). `PackKeyUnsaved` marks a key not yet
  saved. The exported project file drops it. The Demo, Neon and Forest have committed keys (added as one line, so
  their hand-written files keep their layout). The template stays format 5 without a key.
- **Generator:** `PackKeyEmitter` (a `[ModuleInitializer]` with the key XOR SHA-256 of the assembly name) and
  diagnostic MFG012. `MainframeGame.props` makes `MainframePackKey` compiler-visible.
- **Export:**
  - The `encrypt` preset key (default true) and `MF_PACK_KEY`.
  - `-p:MainframePackKey` on the publish, printed as `***`.
  - Validator codes `pack-key-missing` and `pack-key-invalid`.
  - `mf-export --init-pack-key`, and `--pack-list`/`--pack-extract --project`.
  - The dialog's Encrypt Pack row. The editor saves a generated key when it opens a project.
- **Checks:**
  - `PackTests` (6 more), format tests and generator tests.
  - Runner tests: env override, `encrypt: false`, no key bytes or base64 in any exported file.
  - CLI and validator tests, and an editor open-saves-key test.
  - The integration export is encrypted and its run logs `(encrypted)`.
  - Template smoke saves a key and checks the pack can't be listed without it.
  - Export smoke checks every scene ran encrypted.
  - `ContentLoadBenchmarks` has an `Encrypted` case.
- **Docs:** asset-pipeline, game-export, project-and-gamehost, scene-serialization (MFG012), editor,
  protected-exports, milestones, AGENTS.md, README, landing page.

## Decisions

- No new ADR: ADR 0240 holds. The top-level `packKey` (since `export` is an array) and the deterministic mask are
  recorded in the proposal's "Changes from the proposal, phase 3".

## Verified

- `just build`, the Release build (0 warnings), `just format-check`, `just shaders-check`, `check.sh`.
- `ExportsATemplateGameForThisMachineAndRunsIt` (real SDK): the encrypted export runs headless with the compiled-in key.
- `just template-smoke`: passed. `just export-smoke`: all 12 scenes ran from the encrypted pack, and the 8 compared
  ones match the loose build's screenshots exactly.
- Unit and render test results are in the PR.

## Not done

- Phase 4 (NativeAOT): until then the key can be recovered from the IL.
- The human checklist (ILSpy and agent pass, Steam depot patch size, music streaming and seeking from an encrypted pack
  on real hardware).
