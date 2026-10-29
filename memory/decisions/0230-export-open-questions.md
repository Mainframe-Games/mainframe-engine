# ADR 0230 — Game export: the proposal's open questions

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/game-export.md

## Context

The G5 proposal (docs/design/game-export.md, formerly future/) left six questions open. Each takes the proposal's
recommendation where it gave one, else the option that adds nothing new until someone needs it.

## Decision

- **macOS signing from Windows/Linux: "macOS needs a Mac" stays.** A managed ad-hoc Mach-O signer is code-signature
  format work with no user asking for it; macOS presets fail validation off a Mac (`host-unsupported`), and the fix text
  says to export on a Mac (or a macOS CI runner).
- **Single file is never the default.** It needs the natives self-extracted, slows the first start, and cannot live in a
  macOS bundle. It stays an opt-in, experimental key.
- **No version stamping of assemblies.** `-p:Version` as a global property would stamp the engine's assemblies too, and a
  game-only property is not needed yet: the version names the archive, fills the macOS bundle version and is written
  into the exported `project.mfproj` (`ProjectSettings.Version`), which the game can read.
- **Presets stay in `project.mfproj`**, as the proposal and mobile.md chose: one file, one format version, one undo
  history in the editor. A separate `export.mfexport` can be added later if teams need machine-specific paths out of
  version control (output paths are project-relative by default; signing identities are names, not secrets).
- **No per-preset Steam app id.** `steam: true|false` with the ids in the `steam` section; `ProjectSettings.SteamAppId`
  already chooses the demo id for demo builds. A playtest app id can be added as an optional key when someone needs it.
- **No desktop content packs (`.mfpak`) now.** Desktop keeps shipping source files (ADR 0011) with the include/exclude
  globs; revisit after M12.1 defines the pack format.

## Consequences

- Nothing in the format anticipates these; each can be added as an optional key or a new file without a format bump
  beyond what M12.7 already plans.
