# ADR 0197 — The content folder is `Assets/`

- **Date:** 2026-10-19
- **Status:** accepted
- **Milestone:** —
- **Docs:** docs/design/asset-pipeline.md, docs/design/project-and-gamehost.md, docs/design/editor.md

## Context

A game's files (scenes, resources, UI, audio) lived in `Content/`, and so did the engine's and the editor's own files
(shaders, widgets, fonts, icons). All of them merge into one `<output>/Content/` at run time, and paths name the
folder: `"Content/Scenes/Main.mscene"`, `/Content/UI/widgets/widgets.rcss`. People coming from Unity know that folder
as `Assets/`, and the editor's FileSystem panel is getting a Simplified view whose two roots are Assets and Scripts.

## Decision

- **One rename, everywhere.** Game projects, the engine (`MainframeEngine/Assets`), the editor
  (`MainframeEngine.Editor/Assets`), the tests' shared assets and the template all use `Assets/`. The build output is
  `<output>/Assets/`, and every path string says `Assets/…`. There is still one namespace for engine and game files.
- **API names stay.** `ContentPaths` (`FolderName` is now `"Assets"`), `AssetDatabase.ContentDirectory`, and the
  MSBuild properties `MainframeContentSource` and `LocaleContentRoot` keep their names, so game code and csprojs that
  use them still compile. Only the values change.
- **Old paths still load.** `ContentPaths.UpgradeLegacyPath` reads `Content/…` as `Assets/…`. `ContentPaths.Resolve`,
  the RmlUi file interface and `AssetDatabase.ToAbsolutePath` all use it. A path that merely starts with the word
  (`ContentPacks/…`) is unaffected.
- **`project.mfproj` format 4.** The 3 → 4 step rewrites every `Content/…` string value; a bare `"Content"`, such as a
  game's name, stays.
- **The editor migrates old projects.** Opening a project that has `Content/` and no `Assets/` asks first: **Rename
  and Open** or **Cancel**. Rename moves the folder and rewrites `Content/` and `Content\` path prefixes in the
  project's text files (`.mscene`, `.mres`, `.mmusic`, `.msong`, `.rml`, `.rcss`, `.json`, `.po`/`.pot`, `.mfproj`,
  `.cs`, MSBuild files). In MSBuild files it also rewrites the bare folder name (`LinkBase="Content"`, `../Content<`).
  Build output and tool folders are skipped. `.meta` sidecars hold only UIDs and need no change. Cancel leaves the
  project closed, because the editor's asset database scans only `Assets/`.

## Alternatives considered

- **Rename only the game folder.** Engine files would stay under `Content/` while game files moved to `Assets/`,
  leaving two roots at run time and two prefixes in game RML (`/Content/UI/widgets/…` next to `/Assets/UI/demo.rcss`).
- **Rename only what the editor shows.** The disk and the paths in files would no longer match the UI.
- **Rename the APIs too (`AssetPaths`).** This would break every game's code and csproj for a name, with no change in
  behaviour.
- **Open old projects unmigrated.** The asset database would be empty, so UID references would fail to resolve and
  the editor would be half-broken.

## Consequences

- Games built with an older engine are unaffected: they ship their own engine and their own `Content/` output.
- A game's C# that builds the folder name itself (`Path.Combine(root, "Content")`) is not rewritten. The migration
  rewrites only path prefixes in code, so a `"Content"` label stays. The log lists every file the migration changed.
