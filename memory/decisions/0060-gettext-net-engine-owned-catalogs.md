# ADR 0060 — GetText.NET for parsing, engine-owned catalogs for lookups

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M9 (lane m9)

## Context

The approved localization package is `GetText.NET` 10.0.1 (MIT, fork of NGettext; 11.0 is a preview). Inspecting the
assembly showed `Catalog(ILoader, CultureInfo)`, `MoAstPluralLoader` (evaluates the `.mo` file's `Plural-Forms`),
`Catalog.Translations` (`Dictionary<string, string[]>`: header under `""`, contexts as `ctx\u0004id`, plurals keyed by
the singular) and `Catalog.PluralRule`. `Catalog.GetString` allocates 24 B per call (measured), context lookups
concatenate the key, and `CatalogManager` keys its cache on `CurrentCulture` while `Catalog` uses
`CurrentUICulture`. The engine requires allocation-free steady-state frames.

## Decision

- Pin `GetText.NET` `[10.0.1]` in `Directory.Packages.props`. Use it only to parse `.mo` files and build plural rules:
  `new Catalog(new MoAstPluralLoader(stream), culture)`, then copy `Translations` + `PluralRule` into our own
  `TranslationSet`.
- `TranslationSet` merges the catalogs of one locale chain into one `Dictionary<string, TranslationEntry>`
  (ordinal) with a `ReadOnlySpan<char>` alternate lookup; contexts are composed on the stack. Sets are immutable and
  swapped atomically by `Tr.SetLocale`, so lookups are lock-free and allocation-free.
- Each entry keeps its catalog's plural rule (a `pt` message found while `pt_BR` is current uses `pt`'s rule) and
  lazily caches a `CompositeFormat` per form.
- `CatalogManager` and `Catalog.GetString*` are not used.

## Consequences

- Hit 3.6 ns, miss 2.9 ns, context hit 15 ns, all 0 B (BenchmarkDotNet); the render-test allocation gate covers it.
- Upgrading GetText.NET only affects `.mo` parsing and plural evaluation (`TranslationSet.Merge`).
- A switch rebuilds the merged dictionary (~2 ms per 2 000 messages); acceptable for a menu action.
