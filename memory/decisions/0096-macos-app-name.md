# ADR 0096 — macOS app name "Mainframe Editor" (release and development bundles)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10
- **Amended 2026-10-22:** the editor's macOS app name changed from "Mainframe Engine" to **Mainframe Editor**
  (the editor is an optional download; the engine/NuGet package keeps "Mainframe Engine"). The mechanism below is
  unchanged — only the `CFBundleName`/`CFBundleDisplayName` and `.app` folder name differ. The archive prefix stayed
  `MainframeEngine-X.Y.Z-<rid>` (the updater matches on it).
- **Amended 2026-10-26:** the archives are now `MainframeEditor-X.Y.Z-<rid>` (and so is the Windows/Linux top folder),
  matching the app name. A clean break: editors up to 1.1.5 match the old prefix and see no update; they are
  replaced by hand once.

## Context

On macOS the running editor's Dock tooltip and bold app menu read "MainframeEngine.Editor" in development and
"Mainframe Editor" in the release `.app`. The product name is **Mainframe Editor**. The executable/assembly name must
stay `MainframeEngine.Editor` (publish.yml, render tests and the editor tests depend on it).

macOS names a GUI process once, when it registers with LaunchServices (on `NSApplication` startup): from the
`CFBundleDisplayName`/`CFBundleName` of the main bundle, which CoreFoundation derives from the executable's exec path,
or from the executable file name when it is not inside a `.app`. Checked on macOS 27 (Darwin 27):

- `-[NSProcessInfo setProcessName:]` before `NSApplication` exists: LaunchServices name unchanged (`lsappinfo` shows
  the file name). SDL2 has no app-name hint; its app menu items use the same bundle lookup.
- Private LaunchServices calls (`_LSSetApplicationInformationItem`, `CPSSetProcessName`) can rename a running
  process but are undocumented and break between releases.
- An executable started from inside a `.app` — even through a symlink — gets that bundle's identity: `lsappinfo`
  reports the bundle's name, id and path, and the menu bar shows "Mainframe Editor".

## Decision

- `build/macos/Info.plist.in`: `CFBundleName` = `CFBundleDisplayName` = "Mainframe Editor"; identifier
  `com.mainframegames.editor@BUNDLE_ID_SUFFIX@`.
- Release: `build/package-editor.sh` builds `Mainframe Editor.app` (suffix empty). Archives are
  `MainframeEditor-X.Y.Z-<rid>` on every platform (the updater matches on that prefix; `MainframeEngine-` before the
  2026-10-26 amendment), the GitHub Release is titled
  "Mainframe Engine & Editor vX.Y.Z".
- Development: `build/macos/DevAppBundle.targets` (imported by the editor project) runs after every macOS build without
  a RuntimeIdentifier and assembles `bin/<cfg>/net10.0/Mainframe Editor.app`: Info.plist from the same template
  (`$(Version)`, suffix `.dev`), `logo.icns`, and `Contents/MacOS/MainframeEngine.Editor` as a symlink to
  `../../../MainframeEngine.Editor`. A target before `ComputeRunArguments` points `RunCommand` at that path, so
  `dotnet run` and `just editor` launch inside the bundle. The apphost resolves its own path with `realpath`, so the
  app base directory (assemblies, Content) is unchanged and nothing is copied; the process is still a direct child,
  so stdout/stderr, exit codes and Ctrl+C behave as before. `-p:MacDevAppBundle=false` opts out.

## Consequences

- Dock, menu bar, Cmd+Tab and Activity Monitor say "Mainframe Editor" in both development and release runs.
- Running `bin/…/MainframeEngine.Editor` directly (or from an IDE profile that ignores `RunCommand`) still shows the
  executable name; tests and `--smoke` runs do that and do not care.
- The window title ("… — Mainframe Editor") and the About text match the app name; both say "Mainframe Editor".
- Release download names changed (`MainframeEditor-…` → `MainframeEngine-…`); no release has shipped yet.
