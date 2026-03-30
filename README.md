# Mainframe Engine

A modular C# game engine built on Vulkan via [Silk.NET](https://github.com/dotnet/Silk.NET), with [Spine](https://en.esotericsoftware.com/) skeletal animation, an advanced lighting/shadow system, dynamic sky rendering, Steam platform integration, and real-time ImGui debugging.

---

## Overview

Mainframe Engine provides a layered architecture for building 2D/3D games in C#. The primary rendering backend is Vulkan 1.2, with a WebGPU backend planned. It wraps the Spine animation runtime for skeletal animation and includes full Steamworks integration for multiplayer and platform features.

**Target:** .NET 10, cross-platform via Silk.NET  
**Status:** Active development

---

## Architecture

```
mainframe-engine/
├── MainframeEngine/          # Core engine library
│   └── Src/
│       ├── Core/             # Engine loop, IGame interface, timing
│       ├── Rendering/        # Vulkan renderer, cameras, shapes, spine, sky, shadows
│       ├── Components/       # Transform and other game object components
│       ├── Lighting/         # Directional, point, and spot lights
│       ├── Steamworks/       # Steam API wrappers
│       └── Debugging/        # Structured logging
│
├── MainframeEngine.Sandbox/  # Test game demonstrating full engine features
│
├── Plugins/
│   └── Spine/                # Full Spine C# runtime port
│
└── Examples/
    ├── SilkVulkanExamples/   # Vulkan tutorial series via Silk.NET
    └── SpineExamples/        # Spine animation showcase
```

---

## Core Systems

### Game Loop (`Core/`)

Games implement the `IGame` interface:

```csharp
public interface IGame
{
    void OnLoad(Engine engine);
    void OnUpdate(GameTime time);
    void OnRender(GameTime time);
    void OnShadowPass(GameTime time);
    void OnImGui(GameTime time);
    void OnResize(Vector2D<int> size);
    void OnClose();
}
```

`Engine` manages the Silk.NET window, Vulkan renderer, input system, and ImGui, and drives the game loop. `GameTime` provides per-frame timing (DeltaTime, FPS, FrameTimeMs, FrameCount).

### Rendering (`Rendering/`)

**Vulkan Backend:**
- `IRenderer` / `VulkanRenderer` — full Vulkan 1.2 implementation with swapchain management (triple-buffered), 2 frames-in-flight synchronization, validation layers, and depth buffer support
- `IVulkanContext` — exposes Vulkan primitives (device, queues, render pass, command buffers) to renderable objects

**Cameras:**
- `Camera3D` — perspective projection with mouse-look
- `Camera2D` — orthographic projection
- Both implement `ICamera` (ViewMatrix, ProjectionMatrix)

**Shape Primitives:**
- `Box3d` — 3D cube with shadow casting/receiving
- `Quad` — 2D quad with shadow casting/receiving
- `SceneGrid3d` / `SceneGrid2d` — debug grid overlays
- All shapes inherit from `ShapeBase` which handles TRS matrix composition

**Spine Renderer (`Rendering/Spine/`):**
- `SpineRenderer` — batch-renders Spine skeletons (RegionAttachment, MeshAttachment) with per-slot tinting, premultiplied alpha, and multi-texture atlas support
- `SpineTextureLoader` — loads Spine atlas textures via StbImageSharp
- `SpineModel` — high-level skeletal animation wrapper

**Sky Rendering (`Rendering/Sky/`):**
- `SkyProcedural` — gradient sky with animated sun disk, configurable zenith/horizon/ground colors
- `SkyPanoramic` — equirectangular panoramic image sky
- `SkyCubemap` — cubemap-based sky (6 faces)
- All sky types render as full-screen backdrops with UBO-driven inverse view/projection matrices

**Shadow System (`Rendering/Shadows/`):**
- `ShadowSystem` — manages shadow maps for directional and point lights via depth pre-pass rendering
- Integrates with shapes for shadow casting and receiving

**ImGui:**
- `VulkanImGuiController` — ImGui backend for Vulkan

### Lighting (`Lighting/`)

- `LightEnvironment` — container for all scene lights with ambient color (max 4 directional, 16 point, 8 spot)
- `DirectionalLight` — parallel sun-like light
- `PointLight` — omnidirectional light with range and falloff
- `SpotLight` — cone light with inner/outer angles
- ImGui debug gizmos for visualizing light positions, ranges, and cone shapes

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

`Log` — structured logger with severity levels (Debug, Info, Warning, Error, Fatal), ANSI color output, and optional verbose mode with caller source location.

