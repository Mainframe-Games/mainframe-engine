# ADR 0064 — RML translation contract for the game UI

- **Date:** 2026-10-05
- **Status:** accepted; wired into `UiServer` when M8 and M9 were integrated
- **Milestone:** M9 (lane m9), consumed by M8

## Context

RmlUi calls `SystemInterface::TranslateString(out, in)` with the raw text of each text node — no element, no
attributes — and parses a translation containing `<` as RML. The proposal wanted Godot-style exact-match
auto-translation with a `class="no-tr"` opt-out, plus `title`/`placeholder`/`value` attributes. M8 (built in
parallel) exposes `UiServer.Translator` as `delegate bool UiTranslator(ReadOnlySpan<byte> utf8, RmlStringSink output)`
and serves documents through `UiFileInterface`; this lane must not touch `MainframeEngine/Src/UI/`.

## Decision

- One parser, `RmlLocalization.Scan`, for extraction (`mf-l10n`) and runtime, so msgids always match. A msgid is
  the run's text with entities decoded, whitespace collapsed and trimmed; runs without a letter outside `{{ … }}` are
  skipped; `style`/`script`/`textarea` content, `head` (except its `title`), comments and CDATA are ignored.
- Text nodes: `Tr.TranslateMarkup(utf8 | string, out string)` — normalise, look up, return the translation
  entity-encoded (translations can never inject markup) with the run's outer whitespace kept as one space.
- `no-tr`: `RmlLocalization.PrepareDocument(source)`, run by the UI file interface on every `.rml`, prefixes text inside
  opted-out elements with U+FDD0 (a noncharacter), which `TranslateMarkup` strips and reports as handled without
  translating. The same pass translates `title`, `placeholder` and `value` of submit/button inputs, which RmlUi never
  sends to `TranslateString`.
- `ITextTranslator`/`TextTranslator.Current` package these for injection; on `Tr.LocaleChanged` the UI server loads
  `FontFallbackTable` faces for `Tr.LocaleChain` and reloads documents. `FontFallbackTable` is a `.mres` resource
  (`Content/locale/fonts.mres` by convention).
- No contexts or plurals in RML text: such strings come from C# through data models.

## Consequences

- Exact `no-tr` semantics without changing the native shim. Wired at integration (docs/design/localization.md#game-ui-rmlui):
  `UiServerOptions.TextTranslator` (default `TextTranslator.Current`) feeds `UiFileInterface.DocumentPreprocessor`
  and `UiServer.Translator`; the locale change is applied at the start of `UiServer.Process` (never inside an RmlUi
  callback). Found then: RmlUi also sends data-bound text *after substitution* (`Salud 72`) through `TranslateString` on
  every change — each new value was looked up, logged as missing and allocated, and a value equal to a msgid would be
  translated. The server translates a `{{ … }}` template once and returns it marked with U+FDD0; the data view copies
  the marker into every substituted text, which is returned unmarked without a lookup.
- Inline markup splits sentences into several messages, as RmlUi translates per text node; translators see each
  fragment with an `RML <element>` comment.
