# Proposal: "Download Demo Project" in the Project Manager

**Milestone:** [Demo & polish](../../milestones.md#demo--polish-) (D4, last) · **Status:** ✅ done ·
**Depends on:** [Demo project](demo-project.md) (the thing downloaded), [Release](../release.md) (`publish.yml`) ·
**Related:** [Project icons](project-icons.md)

## Problem

A new user of a released editor has no game to look at: New Project creates a near-empty template, and the Demo lives
only in the source repository. Godot's Project Manager offers an asset-library demo download; ours should offer the
[Demo](demo-project.md) in one click.

## Goals

- A **Download Demo Project** button in the Project Manager that fetches the Demo matching the editor's version,
  unpacks it into a folder the user chooses, points it at the engine, adds it to Recent Projects and opens it.
- No new NuGet packages (BCL `HttpClient` + `System.IO.Compression`).
- Works from a release build and from a development build.

## Non-goals

- A general asset/template library.
- Downloading or installing the engine itself. Like New Project, the Demo builds against an engine checkout
  (`TemplateLocator.FindEngineCheckout`, `MAINFRAME_ENGINE_PATH`); NuGet distribution is
  [a separate proposal](distribution-nuget.md).
- Updating an already-downloaded Demo in place.

## Release side

`publish.yml` gains a step after the editor packages are built and before `gh release create` (line 128):

1. `git lfs pull --include="Examples/Demo/**"` (the job otherwise excludes `Examples/**`, lines 72/83).
2. Zip `Examples/Demo` without `bin/`, `obj/`, `.vs/`, `*.user` → `release/MainframeEngine.Demo-v${VERSION}.zip`, with
   the top-level folder inside the zip named `MainframeEngine.Demo/`.
3. Copy it to `release/MainframeEngine.Demo.zip` (stable name for "latest").
4. Both files are attached by the existing `gh release create "v${VERSION}" release/*`.

A `build/package-demo.sh` script does steps 2–3 so `just publish-local` can produce the same zip locally.
`docs/design/release.md` lists the new assets.

## Editor side

### UI

- An icon button (download glyph, tooltip "Download Demo Project" — icons over text, per editor convention) in the
  Project Manager toolbar next to New / Open Folder (`project_manager.rml`, `ProjectManager.cs:55-78` events).
- It opens a **Download Demo** dialog (`Content/Editor/download_demo.rml`, `Src/UI/DownloadDemoDialog.cs`, built like
  `NewProjectDialog`):
  - destination parent folder (default: the New Project dialog's default parent), folder name
    `MainframeEngine.Demo` (validated with the same rules as `NewProjectRequest`; an existing non-empty folder is an
    error);
  - shows the engine checkout the Demo will use, or the same "no engine checkout" error New Project shows;
  - **Download** → progress bar (bytes / total from `Content-Length`), status line, **Cancel**;
  - on failure: the error and a **Retry** button; nothing is left behind.

### Flow (`Src/Projects/DemoDownloader.cs`)

1. **URL.** `https://github.com/Mainframe-Games/mainframe-engine/releases/download/v{EngineInfo.Version}/MainframeEngine.Demo-v{EngineInfo.Version}.zip`
   for release builds. Development builds (`EngineInfo.Version == "0.0.0-dev"`, see `EngineInfo.cs`) use
   `https://github.com/Mainframe-Games/mainframe-engine/releases/latest/download/MainframeEngine.Demo.zip` and the
   dialog notes that the latest release's Demo may not match a development engine. The base URL is a constant in
   one place (`DemoDownloader.ReleasesBaseUrl`) so a fork changes one line.
2. **Download** with one shared `HttpClient` (user agent `MainframeEngine-Editor/{version}`), streamed to a temp file
   under `~/.mainframe/downloads/`, with progress reported to the dialog on the main thread and a
   `CancellationToken`. HTTP 404 → "This editor version has no published demo" message.
3. **Extract** to a temp folder next to the destination with a **zip-slip guard**: every entry's full path must stay
   inside the temp folder; symlink entries are rejected; total uncompressed size is capped at 1 GiB.
4. **Validate**: exactly one top-level folder containing `project.mfproj` that loads with `ProjectSettingsFormat`,
   a `*.Launcher/*.Launcher.csproj` (`GameProjectLayout`) and `Content/Scenes/`.
5. **Point at the engine**: rewrite `MainframeEnginePath` in the Demo's `Directory.Build.props` to
   `GameProjectLayout.RealPath(engineCheckout)` — the same value New Project passes as `--engine-path`
   (`ProjectCreator.cs:192`). The rewrite edits only that property (XML, preserving the rest of the file).
6. **Move into place** (atomic directory move from the temp folder to the destination), delete the temp zip and
   folder.
7. `RecentProjects.Touch` and `Workspace.Commands.OpenProject(destination)` — the same path New Project's `Finish`
   uses (`NewProjectDialog.cs:236`). Opening builds the game as for any project.

Cancellation or any failure deletes the temp zip and temp folder; the destination is never partially written.

## Testing

All in `Tests/MainframeEngine.Editor.Tests`, no network:

- URL builder: release version → tagged asset URL; `0.0.0-dev` → latest URL.
- Extraction from a zip built in the test: happy path; zip-slip entry (`../x`) rejected; absolute entry rejected;
  size cap; missing `project.mfproj` rejected; two top-level folders rejected.
- `Directory.Build.props` rewrite: replaces only `MainframeEnginePath`, keeps other properties and comments; path
  with spaces.
- Downloader with an injected `HttpMessageHandler`: progress callbacks, 404 message, cancellation cleans up.
- `build/package-demo.sh` is run in the CI `template` job and the resulting zip goes through the same validation code
  (a small test entry point or `--validate-demo-zip` editor QA flag), so a broken Demo zip fails CI before release.

## Acceptance

- In a released editor with an engine checkout available, Download Demo → Download produces a folder that opens,
  builds and plays with no manual edits, and appears in Recent Projects with its icon
  ([Project icons](project-icons.md)).
- The release for `vX.Y.Z` has `MainframeEngine.Demo-vX.Y.Z.zip` and `MainframeEngine.Demo.zip` with real LFS content
  (no pointer files).
- Cancel and failures leave no files behind.
- `docs/design/editor.md#projects` and `docs/design/release.md` updated.

## Task list

1. `build/package-demo.sh`, `publish.yml` step, `just publish-local`, CI zip validation.
2. `DemoDownloader` (URL, download, extract, validate, rewrite, move) + tests.
3. Dialog + Project Manager button, wiring to Recent Projects / Open; docs.
