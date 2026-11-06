# Plan: Engine file extensions start with `mf`

**Status:** done, see [log](../logs/2026-11-06-mf-file-extensions.md)
**Docs:** [Asset pipeline](../../docs/design/asset-pipeline.md), [Scene serialization](../../docs/design/scene-serialization.md),
[Saves](../../docs/design/project-and-gamehost.md), ADR 0197 (the `Content/` → `Assets/` migration this copies)
**Started:** 2026-11-06

## Goal

Every file extension the engine, editor or video library defines starts with `mf`, and a test fails when a new one
does not. The owner approved this list:

| Old | New |
|---|---|
| `.mscene` | `.mfscene` |
| `.mres` | `.mfres` |
| `.mmusic` | `.mfmusic` |
| `.msave` | `.mfsave` |
| `.msong` | `.mfsong` |
| `.meta` | `.mfmeta` |
| `.probes` | `.mfprobes` |
| `x.slang.spvlock` | `x.slang.mfspvlock` |
| `x.slang.layout.json` | `x.slang.mflayout.json` |

Already compliant: `.mfproj`, `.mfpak`, `.mftimeline`, `.mfcap.json`, `.mftranscript.json`, `.mfshader`.
Design-only names in the proposals move too: `.mskel` → `.mfskel`, `.manim` → `.mfanim`, `.mmesh` → `.mfmesh`.

Old projects keep working. The runtime still reads the old names, so a game can update its package before anyone opens
it in the editor. The editor offers a one-click migration when it opens an old project, and declining leaves the project
closed (as ADR 0197 does). A player's old `.msave` files are renamed the first time the game starts.

## Not in scope

- Third-party formats (`.slang`, `.spv`, `.rml`, `.rcss`, `.po`/`.mo`/`.pot`, Spine `.atlas`/`.skel`, `.gdshader`,
  media), files .NET writes (`.crashreport.json`), and temporary or fixed names (`.tmp`, `.bakN`, `.renaming`,
  `.partial`, `.stems`, `saves/.sync.json`, `shaders.lock`, `~/.mainframe/`).
- History: `memory/logs/`, `memory/plans/`, `memory/context/progress-log.md` and older ADRs keep the names they had.
- Bumping the `project.mfproj` format (the G19 branch takes format 6). Old paths in a project file resolve through the
  same fallback as code.

## Steps

- [x] `FileExtensions` in the engine: one constant per engine format, the legacy map and `UpgradeLegacy`. The existing
  constants (`SceneFormat.SceneExtension`, `SaveFile.Extension`, `AssetDatabase.MetaExtension`, …) point at it.
  Verified by: unit tests for the map and `UpgradeLegacy`.
- [x] Runtime reads the old names: `ResourceLoader` dispatch, `AssetDatabase` (self-describing files, `.meta`
  sidecars, build outputs), `AssetUid` prefixes, `ContentPaths.Resolve` (a missing old path falls back to the new one),
  surface-shader layouts, export staging/validation, the l10n scene extractor. Verified by: tests that load a scene,
  a resource, a sidecar UID and a shader layout from a project that has only the old names.
- [x] Saves: `SaveServer` renames `x.msave`, `x.msave.tmp` and `x.msave.bakN` to the new names on start, unless the
  new file already exists. Verified by: a test that lists, loads and backs up a slot written as `.msave`.
- [x] Editor: new names everywhere, plus `FileExtensionMigration` (renames the files, rewrites the paths in the
  project's text files) and an open-time prompt chained after the `Assets/` check. Verified by: migration tests on a
  fixture project with every old extension, and `just qa-editor`.
- [x] Repo content: `git mv` every old-named file (Demo, Forest, Neon, template, test content) and rewrite references
  in code, scenes, resources, docs, the docs site, the template, `justfile`, build scripts, `.gitattributes`, AGENTS.md.
  Verified by: `git grep` for the old extensions finds only the legacy map, migration code, tests of both, and history.
- [x] The rule: ADR 0249, a line in AGENTS.md, a "File extensions" table in `asset-pipeline.md`, and
  `FileExtensionRuleTests` (a source scan, like `ContentIoAuditTests`) that fails on an extension constant without
  `.mf` that is not on its third-party list. Verified by: the test fails when a `.mfoo` constant is renamed to `.foo`.
- [x] Gates from `.agents/workflow.md`, plus `just test-render`, `just qa-editor`, `just template-smoke` and the Demo,
  Forest and Neon test suites. Verified by: each one passes.

## Done when

- `git grep -P '\.(mscene|mres|mmusic|msave|msong|probes|spvlock)\b'` outside history, the legacy map, migrations and
  their tests returns nothing.
- `FileExtensionRuleTests` passes, and fails on a non-`mf` engine extension.
- A project made with the old names loads in the runtime unchanged, and opens in the editor after migration.
- Every gate in `.agents/workflow.md` passes.

## Open questions

- None. The owner chose the full list on 2026-11-06.
