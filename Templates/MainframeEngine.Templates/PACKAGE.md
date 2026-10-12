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
- `Content/`, with a sample scene.

The game references the [`MainframeEngine`](https://www.nuget.org/packages/MainframeEngine) package of the same
version as this template package; `--engine-version` picks another release. The optional Mainframe Editor (on
[GitHub Releases](https://github.com/Mainframe-Games/mainframe-engine/releases)) opens these projects.

**Requirements:** the .NET 10 SDK, and a GPU with Vulkan 1.2 (MoltenVK is included on macOS).

## Player builds

The `MainframeEngine` package ships a script that packages the game for players; `MainframePackageGameScript` gives
its path:

```bash
script="$(dotnet msbuild MyGame.Desktop -getProperty:MainframePackageGameScript)"
bash "$script" MyGame.Desktop/MyGame.Desktop.csproj osx-arm64 builds   # or win-x64, linux-x64
```

The result is `builds/MyGame-<version>-osx-arm64.zip` (a signed `.app`), `-win-x64.zip` or `-linux-x64.tar.gz`. Use
`bash`, since NuGet drops the executable bit.

[Documentation](https://mainframe-games.github.io/mainframe-engine/) · MIT licence
