# ADR 0066 — The `qps` pseudo-locale

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M9 (lane m9)

## Context

The proposal asked for a generated pseudo-locale (accented, ~30% longer, bracketed) selectable at run time. The
Sandbox's only UI today is ImGui with its built-in font, which covers Latin-1 only; characters outside it render as
`?`.

## Decision

- `mf-l10n pseudo` writes a complete `qps` catalog from the template (`Language: qps`, two plural forms; both forms
  transformed). Letters map to accented look-alikes, `ceil(letters × expansion)` padding characters (`" ~~"`) are added
  inside `[` `]`; format items, escaped braces, `{{ data }}`, `%s` sequences and outer whitespace are kept.
- `--charset full` (default) maps every ASCII letter (Latin Extended); `--charset latin1` only uses Latin-1 accents.
  The Sandbox uses `latin1` so the ImGui overlay stays readable; game UIs with real fonts use `full`.
- `qps` is a normal catalog folder: `Tr.GetAvailableLocales()` offers it; `LocaleId.GetCulture("qps")` formats as `en`.
  `update` skips it (it is regenerated, never translated).

## Consequences

- Untranslated (hard-coded) strings stand out without brackets; truncation shows as clipped brackets/padding.
- Regenerating `qps` after every extraction keeps it complete (`just l10n-extract`).
