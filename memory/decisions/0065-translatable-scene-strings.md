# ADR 0065 — Translatable scene strings and re-translation on locale change

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M9 (lane m9)

## Context

Scene files store node properties as JSON through generated accessors (ADR 0012). Player-facing text in scenes must
be extracted for translators, shown translated, and refreshed when the language changes, while files keep the source
text. The extractor cannot load scenes semantically without knowing which properties are text.

## Decision

- `[Export(Translatable = true)]` on `string`, `string[]` or `List<string>` members (anything else: generator error
  MFG010) sets `ExportHints.Translatable`; `NodeTypeInfo.TranslatableProperties` lists them, base types first.
- The property value stays the msgid. Nodes display it through `Node.Atr(text, context)` / `AtrN`, which honour
  `Node.AutoTranslateMode` (`Inherit`/`Always`/`Disabled`, exported on `Node`, Godot's `auto_translate_mode`), in
  `OnReady` and in the new `protected virtual Node.OnLocaleChanged()`. Changing the mode re-runs `OnLocaleChanged` on
  the subtree.
- `Tr` tracks live `SceneTree`s weakly; `SetLocale` makes each propagate `OnLocaleChanged` (parents first) and raise
  `SceneTree.LocaleChanged` — immediately on the tree's own thread when idle, at the end of the frame (deferred call)
  while ticking, and at the start of the next `Tick` when called from another thread.
- `mf-l10n extract --scenes` walks `.mscene`/`.mres` JSON (nodes, nested-instance props and overrides typed through the
  instanced file, inline resources) and asks `TypeRegistry` which properties are translatable, loading game assemblies
  given with `--assembly` (their module initializers register their types); `--translatable Type.Property` adds more.
  References carry the JSON line of each value.

## Consequences

- No generated per-property translation code: nodes decide how they display text, and UI nodes (M8) follow the same
  pattern. Saving never writes translations.
- Extraction needs the game built first (`just l10n-extract` builds the Sandbox); unknown types are reported.
