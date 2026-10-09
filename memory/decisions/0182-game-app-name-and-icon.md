# ADR 0182 — A game's app name and icon come from project.mfproj; macOS dev runs launch from a bundle

- **Date:** 2026-10-10
- **Status:** accepted
- **Milestone:** Forest slice follow-up (G8e), generalising ADR 0096
- **Spec:** docs/design/release.md → macOS app name → Game executables; docs/design/project-and-gamehost.md → App name
  and icon; docs/design/forest.md → App name and icon

## Context

Brogan: "Give it project icon. And make the exe called 'Forest Demo'". On macOS, `just forest` showed "Forest.Desktop"
in the Dock tooltip and a blank icon: macOS names a process and picks its Dock icon from the bundle it runs from (ADR
0096), and only the editor had a development bundle (`build/macos/DevAppBundle.targets`, hard-wired to "Mainframe
Engine"). The Forest had no `window.icon` either. `build/package-game.sh` already takes a packaged game's name from
`project.mfproj` `name` and its icon from `window.icon`, so the project file was already the source for releases.

## Decision

- **`project.mfproj` stays the single source; no new keys.** `name` is the player-facing app name and `window.icon` the
  icon (a PNG) for development runs too. `build/MainframeGame.props` reads `name`, `window.icon` and `version` (first
  match, like `isDemo`) into `MainframeAppName`, `MacAppDisplayName`, `MacAppIcon` and `MacAppVersion`, sets `Product`,
  and sets the executable's `AssemblyTitle` (Windows file description) to the name. Desktop projects override any of
  them in the csproj. An `app` section was considered and rejected: it would duplicate `name` and `window.icon`, need a
  format migration and Project Settings UI, and the packager already reads the existing keys.
- **The dev bundle is generic.** `Info.plist.in` has `@NAME@`, `@BUNDLE_ID@`, `@EXECUTABLE@`, `@ICON_FILE@`,
  `@VERSION@`; `DevAppBundle.targets` takes `MacAppDisplayName`, `MacAppBundleId` (default
  `com.mainframegames.<name>`, plus `.dev`), `MacAppIcon` (an `.icns`, or a PNG converted once per change by
  `build/macos/png-to-icns.sh`) and `MacAppVersion`, evaluates everything when its targets run (so a `.props` import
  works), and builds only for non-test executables. The editor sets its three values in its csproj; its dev bundle and
  release Info.plist are byte-identical to before.
- **Bundled games keep the bundle's Dock icon.** On macOS SDL's window icon only replaces the Dock icon, so `GameHost`
  drops `window.icon` when `MacAppBundle.HasIcon` (CoreFoundation: the main bundle has a `CFBundleIconFile`). The
  `.icns` follows Apple's icon grid; `window.icon` stays full-bleed for Windows/Linux and unbundled macOS runs.
- **The editor's Play** starts `<name>.app/Contents/MacOS/<assembly>` when a dev bundle exists (working directory: the
  build output).
- **Forest:** `name` "Forest Demo" (also the window title; `window.title` removed), `window.icon`
  `Content/Brand/icon.png`, `MacAppIcon ../Brand/forest.icns`, `ApplicationIcon ../Brand/forest.ico`. The assembly stays
  `Forest.Desktop`: every user-visible place says Forest Demo, while the scripts, tests and docs that run
  `Forest.Desktop.dll` keep working. The icon (a backlit spruce on a warm haze, the brand tile and amber) is SVG in
  `Examples/Forest/Brand` (full, small ≤ 48 px, macOS grid), rendered by `build/brand/app-icon.sh` (`just forest-brand`).

## Consequences

- `just forest` shows "Forest Demo" with the Forest icon in the Dock, app menu, Cmd+Tab and Activity Monitor
  (LaunchServices name and bundle id `com.mainframegames.forestdemo.dev`). Every game made from the template gets
  `<Name>.app` the same way; `Examples/Demo` gets `Mainframe Demo.app`.
- Renaming a project moves its user data folder: the Forest's settings start fresh once (`Mainframe Forest` →
  `Forest Demo`).
- The Windows apphost gets the icon and version strings only when building on Windows (an SDK rule).
- `build/package-game.sh` prefers the desktop project's `.icns` `MacAppIcon` and `.ico` `ApplicationIcon` over the
  PNG, and finds `window.icon` in single-line `window` sections.
- `just template-smoke` checks `SmokeGame.app`'s Info.plist, icon and executable on macOS.

## Alternatives considered

- **Rename the assembly to "Forest Demo".** Breaks `Forest.Desktop.dll` in `build/forest-*.sh`, docs, tests and lane
  scripts, and puts a space in the executable; the packager already renames the shipped executable (`ForestDemo`).
- **Set the name in-process** (`NSProcessInfo`, SDL hints): does not change the LaunchServices name (ADR 0096).
- **Generate the `.icns` from `window.icon` only:** a full-bleed tile looks oversized in the Dock; games that care
  supply an Apple-grid `.icns` in `MacAppIcon`.
