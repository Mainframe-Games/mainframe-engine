# Mainframe Engine

A modular C# game engine on Vulkan: a Godot-style scene tree with source-generated serialization, PBR materials,
cascaded shadows, RmlUi game UI, Jitter2/Box2D physics, SoundFlow audio, multiplayer, gettext localization and Spine
animation.

This package is everything a game needs to build and run, with no engine checkout and no editor required:

- `MainframeEngine.dll` (and the bundled `spine-csharp.dll`)
- the source generator for `[Export]`, `[Signal]`, `[Replicated]` and `[Rpc]` members
- the native libraries for `win-x64`, `linux-x64` and `osx-arm64`/`osx-x64`
- the engine content (compiled shaders, UI widgets, fonts), which is copied into your app's `Content/` folder
- build integration: `.po` → `.mo` localization, the `DEMO` define from `project.mfproj`, and macOS development app bundles

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

Copy `project.mfproj` and your `Content/` to the output folder, as the template's desktop project does.

## Links

- [Website](https://mainframe-games.github.io/mainframe-engine/) and [documentation](https://mainframe-games.github.io/mainframe-engine/docs/)
- [Source, editor downloads and releases](https://github.com/Mainframe-Games/mainframe-engine)
- MIT licence
