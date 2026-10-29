# Mainframe Engine

A modular C# game engine on Vulkan: a Godot-style scene tree with source-generated serialization, PBR materials,
cascaded shadows, RmlUi game UI, Jitter2/Box2D physics, SoundFlow audio, multiplayer, gettext localization and Spine
animation.

This package is everything a game needs to build and run, with no engine checkout and no editor required:

- `MainframeEngine.dll` (and the bundled `spine-csharp.dll`)
- the source generator for `[Export]`, `[Signal]`, `[Replicated]` and `[Rpc]` members
- the native libraries for `win-x64`, `linux-x64` and `osx-arm64`/`osx-x64`, Valve's `steam_api` included
- Steam integration over Facepunch.Steamworks (bundled), which starts when the Steam client runs, Apple Silicon too
- the engine content (compiled shaders, UI widgets, fonts), which is copied into your app's `Assets/` folder
- build integration: `.po` → `.mo` localization, the `DEMO` define from `project.mfproj`, and macOS development app bundles
- player builds: `tools/mf-export`, the exporter the editor's File › Export runs (see below)

**Requirements:** .NET 10, plus a GPU with Vulkan 1.2 (MoltenVK is included on macOS).

## Start a game

The quickest way is the `mfgame` template from the engine repository, or **New Project** in the optional Mainframe
Editor. Both create a game library, a desktop project and `project.mfproj` that reference this package.

To add the engine to an existing project:

```bash
dotnet add package MainframeEngine
```

```csharp
using MainframeEngine;

// Reads project.mfproj next to the app (window, input map, autoloads, main scene) and runs it.
return GameHost.Run(args, typeof(MyNode).Assembly);
```

Copy `project.mfproj` and your `Assets/` to the output folder, as the template's desktop project does.

## Player builds

Player builds come from export presets in `project.mfproj` (the editor's File › Export… writes them). The package
ships `mf-export`; `MainframeExportTool` gives its path:

```bash
mf_export="$(dotnet msbuild MyGame.Desktop -getProperty:MainframeExportTool)"
dotnet "$mf_export" --project . --preset "Windows"   # or --all, --list
dotnet "$mf_export" MyGame.Desktop/MyGame.Desktop.csproj osx-arm64 builds   # without a preset (the old package-game form)
```

It makes `<Exe>-<version>-osx-arm64.zip` (a signed `.app`), `-win-x64.zip` or `-linux-x64.tar.gz`, with only that
platform's natives. macOS presets sign (ad hoc, or a Developer ID) and notarize; they need a Mac.

## Links

- [Website](https://engine.mainframe.games/) and [documentation](https://engine.mainframe.games/docs/)
- [Source, editor downloads and releases](https://github.com/Mainframe-Games/mainframe-engine)
- MIT licence; the bundled third-party code keeps its own licences (Valve's `steam_api` natives: the Steamworks SDK
  Access Agreement)
