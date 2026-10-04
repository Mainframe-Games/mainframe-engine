# ADR 0062 — In-house `.mo` compiler (`mf-l10n`) instead of requiring GNU gettext

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M9 (lane m9)

## Context

GetText.NET reads only `.mo`. The proposal compiled `.po` with GNU `msgfmt` in an MSBuild target and fell back to
committed `.mo` files when gettext is missing (as shaders do with `glslc`). gettext is not installed by default on
Windows or macOS, and no additional NuGet package was approved for `.po` handling.

## Decision

- `Tools/MainframeEngine.L10n` (`mf-l10n`, net10.0 exe) implements `.po` parsing/writing and a `.mo` writer in C#
  that reproduces `msgfmt` byte for byte: revision 0, little-endian, messages sorted by msgid bytes (up to the first
  NUL), untranslated/fuzzy/obsolete dropped (fuzzy header kept), the `hashpjw` hash table (64-bit arithmetic as on
  LP64) with gettext's sizing (next odd prime ≥ 4n/3, minimum 3 — derived empirically against msgfmt 1.0 for 1–120
  messages) and double hashing.
- `build/Localization.targets` builds the tool through a project reference (`ReferenceOutputAssembly=false`,
  RID/self-contained properties removed) and runs it per `.po` into `obj/<Configuration>/locale`, incrementally; the
  output gets `Content/locale/**/<domain>.mo`. `-p:CompileLocales=false` (or a missing tool) ships the committed
  `.mo` files with warning MFL10N001. Translation errors (placeholders, plural counts) fail the build.
- `.mo` files are committed (`*.mo binary` in `.gitattributes`); `just l10n-compile` refreshes them and
  `mf-l10n check` (`just l10n-check`) fails when they are stale. `check --msgfmt` additionally runs `msgfmt -c` when it
  is on PATH and requires identical bytes; a unit test does the same (skipped without msgfmt).

## Consequences

- No external tools to build or run games; translators can still use any gettext tool, and GNU output is
  interchangeable with ours.
- The tool is also the editor's (M10) back end for extract/merge/coverage.
