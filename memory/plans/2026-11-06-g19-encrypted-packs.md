# Plan: G19 phase 3, encrypted packs

**Status:** done
**Docs:** [Protected exports](../../docs/design/protected-exports.md) (§ Encryption (phase 3), Scenarios → Phase 3),
[ADR 0239](../decisions/0239-protected-exports.md), [ADR 0240](../decisions/0240-committed-pack-key.md),
[Asset pipeline → Packs](../../docs/design/asset-pipeline.md#packs)
**Started:** 2026-11-06

## Goal

Exports encrypt `Assets.mfpak` (AES-256-CTR) with a key committed in `project.mfproj`, which the generator compiles into
the game, so a packed export's folder shows no file names or content. Builds on phase 2 (`feature/g19-asset-packs`).

## Not in scope

- NativeAOT (phase 4). Tamper resistance, DRM.

## Steps

- [x] `PackCipher` (CTR over ECB, 0 B reads), writer and reader encryption, key check, `PackKeyException`. Verified by:
  `EncryptedPackContainsNoPlaintextPathsOrContent`, `AnEncryptedPackRoundTripsAndSeeksWithItsKey` (0 B gate),
  `WrongKeyFailsAtMountWithAClearError`, `EncryptedPackIsDeterministicForAStableKey`,
  `ChangingOneAssetChangesOnlyItsEntryAndTheIndex`.
- [x] Format 6 `packKey`, migration, `PackKeyUnsaved`, exported file without the key. Verified by:
  `FormatSixMigrationAddsAPackKeyOnce`, `ABadPackKeyIsAnError`, the runner's exported project check.
- [x] Generator module initializer (MFG012), `MainframeGame.props`, `GameHost` running module constructors. Verified by:
  `APackKeyBuildPropertyBecomesAMaskedModuleInitializer`, `NoPackKeyEmitsNothingAndABadOneIsMfg012`, the integration
  export's run logging `(encrypted)`.
- [x] `encrypt` preset key, `MF_PACK_KEY`, validator, `--init-pack-key`, `--pack-list/--pack-extract --project`, the
  dialog row, the editor saving the key on open. Verified by: `ExportWithoutASavedPackKeyFailsWithAFix`,
  `PackKeyEnvironmentVariableOverridesTheProject`, `EncryptFalseWritesAPlainPack`,
  `InitPackKeySavesAKeyOnceAndListAndExtractUseIt`, `OpeningAnOlderProjectSavesItsNewPackKey`, `ExportEditorTests`.
- [x] Example projects get committed keys; template smoke and export smoke encrypted. Verified by: `just template-smoke`,
  `just export-smoke`.
- [x] Docs. Verified by: `check.sh`, the docs-site build.

## Outcome

Done. Changes from the plan: none beyond "Changes from the proposal, phase 3" in the proposal (top-level `packKey`,
SHA-256 mask, module constructors run by `GameHost`, the editor saving the key). Log:
[2026-11-06 G19 encrypted packs](../logs/2026-11-06-g19-encrypted-packs.md).

## Done when

- The tests above and the gates pass, and an exported template game ships an encrypted pack it reads with the
  compiled-in key.
