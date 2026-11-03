# ADR 0240 — One committed pack key per project

- **Date:** 2026-11-04
- **Status:** accepted
- **Docs:** docs/design/future/protected-exports.md#encryption-phase-3, [ADR 0239](0239-protected-exports.md)

## Context

Encrypted packs (ADR 0239) need a key. It ships inside every copy of the game, so keeping it from the repository
protects nothing. What does matter is that rebuilds reproduce, and that Steam patches stay small when one asset
changes.

## Decision

- Each project has one 32-byte pack key, stored as `export.packKey` (base64) in `project.mfproj` and committed with it.
  Project format 6's migration generates it once.
- `MF_PACK_KEY` overrides it, like `MF_SIGN_IDENTITY`.
- The key is compiled into the game assembly by the generator, never the engine. It is never written into the
  runtimeconfig, an AppContext switch, or the exported `project.mfproj`.
- Each entry's nonce is derived from its path and content, so a stable key gives byte-identical packs for unchanged
  content.

## Ruled out

- **A fresh key per build.** Every patch would re-download the whole pack, and builds would not reproduce.
- **An uncommitted local secret** (`.mainframe/`). CI and other machines would need it supplied, for no gain: the key
  ships in the game.

## Consequences

- Anyone with the project's repository has the key. That matches the threat model: anyone with the game has it too.
- Projects saved before format 6 cannot export encrypted until the key is saved (`mf-export --init-pack-key`, or open
  the project in the editor).

## Revisit when

- Per-build or per-customer keys are needed (for example watermarked review builds).
