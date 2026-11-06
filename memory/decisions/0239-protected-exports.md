# ADR 0239 — Exports ship encrypted content packs; code is protected by NativeAOT, not an IL obfuscator

- **Date:** 2026-11-04
- **Status:** accepted
- **Docs:** docs/design/protected-exports.md (G19), docs/design/game-export.md,
  docs/design/future/mobile.md#asset-pipeline

## Context

An export ships `Assets/` as loose files and the game as a managed assembly. ILSpy, or an AI agent pointed at the
install folder, recovers the game's data and near-source code in minutes. The owner wants every game made with the
engine protected from that low-effort copying, without each game doing its own work. The decryption key has to ship
with the game, so this is deterrence, not secrecy.

## Decision

- **Assets.** Every runtime content read goes through one content file system. Exports pack the game's and the
  engine's `Assets/` and `project.mfproj` into `Assets.mfpak`, the format mobile already planned, encrypted with
  AES-256-CTR.
- **Code.** Protect it with NativeAOT (a `nativeAot` preset key), as the proposal's last phase, once the engine is trim-
  and AOT-clean. Until then the code stays IL.
- **Defaults.** `pack: true` and `encrypt: true`, so a game is protected without opting in. Games that want moddable
  files set `pack: false` or list them in `loose`.

## Ruled out

- **An IL obfuscator** (Obfuscar, ConfuserEx forks). It is a new dependency, and renaming breaks every name-based
  contract: `[Export]`, `[Signal]`, `[Rpc]`/`[Replicated]`, scene type names and save schemas. NativeAOT removes the IL
  instead.
- **NativeAOT first.** It protects the biggest leak sooner, but it waits on making the whole engine AOT-clean (no
  analyzers run today). Packs protect assets now, and mobile needs the content seam anyway.
- **Opt-in protection.** Most games would ship unprotected.
- **Authenticated encryption (AES-GCM).** It needs a whole entry decrypted to verify it, so audio streaming could not
  seek. Tamper resistance is not a goal.

## Consequences

- Runtime code must not read content with `File.*`. An audit test enforces it.
- Packed exports can break where loose builds worked: case-mismatched paths on case-insensitive file systems, or game
  code that reads `Assets/` with `File.*` itself.
- Mobile's M12.1 content seam and M12.5 pack format are built on desktop first.
- Until NativeAOT ships, the key is recoverable from the game's IL.

## Revisit when

- The engine passes the AOT analyzers: phase 4 can start, or M12.6 takes it.
- Players or modders need a supported mod-loading path beyond `pack: false` and `loose`.