---

## Tech Stack

| System | Library | Version |
|--------|---------|---------|
| Windowing / Input | Silk.NET | 2.21.0 |
| Vulkan bindings | Silk.NET.Vulkan | 2.22.0 |
| Skeletal animation | Spine Runtime | (Plugin) |
| Debug UI | ImGui.NET | 1.89.9.3 |
| Steam platform | Steamworks.NET | 2024.8.0 |
| Image loading | StbImageSharp | 2.30.15 |
| Model loading | Silk.NET.Assimp | 2.21.0 |

**Proposed (not yet integrated):**
- Audio: 
    - [FmodAudio](https://github.com/sunkin351/FmodAudio)
    - [SoundFlow](https://github.com/LSXPrime/SoundFlow)
- Physics: 
    - [JoltPhysicsSharp](https://github.com/amerkoleci/JoltPhysicsSharp) 
    - [box2d-netstandard](https://github.com/codingben/box2d-netstandard)
- Networking: 
    - [ENet-CSharp](https://github.com/nxrighthere/ENet-CSharp)
- Game UI Framework: 
    - [Myra](https://github.com/rds1983/myra)
    - [Skia](https://github.com/mono/skiasharp)
- Localization:
    - [GetText](https://github.com/perpetualKid/GetText.NET)

---

## Project Configuration

- **Target Framework:** `net10.0`
- **Nullable:** enabled
- **Unsafe:** enabled (required for Vulkan buffer/matrix operations)
- **Implicit usings:** enabled
- Content files (shaders, textures) are auto-copied to build output

---

## Shaders

GLSL sources in `Content/Shaders/` are compiled to SPIR-V via `glslc`. The `.spv` files are copied to the output directory and loaded at runtime.

**Engine shaders** (`MainframeEngine/Content/Shaders/`):
- **Shapes/** — vertex/fragment for quad and box rendering (`.vk.vert`/`.vk.frag`)
- **Spine/** — skeletal animation with multi-texture blending
- **Sky/** — procedural gradient, panoramic equirectangular, and cubemap variants
- **Shadows/** — depth pass shaders for 2D and omnidirectional point light shadow maps
- **SceneGrid/** — debug grid overlay
- **ImGui/** — Vulkan ImGui rendering backend

---

## Sandbox

`MainframeEngine.Sandbox` is the primary test project. `Game.cs` implements `IGame` and demonstrates:
- 3D scene with `Camera3D` and mouse-look
- `SkyPanoramic` backdrop
- Directional, point, and spot lights with debug gizmos
- Shadow casting/receiving on shapes
- `Box3d`, `Quad`, and `SceneGrid3d` rendering
- ImGui debug overlay with FPS, delta time, frame count, frame time
- Input handling (keyboard/mouse via Silk.NET)

---

## Examples

### Vulkan Tutorial (`SilkVulkanExamples`)

An interactive Vulkan tutorial series built on Silk.NET, progressing from basic setup through advanced rendering:

| Section | Topics |
|---------|--------|
| 1.1 Hello World | Window creation, Vulkan instance, validation layers, device selection, swapchain, render pass, graphics pipeline, command buffers, 2 frames-in-flight rendering |
| 1.2 Hello Quad | Vertex/index buffers, quad rendering |
| 1.3 Textures | Image loading, texture sampling |
| 1.4 Abstractions | Renderer abstractions and utilities |
| 1.5 Transformations | Model/view/projection matrices |
| 2.1 Coordinate Systems | 3D coordinate spaces |
| 2.2 Camera | Interactive camera with mouse-look |
| 3.1–3.5 Lighting | Ambient, diffuse, specular lighting, materials, lighting maps |
| 4.1 Model Loading | Importing and rendering 3D models via Assimp |

### Spine Examples (`SpineExamples`)

Demonstrates Spine skeletal animation using `SpineRenderer` with the Vulkan backend, including model loading, skinning, and animation playback.

---

## Plugins

### Spine C# Runtime (`Plugins/Spine/`)

Full C# port of the Spine skeletal animation runtime:
- Skeleton data loading (JSON/binary)
- Skeletal transforms and IK solving
- Region and mesh attachment rendering
- Animation state machine
- Skin swapping and slot customization

---

## Getting Started

1. Clone the repository
2. Open `MainframeEngine.sln` in Visual Studio or Rider
3. Set `MainframeEngine.Sandbox` as the startup project
4. Build and run — requires .NET 10 SDK
