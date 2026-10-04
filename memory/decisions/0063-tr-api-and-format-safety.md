# ADR 0063 — `Tr` API shape and format-safe placeholders

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M9 (lane m9)

## Context

The proposal sketched `Tr.Get(FormattableString)`, `Tr.Plural`, `Tr.Ctx`. The lane brief asked for `Tr._("…")`,
`Tr.P(ctx, …)`, `Tr.N(…)` with format-safe placeholders and zero-allocation cached lookups. The GetText.NET extractor
matches calls by method name (aliases `-as/-ad/-ap/-adp`) and turns interpolated strings into `{0}` msgids. A
translated format that does not match the call (`{1}` with one argument, `{0` typo, a specifier the argument type
rejects) makes `string.Format` throw.

## Decision

- Names: `Tr._` (gettext), `Tr.P` (pgettext), `Tr.N` (ngettext), `Tr.NP` (npgettext); extractor aliases
  `-as _ -ad P -ap N -adp NP`. `CA1707` is suppressed on `Tr` with a justification.
- Overloads **without** arguments return the translation as is (no formatting, no allocation). Overloads **with**
  arguments format with `Tr.Culture` through cached `CompositeFormat`s; generic `<T0..T2>` overloads use
  `string.Format<T…>` (no boxing), `params ReadOnlySpan<object?>` covers more.
- Plurals: `n` is always `{0}`, extra arguments start at `{1}`; untranslated or empty forms fall back to
  singular/plural by `n == 1`.
- Safety: an invalid translated format, one needing more arguments than given, or one an argument rejects falls back
  to the source format (warning logged once per message); a broken source format is returned unformatted. `Tr` never
  throws for bad catalog content. `mf-l10n compile` rejects such translations up front (errors with file and line).
- `TrInterpolatedStringHandler` overloads make `Tr._($"Score: {score}")` look up `"Score: {0}"` (alignment/format kept,
  literal braces re-escaped), matching the extractor; documented as allocating.

## Consequences

- Game code reads like gettext C code; the extractor needs no custom parser.
- A constant interpolated string with escaped braces and no holes compiles to a plain string, so its runtime msgid
  differs from the extracted one (documented known issue).
