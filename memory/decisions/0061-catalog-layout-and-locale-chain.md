# ADR 0061 — Catalog layout, locale ids and the fallback chain

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M9 (lane m9)

## Context

The proposal placed catalogs at `Content/Locale/{locale}/messages.mo` (no `LC_MESSAGES`) and relied on GetText.NET's
`pt_BR → pt-BR → pt` probing. The lane brief asked for `Content/locale/<lang>/LC_MESSAGES/<domain>.mo` via
`ContentPaths` and a chain such as `pt_BR → pt → en`. Folder names are case-sensitive on Linux.

## Decision

- Layout: `{LocalizationOptions.LocaleDirectory}/{locale}/LC_MESSAGES/{Domain}.mo`, defaults `Content/locale` and
  `messages`, resolved through `ContentPaths` — the standard gettext layout every tool understands. `.po` sources and
  the `messages.pot` template live in the same tree.
- Locale ids are gettext style, normalised by `LocaleId.Normalize` (`pt-br`, `pt_BR.UTF-8@euro` → `pt_BR`;
  `zh-hans-cn` → `zh_Hans_CN`); .NET names are accepted everywhere.
- Chain: the locale, its parents (`zh_Hans_CN → zh_Hans → zh`), `FallbackLocales` (each with parents), then
  `SourceLocale` (default `en`, needs no catalog). First catalog with the message wins.
- Startup: `EngineOptions.Locale` if catalogs exist for it, else the OS UI language if catalogs exist for it or its
  language, else the source locale. The source language never logs missing translations.
- `Tr.Culture` formats `Tr` messages; the process `CurrentCulture`/`CurrentUICulture` are **not** changed (the proposal
  set both to keep `CatalogManager` consistent, which we do not use, and changing them breaks culture-sensitive
  parsing elsewhere).

## Consequences

- Catalogs work unchanged with Poedit/Weblate/msgfmt. Games pick fallbacks per project (`ca → es`).
- Games that want `ToString()` in the player's culture set `CultureInfo.CurrentCulture = Tr.Culture` themselves.
