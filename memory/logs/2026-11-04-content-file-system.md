---
summary: >-
  G19 phase 1: every runtime content read in MainframeEngine/Src goes through ContentFiles, a read-only file system
  with mounts anchored at directories; no behaviour change without a mount. Proposal and ADRs 0239/0240 for the rest.
---

# 2026-11-04: Content file system (G19 phase 1)

Branch `feature/protected-exports`. Plan: [Content file system](../plans/2026-11-04-content-file-system.md).
Proposal: [Protected exports](../../docs/design/protected-exports.md) (accepted; phases 2–4 to come).

## What changed

- The proposal for protected exports (G19): content packs (`Assets.mfpak`), AES-256-CTR encryption with a per-project
  key, and NativeAOT for code. Deterrence only: the key ships in the game. G19 row in `docs/milestones.md`.
- `MainframeEngine/Src/Core/Content/`: `ContentFiles` (`Mount`, `Exists`, `DirectoryExists`, `GetInfo`, `OpenRead`,
  `ReadAllBytes`, `ReadAllText`, `EnumerateFiles`, `EnumerateDirectories`), `IContentSource`, and the internal
  `MemoryContentSource`. A mount serves the absolute paths under its root; lookups ask mounts by priority, then the
  disk. With nothing mounted every call is the `File`/`Directory` call it replaced, paths included.
- Every runtime content read swapped: resources, project settings, shaders, textures, sky, canvas and spatial
  shaders, light probes, terrain and tree reads, fonts, RmlUi's file interface, audio streams, music, catalogs.
- Native readers: Assimp imports through an `aiFileIO` bridge (`AssimpContentIO`) only when the model lies under a
  mount; Spine reads through the runtime's stream overloads (`SpineContent`); RmlUi's `LoadFontFace(path)` already
  reads through the managed file interface (shown by `UiDocumentAndFontLoadFromAMount`).
- Disk-only reads carry `// disk-io: <reason>`; `ContentIoAuditTests.RuntimeContentCodeDoesNotCallFileApis` fails on
  any other `File`/`Directory` read in `MainframeEngine/Src`.
- Docs: asset pipeline ("Content file system"), `project-and-gamehost.md`, AGENTS.md.

## Decisions

- [ADR 0239](../decisions/0239-protected-exports.md): encrypted content packs; NativeAOT later instead of an IL
  obfuscator.
- [ADR 0240](../decisions/0240-committed-pack-key.md): one pack key per project, committed in `project.mfproj`
  (`MF_PACK_KEY` overrides). `pack` and `encrypt` default on.
- Mounts are anchored at a directory instead of converting the engine to content paths: callers keep their absolute
  paths. No `IContentFileSystem`/`ContentFileSystem` layer yet; mounts are the extension point (mobile can add one).

## Dead ends

- Resolving relative paths through `ContentPaths` inside `ContentFiles` broke `just shaders-check` (20 shaders
  reported stale): `mf-shaders check` passes repo-relative paths. Relative paths now resolve against the working
  directory, as `File` does; callers resolve `"Assets/…"` with `ContentPaths.Resolve` first.
- ADRs were first numbered 0238/0239; main took 0238 (editor async loading) meanwhile, so they became 0239/0240.

## Verified

- After rebasing on main (#162–#164): `just build`, Release `-warnaserror`, `just test` (3268 engine, 1811 editor,
  460 video; new: `ContentFilesTests`, the mount tests, the audit), `just format-check`, `just shaders-check`,
  `check.sh`.
- `just test-render` on an RTX 3070 Ti: 227 passed, validation and allocation gates green (no NVIDIA goldens, so no
  pixel comparison). `just render-tests-linux`: 227 passed against the `lavapipe` goldens, none re-recorded.
- `just test-linux` (case-sensitive file system): 3264 / 1809 / 460 passed. `just template-smoke`: built, ran, exported
  and ran the export headless.
- The audit fails when a `File.ReadAllBytes` is added to `Texture2D` (checked by hand, reverted).
- `just demo-screenshots` before the rebase: the scenes render as before (spot-checked Spine and shaders).

## Not verified

- `moltenvk` goldens (a Mac).
- Nothing mounts a source outside tests yet, so no export reads through a mount until phase 2.

## Next

- Phase 2: the `Assets.mfpak` format, `PackContentSource`, `GameHost` mounting, export presets.
- Phase 2 open question: warn in Debug when a path matches the disk only case-insensitively (packs are case-sensitive).
