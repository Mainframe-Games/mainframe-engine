# ADR 0229 — Game export: one C# runner in an export library, mf-export, project format 5

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/game-export.md, docs/design/release.md#games, docs/design/editor.md#export

## Context

G5 adds File › Export to the editor. Player builds came from `build/package-game.sh`: bash, `zip`, `sed` reads of
`project.mfproj` at a fixed indent, macOS `sips`/`iconutil` or ImageMagick for icons, nothing in the editor, and no way
to keep per-platform settings with the project. The proposal's task G5.1 asked for spikes on .NET 10 before committing to
the design.

## Decision

- **One pipeline in C#,** `ExportRunner` in a new library `Tools/MainframeEngine.Export` (references the engine), used by
  the editor (File › Export, `--export-release`/`--export-debug`/`--export-all`), `mf-export`
  (`Tools/MainframeEngine.Export.Cli`), `just package-game` (the old arguments, now an unsaved preset), `just export`
  and CI. `build/package-game.sh` is deleted. Icons (`.ico`, `.icns` with PNG chunks), the Info.plist and the archives
  (zip, tar.gz with explicit Unix modes) are written in C#; macOS still needs `codesign`, `ditto` and `xcrun`.
  Signing is ported to C# (`MacSigner`) with the same inside-out rules as `build/macos/sign-app.sh`, which the editor's
  release keeps; `SymbolCollector` likewise mirrors `build/collect-symbols.sh`. The editor's tool helpers
  (`ProcessRunner`, `DotnetSdk`, `GameProjectLayout`, `MsBuildOutputParser`) move into the library unchanged.
- **The package carries `mf-export`** in `tools/mf-export` (like `mf-l10n`; `pack-nuget.sh` builds it first) instead of
  the scripts; `MainframeGame.props` names it `MainframeExportTool` (in a checkout: the CLI project). The engine's
  natives per RID are baked into the library at build time, so the package's and the released editor's exporter prune
  other platforms' natives without a checkout.
- **Presets live in `project.mfproj`** (`export`), the mobile proposal's choice, as `ExportPreset` +
  `IExportPlatformOptions` (`DesktopExportOptions`, `MacOsExportOptions`) in the engine, hand-written reader/writer.
- **Project format 5.** Formats 3 and 4 were taken by earlier milestones; 4 → 5 is an empty step whose only job is to
  make an older editor reject the file instead of dropping (and on save deleting) the presets.
- **Two desktop keys beyond the proposal:** `symbols` (`ship`/`separate`, the G15 crash-reporting option the old script
  had) and `profiler` (off sets the `MainframeEngine.Profiler.Disabled` AppContext switch through
  `MainframeGame.props`, the profiler doc's per-preset switch).

### Spike outcomes (G5.1, .NET SDK 10.0.402 on macOS arm64)

- **`--artifacts-path` isolates** the desktop project, the game library and the engine projects (bin and obj, the
  restore files included) from the editor's Debug `bin/`/`obj/`. It broke one thing: the engine copied its Steamworks
  shim builds from `MainframeEngine.Steamworks/{Win64,Posix}/bin/<cfg>/<tfm>/`; `MainframeEngine.csproj` now takes them
  from `$(ArtifactsPath)/bin/MainframeEngine.Steamworks.<variant>/<cfg>/` when `UseArtifactsOutput` is true.
- **No NETSDK1074:** a `win-x64` publish from macOS writes the `ApplicationIcon` (checked: the `.ico`'s PNG bytes are in
  the apphost) and the GUI subsystem (`MainframeWindowsGui`, PE subsystem 2). The proposal's `win-icon-host` warning is
  dropped.
- **ReadyToRun cross-compiles** `linux-x64` and `win-x64` from macOS; the default stays on.
- **Single file** is not supported inside a macOS bundle (the bundle apphost rewrite needs the app's `.dll` beside it):
  `single-file` is an error for macOS presets, a warning (experimental) elsewhere.
- **`.icns` with PNG chunks** (`icp4`…`ic10`) is read by `iconutil` and shows in Finder; an exported template game
  passes `codesign --verify --deep --strict` and runs `--headless`.

## Consequences

- Exports are reproducible everywhere a .NET 10 SDK runs; Windows and Linux targets export from any host, macOS targets
  need a Mac (`host-unsupported`).
- CI's `template` job exports the template game for its host RID and runs it headless; the Linux job no longer
  packages an unsigned macOS bundle (fake-host unit tests cover the bundle layout).
- An older editor cannot open a format-5 project ("Update the engine.").
- Game builds now carry `--artifacts-path` intermediates under the project's `.mainframe/export/obj` (ignored by the
  template's `.gitignore`), one tree per RID and full/demo.
