# Proposal: Project icons in the Project Manager

**Milestone:** [Demo & polish](../../milestones.md#demo--polish-) (D3) · **Status:** ✅ done ·
**Depends on:** [M10 editor → projects](../editor.md#projects) · **Related:** [Demo project](demo-project.md) (ships
an icon), [Demo download](demo-download.md)

## Problem

The Project Manager (`MainframeEngine.Editor/Src/UI/ProjectManager.cs`, `Content/Editor/project_manager.rml`) shows
every project with the same font glyph (`icon-folder-code`, or `icon-folder-x` when missing,
`ProjectManager.cs:24-30`, `project_manager.rml:109-117`). Godot shows each project's own icon
(`application/config/icon`), which makes a long list scannable.

The engine already has an icon per project — `window.icon` in `project.mfproj` (`WindowSettings.Icon`,
`MainframeEngine/Src/Project/ProjectSettings.cs:180-181`), used for the game window (`Engine.SetWindowIcon`,
`Engine.cs:393-405`) and editable in Project Settings (`ProjectSettingsModel.cs:109-110`). Nothing in the editor
displays it, and the template does not set one.

There is also a latent bug: RmlUi's default `SystemInterface::JoinPath` strips a leading `/`
(`Native/RmlUi/external/RmlUi/Source/Core/SystemInterface.cpp:54-59`), and the shim's `JoinPath` callback
(`Native/RmlUi/shim/core.cpp:61-69`, field at `MainframeEngine/Src/UI/Rml/RmlNative.cs:571`) is never assigned from
C#. So `<img src="/Users/me/game/Content/icon.png">` becomes `Users/me/…` and fails on macOS and Linux. The
FileSystem panel's thumbnails bind absolute cache paths the same way (`FileSystemPanel.cs:264-267`,
`filesystem.rml:97`) and are likely affected.

## Goals

- Each Project Manager row shows the project's icon; projects without one keep a glyph.
- New projects get a default icon (the brand logo) so the list looks right out of the box.
- Absolute image paths work in RmlUi on every OS.

## Non-goals

- A separate "project icon" setting. `window.icon` **is** the project icon (one PNG for the window, taskbar and
  Project Manager), like Godot's `config/icon`. No `project.mfproj` format bump.
- Icons for `.app` bundles / `.exe` resources of exported games (release packaging concern).

## Design

### Root-cause fix: absolute paths in RmlUi

Assign the shim's `JoinPath` callback in the C# system interface (`MainframeEngine/Src/UI/UiSystemInterface.cs`):

- if the source is rooted (`Path.IsPathRooted`, including `C:/…`) or has a scheme (`engine://`, `file://`), return it
  unchanged;
- otherwise keep RmlUi's behaviour (resolve relative to the document's directory; leading `/` = content root, which
  `UiFileInterface.ResolvePath` already handles, `UiFileInterface.cs:59-75`).

Rooted paths then reach `UiFileInterface.ResolvePath`, which already uses them as-is. Engine games are unaffected
(their `src` values are content-relative). The marshalled callback must not allocate per call beyond the result
string (it runs at document load, not per frame).

### Default icon in the template

- `Templates/MainframeEngine.Templates/content/mfgame/Content/icon.png` — a copy of the brand logo
  (`MainframeEngine/Content/Brand/logo-256.png`).
- The template's `project.mfproj` sets `"window": { "icon": "Content/icon.png", … }`.
- `ProjectCreator` verification (`ProjectCreator.cs:219-237`) also checks the icon exists.
- The [Demo](demo-project.md) ships its own `Content/icon.png`.

### Reading the icon

`RecentProjects` (`MainframeEngine.Editor/Src/Projects/RecentProjects.cs`) keeps storing only path/name/time; the icon
is resolved at display time by a small `ProjectIconResolver`:

1. Read `project.mfproj` through `ProjectSettingsFormat` (tolerant: any parse error → no icon).
2. Take `window.icon`; resolve it against the project folder (it is `Content/…`-relative).
3. Accept only an existing `.png` inside the project folder (no `..` escapes).
4. Cache `(project path) → (icon path, project.mfproj mtime, icon mtime)`; re-resolve when either mtime changes.

Resolution runs when the list is (re)built — opening the Project Manager and after New / Open / Remove / Download —
not per frame. Missing projects (`IsValid() == false`) skip resolution.

### Display

- Each row's data gets an `icon` string (absolute path) and `hasIcon` flag
  (`ProjectManager.cs` row model, `project_manager.rml` row template):
  - `hasIcon` → `<img src="{{icon}}"/>`, 48×48 dp, rounded 6 dp, `image-rendering` smooth;
  - otherwise the existing `icon-folder-code` glyph; missing projects keep `icon-folder-x` (greyed row).
- When an icon file changed since the last build of the list, call `RmlCore.ReleaseTextures`
  (`MainframeEngine/Src/UI/Rml/RmlCore.cs:257`) for that source so RmlUi does not show the cached image.
- Project Settings → Window → Icon gets a small preview of the chosen PNG (same `<img>` path).

## Testing

- Unit (`Tests/MainframeEngine.Tests`): `JoinPath` — rooted Unix path, Windows drive path, `engine://`, relative to
  document, leading-`/` content path.
- Unit (`Tests/MainframeEngine.Editor.Tests`): `ProjectIconResolver` — icon present, no `window.icon`, missing file,
  non-PNG, `..` escape rejected, mtime cache invalidation, malformed `project.mfproj`.
- Template smoke (`just template-smoke`): new project has `Content/icon.png` and `window.icon` set.
- Editor QA: `Tests/QA/readme-screenshots.qa` Project Manager screenshot shows the Demo's icon; FileSystem thumbnails
  render on macOS (regression check for the `JoinPath` fix).

## Acceptance

- Project Manager rows show each project's `window.icon`; the glyph fallbacks behave as described.
- `dotnet new mfgame` projects show the brand logo in the Project Manager and as their window icon.
- FileSystem panel thumbnails render on macOS and Linux.
- `docs/design/editor.md#projects` and `docs/design/game-ui.md` (absolute `src` paths) updated.

## Task list

1. `JoinPath` callback + tests (verify FileSystem thumbnails).
2. Template icon + `window.icon` + creator verification.
3. `ProjectIconResolver`, row model + RML, texture release, Project Settings preview; tests; docs.
