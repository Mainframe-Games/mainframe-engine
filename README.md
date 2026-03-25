⚠️ HEAVILY UNDER CONSTRUCTION ⚠️

# Mainframe Engine

A modular C# game engine framework built around [Spine](https://en.esotericsoftware.com/) skeletal animation, with modern rendering backends, Steam platform integration, and real-time debugging tools.

---

## Overview

Mainframe Engine provides a clean, layered architecture for building 2D/3D games in C#. It abstracts low-level graphics APIs (OpenGL, Vulkan), wraps the Spine animation runtime for skeletal animation, and includes full Steamworks integration for multiplayer and platform features.

**Target:** .NET 10, cross-platform via Silk.NET
**Status:** Active development — core systems functional, additional backends and features being added

---

## Architecture

```
mainframe-engine/
├── MainframeEngine/          # Core engine library
│   └── Src/
│       ├── Core/             # Engine loop, IGame interface, timing
│       ├── Rendering/        # OpenGL abstractions, cameras, shapes, Spine renderer
│       ├── Components/       # Transform and other game object components
│       ├── Steamworks/       # Steam API wrappers
│       └── Debugging/        # Logging system
│
├── MainframeEngine.Sandbox/  # Test game / scratchpad using the engine
│
├── Plugins/
│   └── Spine/                # Full Spine C# runtime port
│
└── Examples/
    ├── BlissExamples/        # Bliss framework example
    ├── SDL3Examples/         # SDL3 windowing/input example
    ├── SilkOpenGLExamples/   # OpenGL rendering via Silk.NET
    ├── SilkVulkanExamples/   # Vulkan rendering via Silk.NET
    └── SpineExamples/        # Spine animation showcase
```

---

## Core Systems

### Game Loop (`Core/`)

The engine uses an `IGame` interface that any game implements:

```csharp
public interface IGame
{
    void OnLoad(Engine engine);
    void OnUpdate(GameTime time);
    void OnRender(GameTime time);
    void OnImGui(GameTime time);   // ImGui debug UI
    void OnClose();
}
```

`Engine` manages the Silk.NET window, OpenGL context, input system, ImGui, and drives the game loop. `GameTime` provides per-frame timing (DeltaTime, FPS, FrameTimeMs, FrameCount).

### Rendering (`Rendering/`)

**OpenGL Abstractions:**
- `Shader` — compile/link GLSL shaders, set uniforms
- `Texture` — load images (StbImageSharp), bind to slots
- `BufferObject<T>` — generic GPU buffer (VBO, IBO, etc.)
- `VertexArrayObject` — vertex attribute layout management

**Cameras:**
- `Camera3D` — perspective projection with mouse-look
- `Camera2D` — orthographic projection
- Both implement `ICamera` (ViewMatrix, ProjectionMatrix)

**Shapes:**
- `Box3d` — 3D cube primitive
- `Quad` — 2D quad
- `SceneGrid3d` / `SceneGrid2d` — debug grid overlays
- Shapes inherit from `ShapeBase` which handles TRS matrix composition

**Spine Renderer (`Rendering/Spine/`):**
- `SpineRenderer` — batch-renders Spine skeletons (RegionAttachment, MeshAttachment), handles MVP matrices and texture atlas multi-texture support
- `SpineTextureLoader` — loads Spine atlas textures via StbImageSharp

### Components (`Components/`)

- `Transform` — position, rotation, scale → model matrix (TRS composition)

### Steam Integration (`Steamworks/`)

Full Steamworks.NET wrapper:
- `Steam` / `SteamManager` — initialization, lifecycle, user info, branch detection
- `SteamLobby` / `SteamLobbyInfo` — multiplayer lobby creation and management
- `SteamRichPresence` — player status/activity display
- `SteamFriend` / `SteamAvatar` — friend list and profile pictures
- `SteamAchievements` — achievement unlocking
- `SteamOverlay` — Steam overlay control
- `SteamRemotePlay` — Remote Play Together support

### Logging (`Debugging/`)

`Log` — structured logging with multiple severity levels.

---

## Tech Stack

| System | Library | Version |
|--------|---------|---------|
| Windowing / Input | Silk.NET | 2.21.0 |
| OpenGL bindings | Silk.NET.OpenGL | 2.21.0 |
| Vulkan bindings | Silk.NET.Vulkan | 2.21.0 |
| Skeletal animation | Spine Runtime | (Plugin) |
| Debug UI | ImGui.NET | latest |
| Steam platform | Steamworks.NET | 2024.8.0 |
| Image loading | StbImageSharp | 2.30.15 |
| Model loading | Silk.NET.Assimp | 2.21.0 |

**Proposed (not yet integrated):**
- Audio: [FmodAudio](https://github.com/sunkin351/FmodAudio)
- Physics: [JoltPhysicsSharp](https://github.com/amerkoleci/JoltPhysicsSharp) or [box2d-netstandard](https://github.com/codingben/box2d-netstandard)
- Networking: [ENet-CSharp](https://github.com/nxrighthere/ENet-CSharp)
- UI: [nanogui](https://github.com/wjakob/nanogui)

---

## Project Configuration

- **Target Framework:** `net10.0`
- **Nullable:** enabled
- **Unsafe:** enabled (required for OpenGL buffer/matrix operations)
- **Implicit usings:** enabled
- Content files (shaders, textures) are auto-copied to build output

---

## Rendering Backends

The `Examples/` directory demonstrates multiple rendering backends that can be used as the windowing/rendering foundation:

- **Silk.NET + OpenGL** — primary backend used by the core engine
- **Silk.NET + Vulkan** — lower-level alternative
- **SDL3** — alternative windowing and input
- **Bliss** — additional framework option

---

## Shaders

Shaders live in `Content/Shaders/` and are loaded at runtime. The `Shader` class handles compilation, linking, and uniform assignment. MVP matrix transforms are standard across all shape and Spine renderers.

---

## Sandbox

`MainframeEngine.Sandbox` is the primary test project. `GameTest.cs` implements `IGame` and demonstrates:
- 3D scene with Camera3D and mouse-look
- Box3d and grid rendering
- ImGui debug overlay with live engine stats
- Input handling (keyboard/mouse via Silk.NET)

---

## Getting Started

1. Clone the repository
2. Open `MainframeEngine.sln` in Visual Studio or Rider
3. Set `MainframeEngine.Sandbox` as the startup project
4. Build and run — requires .NET 10 SDK
