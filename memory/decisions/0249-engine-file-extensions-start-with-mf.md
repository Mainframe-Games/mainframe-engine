# ADR 0249 — Engine file extensions start with `mf`

- **Date:** 2026-11-06
- **Status:** accepted
- **Docs:** docs/design/asset-pipeline.md#file-extensions, memory/plans/2026-11-06-mf-file-extensions.md

## Context

The engine's own formats had grown two naming styles: `.mfproj`, `.mfpak`, `.mftimeline` and `.mfcap.json` started
with `mf`, while `.mscene`, `.mres`, `.mmusic`, `.msong` and `.msave` had a lone `m`, and the sidecars (`.meta`,
`.probes`, `x.slang.spvlock`, `x.slang.layout.json`) had no prefix at all. `.meta` is also Unity's sidecar name, so a
Mainframe project and a Unity one can look alike. The owner asked for one rule.

## Decision

- **Every file extension the engine, editor, video library or tools define starts with `.mf`.** The constants live in
  `FileExtensions` (`MainframeEngine/Src/Resources/FileExtensions.cs`); the older per-type constants point at it.
- **Renamed:** `.mscene` → `.mfscene`, `.mres` → `.mfres`, `.mmusic` → `.mfmusic`, `.msong` → `.mfsong`,
  `.msave` → `.mfsave`, `.meta` → `.mfmeta`, `.probes` → `.mfprobes`, `x.slang.spvlock` → `x.slang.mfspvlock`,
  `x.slang.layout.json` → `x.slang.mflayout.json`. The proposals' `.mskel`, `.manim` and `.mmesh` become `.mfskel`,
  `.mfanim` and `.mfmesh`.
- **Out of the rule:** third-party formats (`.slang`, `.spv`, `.rml`, `.po`, Spine's `.atlas`, media), files .NET
  writes, and temporary or fixed names (`.tmp`, `.bakN`, `.partial`, `.stems`, `shaders.lock`, `saves/.sync.json`).
- **Old projects keep working.** The runtime reads the legacy names: `FileExtensions.Matches` for loader dispatch,
  `FindExisting` for sidecars, and `Upgrade` in `AssetDatabase.ToAbsolutePath` and `ContentPaths.Resolve`, so an old path
  in code or `project.mfproj` finds a renamed file. `SaveServer` renames a player's `slot.msave` (and its `.tmp`,
  `.bakN` and `.corrupt-…` copies) once at start. The editor offers `FileExtensionMigration` when it opens an old
  project (after the `Assets/` check, ADR 0197), and declining leaves the project closed. The editor writes only the
  current names.
- **Enforced** by `FileExtensionRuleTests`: a source scan of every `const string …Extension…`/`…Suffix…` that fails on a
  value without `.mf` unless it is on the test's exempt list with a reason.
- **No project format bump.** Old paths in a project file resolve through the same fallback as code, and the G19 branch
  holds format 6.

## Ruled out

- **Leaving `.meta` and the shader sidecars alone.** Fewer files would churn (389 sidecars in the repo), but the rule
  would need exceptions for the engine's own formats, and the migration code is the same either way.
- **A clean break with no legacy reads.** A game that updates its `MainframeEngine` package without opening the editor
  would stop loading, and players would lose sight of their saves.
- **Bumping `project.mfproj` and migrating there.** The extensions are on files, not in the project format, and the
  bump would collide with G19's format 6.

## Consequences

- One prefix to search for and to explain. A new format gets an `.mf` name, or the test says why not.
- Branches open at the time (G19 phase 3, navigation avoidance, world-space UI, G6 follow-ups) touch renamed files and
  need a rebase. Git follows the renames; lines naming the old extensions conflict.
- The legacy reads are code to keep: `FileExtensions.Legacy`, the fallbacks in `AssetDatabase`/`ContentPaths`, the
  save rename and `FileExtensionMigration`.
- Game code that writes sidecars by hand (`png + ".meta"`) is not rewritten by the migration; it should use
  `AssetDatabase.MetaExtension`.

## Revisit when

- No supported project or shipped game uses the legacy names any more: drop the legacy reads and the migration.
