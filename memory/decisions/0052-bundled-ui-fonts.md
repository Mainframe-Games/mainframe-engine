# ADR 0052 — Bundled UI fonts: Lato Latin and Roboto Mono (OFL)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M8 (W4 lane m8)

## Context

RmlUi has no system-font fallback: every document needs a loaded font face. The task suggested an OFL font such as a
Noto Sans or Inter subset. No network access is assumed in the build; the RmlUi submodule already vendors OFL fonts
used by its samples.

## Decision

Bundle **Lato Latin** (regular, bold, italic; `font-family: LatoLatin`) and **Roboto Mono** regular
(`"Roboto Mono"`) from RmlUi 6.3's `Samples/assets`, in `MainframeEngine/Content/UI/fonts/` with their SIL OFL 1.1
licence texts (`OFL-Lato.txt`, `OFL-RobotoMono.txt`; the Roboto Mono copyright line added from the upstream project).
The UI server loads every `.ttf`/`.otf` in that folder at start-up. `*.ttf`/`*.otf` are marked binary in
`.gitattributes` (not LFS: ~540 KB total, needed by every checkout's tests).

Lato Latin covers Latin-1 and Latin Extended (accents, `Æ`, `Œ`); Roboto Mono gives tabular digits for stats.

## Consequences

- No new download or package; provenance is the pinned RmlUi submodule.
- Symbols outside Latin (e.g. `♥`, `✓`, CJK) are missing; games add fallback faces with `UiServer.LoadFonts` or
  `RmlCore.LoadFontFace(path, fallbackFace: true)` (M9 will need CJK fallbacks).
- THIRD_PARTY_NOTICES lists both fonts.
