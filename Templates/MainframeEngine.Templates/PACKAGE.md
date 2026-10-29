# Mainframe Engine templates

`dotnet new` templates for [Mainframe Engine](https://github.com/Mainframe-Games/mainframe-engine) games.

```bash
dotnet new install MainframeEngine.Templates
dotnet new mfgame -n MyGame
dotnet run --project MyGame/MyGame.Desktop
```

`mfgame` creates:

- `MyGame/`, a class library for your node types (start from `Src/Spinner.cs`);
- `MyGame.Desktop/`, the executable that runs the project with `GameHost`;
- `project.mfproj`, which holds the main scene, window, physics, input actions and autoloads;
- `Assets/`, with a sample scene.

The game references the [`MainframeEngine`](https://www.nuget.org/packages/MainframeEngine) package of the same
version as this template package; `--engine-version` picks another release. The optional Mainframe Editor (on
[GitHub Releases](https://github.com/Mainframe-Games/mainframe-engine/releases)) opens these projects.

**Requirements:** the .NET 10 SDK, and a GPU with Vulkan 1.2 (MoltenVK is included on macOS).

## Player builds

Export presets in `project.mfproj` (the editor's **File › Export…** writes them) describe each player build. The
`MainframeEngine` package ships `mf-export`, the same exporter the editor runs; `MainframeExportTool` gives its path:

```bash
mf_export="$(dotnet msbuild MyGame.Desktop -getProperty:MainframeExportTool)"
dotnet "$mf_export" --project . --list                     # the presets and their validation
dotnet "$mf_export" --project . --preset "Windows"         # or --all
dotnet "$mf_export" MyGame.Desktop/MyGame.Desktop.csproj osx-arm64 builds   # no preset: the old package-game form
```

The result is `MyGame-<version>-osx-arm64.zip` (a signed `.app`), `-win-x64.zip` or `-linux-x64.tar.gz` (or a folder).

[Documentation](https://engine.mainframe.games/docs/) · MIT licence
