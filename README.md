⚠️ Under Heavy Construction ⚠️


# Mainframe Engine

A modular C# game engine built on Vulkan via [Silk.NET](https://github.com/dotnet/Silk.NET), with [Spine](https://en.esotericsoftware.com/) skeletal animation, an advanced lighting/shadow system, dynamic sky rendering, Steam platform integration, and real-time ImGui debugging.

This engine is mostly for educational purposes. One day I will make a game using it but for now its mostly for learning how engines work and setting up a framework in which I like to work. Feel free to use as you wish and submit pull requests or feature requests in Issues, but this engine is mostly for me 😁

---

## Overview

Mainframe Engine provides a layered architecture for building 2D/3D games in C#. The primary rendering backend is Vulkan 1.2, with a WebGPU backend planned. It wraps the Spine animation runtime for skeletal animation and has early ENet networking and Steamworks wrappers (scaffolds, being wired up; see the roadmap).

**Target:** .NET 10, cross-platform via Silk.NET  
**Status:** Active development

---

## Documentation & Roadmap

- **[Design docs](docs/README.md):** how each subsystem works today, one topic per file, with known issues.
- **[Milestones](docs/milestones.md):** the roadmap. Every planned feature links to a detailed design proposal.

Where the engine is heading:

| Area | Plan | Design doc |
|---|---|---|
| Windowing / input | SDL2 via Silk.NET (switched from GLFW in M0) | [Build & platforms](docs/design/build-and-platforms.md#windowing-sdl2) |
| Scene model | Godot-style nodes: `SceneTree`, lifecycle callbacks, signals, groups, `.mscene` scene files | [Node system](docs/design/future/node-system.md), [Scene serialization](docs/design/future/scene-serialization.md) |
| Physics | [Jitter2](https://github.com/notgiven688/jitterphysics2) for 3D, [Box2D.NET](https://github.com/ikpil/Box2D.NET) (Box2D v3) for 2D | [Physics](docs/design/future/physics.md) |
| Audio | [SoundFlow](https://github.com/LSXPrime/SoundFlow) | [Audio](docs/design/future/audio.md) |
| Game UI | [RmlUi](https://github.com/mikke89/RmlUi) (HTML/CSS-style documents) | [Game UI](docs/design/future/game-ui.md) |
| Localization | [GetText.NET](https://github.com/perpetualKid/GetText.NET) | [Localization](docs/design/future/localization.md) |
| Editor | `MainframeEngine.Editor`, a separate project whose UI is built with the same RmlUi stack as games | [Editor](docs/design/future/editor.md) |
| Rendering backend | Backend-neutral GPU API, then WebGPU | [Backend abstraction](docs/design/future/rendering-backend-abstraction.md) |

---

## Architecture

```
mainframe-engine/
├── MainframeEngine/          # Core engine library
│   └── Src/
│       ├── Core/             # Engine base class, timing, FPS counter
│       ├── Rendering/        # Vulkan renderer, cameras, spine, sky, shadows, scene grids
│       ├── Nodes/            # Scene graph nodes (Node, Node3D, SpineNode, shapes)
│       ├── Lighting/         # Directional, point, and spot lights
│       ├── Networking/       # ENet client/server, buffered serialization, object pooling
│       ├── Steamworks/       # Steam API wrappers
│       ├── Debugging/        # Structured logging
│       └── Utils/            # ImGui gizmos, color extensions
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

Games subclass `Engine` and override its abstract methods:

```csharp
public sealed class Game(in EngineOptions options) : Engine(options)
{
    protected override void OnLoad() { base.OnLoad(); /* setup */ }
    protected override void OnUpdate(in GameTime gameTime) { }
    protected override void OnShadowPass(in GameTime gameTime) { }
    protected override void OnRenderMainPass(in GameTime gameTime) { }
    protected override void OnImGui(in GameTime gameTime) { }
    protected override void OnClose() { /* cleanup */ base.OnClose(); }
}
```

`Engine` manages the Silk.NET window, Vulkan renderer, input context, and ImGui, and drives the game loop. `GameTime` provides per-frame timing (DeltaTime, FPS, FrameTimeMs, FrameCount).

**`EngineOptions`** configures startup:

```csharp
new EngineOptions
{
    GameName = "My Game",
    RenderingBackend = RenderingBackend.Vulkan,
    WindowSize = new Vector2D<int>(1280, 720),
    IconPath = "Content/icon.png"
}
```

The frame loop order is:
1. `OnImGui` — ImGui window construction
2. `OnUpdate` — game logic
3. `OnShadowPass` — depth pre-pass (before main render pass)
4. `OnRenderMainPass` — geometry and sky rendering

### Nodes (`Nodes/`)

The engine uses a lightweight scene graph. `Node` is the base class; `Node3D` adds position, rotation, and scale with TRS matrix composition.

```
Node
├── Node3D            (Position, Rotation, Scale → ModelMatrix)
│   ├── Box3d         (3D lit cube, shadow casting/receiving)
│   ├── Quad          (2D lit quad, shadow casting/receiving)
│   └── SpineNode     (Spine skeletal animation as a 3D node)
└── NetworkNode       (ENet server/client lifecycle management)
```

`Node.Initialize(renderer, shadowSystem)` must be called once after the renderer and shadow system are created (see Sandbox).

**`NetworkNode`** manages the ENet library lifecycle and wraps `EnetServer` / `EnetClient` as a scene graph node. Call `StartServer` or `StartClient` to begin networking, and the node's `OnUpdate` automatically polls ENet events.

**`NodeId`** is a type-safe auto-incrementing identifier for nodes (starts at 1; 0 = error).

**`SpineNode`** wraps a Spine skeleton as a `Node3D`, handling animation state, world transform updates, and rendering:

```csharp
var folder = new SpineFolder("Content/Spine/character");
var spineNode = new SpineNode(Renderer, folder);
spineNode.SetAnimation("walk");
spineNode.Position = new Vector3(0, 0, 0);
spineNode.Scale = new Vector3(0.1f);

// In OnUpdate:
spineNode.OnUpdate(gameTime);
// In OnRenderMainPass:
spineNode.Draw(camera, lights);
```

### Rendering (`Rendering/`)

**Vulkan Backend:**
- `IRenderer` / `VulkanRenderer` — full Vulkan 1.2 implementation with swapchain management (recreated on resize/VSync/image-count changes), 2 frames-in-flight synchronization with per-frame-slot resources, validation layers (on in Debug builds), and depth buffer support
- `IVulkanContext` — exposes Vulkan primitives (device, queues, render pass, command buffers) to renderable objects

**Cameras:**
- `Camera3D` — perspective projection with mouse-look
- `Camera2D` — orthographic projection
- Both implement `ICamera` (ViewMatrix, ProjectionMatrix)

**Shape Primitives (`Nodes/Shapes/`):**
- `Box3d` — 3D cube with shadow casting/receiving
- `Quad` — 2D quad with shadow casting/receiving
- `SceneGrid3d` / `SceneGrid2d` — debug grid overlays
- All shapes inherit from `ShapeBase` which inherits `Node3D` for TRS matrix composition

**Spine Renderer (`Rendering/Spine/`):**
- `SpineRenderer` — batch-renders Spine skeletons (RegionAttachment, MeshAttachment) with per-slot tinting, premultiplied alpha, and multi-texture atlas support
- `SpineTextureLoader` — loads Spine atlas textures via StbImageSharp

**Sky Rendering (`Rendering/Sky/`):**
- `SkyProcedural` — gradient sky with animated sun disk, configurable zenith/horizon/ground colors
- `SkyPanoramic` — equirectangular panoramic image sky
- `SkyCubemap` — cubemap-based sky (6 faces)
- All sky types are subtypes of `SkyEnvironment` and render as full-screen backdrops with UBO-driven inverse view/projection matrices

**Shadow System (`Rendering/Shadows/`):**
- `ShadowSystem` — manages shadow maps for directional, spot and point lights via depth pre-pass rendering; each sub-pass uses its own light matrix (dynamic-offset ring). Optional: without it, lit nodes bind a "no shadows" fallback
- Integrates with shapes for shadow casting and receiving

**ImGui:**
- `VulkanImGuiController` — ImGui backend for Vulkan

### Lighting (`Lighting/`)

- `LightEnvironment` — container for all scene lights with ambient color (max 4 directional, 16 point, 8 spot)
- `DirectionalLight` — parallel sun-like light
- `PointLight` — omnidirectional light with range and falloff
- `SpotLight` — cone light with inner/outer angles
- ImGui debug gizmos for visualizing light positions, ranges, and cone shapes

### Networking (`Networking/`)

UDP networking built on [ENet-CSharp](https://github.com/nxrighthere/ENet-CSharp) with pooled buffer serialization.

**Client / Server:**
- `EnetServer` — hosts a game session, tracks connected peers, broadcasts packets
- `EnetClient` — connects to a server, sends packets on a channel (a `byte` id)
- Both are created through `NetworkNode` (their constructors are internal). They poll ENet events (Connect, Disconnect, Timeout, Receive) and use `PeerId` for type-safe peer identification.
- **Status:** scaffold. There is no message protocol or state replication yet; see [Networking](docs/design/networking.md) and milestone M5.

**Serialization (`Buffers/`):**
- `NetBufferWriter` / `NetBufferReader` — BinaryWriter/Reader wrappers supporting all primitives, `string`, `Vector3`, and generic `INetworkTransferable` arrays
- `NetBufferPool` — internal object pool for readers and writers; disposing a buffer returns it to the pool
- `GetDataSpan()` provides zero-copy access to the underlying buffer

**Custom Types (`Transfer/`):**
- `INetworkTransferable` — implement `NetworkWrite` / `NetworkRead` to serialize game-specific types

**Utilities (`Utils/`):**
- `NetworkUtils` — default port (`6969`), region constants (OCE, USE, USW, EU, Asia), and `GetPrimaryLocalIPv4()` for LAN discovery

```csharp
// Server (call net.OnUpdate(gameTime) every frame to poll ENet)
var net = new NetworkNode();
net.StartServer(port: 7777, maxClients: 16);

using var writer = new NetBufferWriter(1024);
writer.Write("hello");
net.Server!.SendToPeers(channel: 0, writer.GetDataSpan(), PacketFlags.Reliable);

// Client
var clientNet = new NetworkNode();
clientNet.StartClient("127.0.0.1", 7777);
clientNet.Client!.Send(channel: 0, writer.GetDataSpan(), PacketFlags.Reliable);
```

### Steam Integration (`Steamworks/`)

Steamworks.NET wrappers. **Status:** scaffold. Steam is not initialized yet (`Steam.Valid` is always false), so none of these are active; see [Steamworks](docs/design/steamworks.md) and milestone M5.
- `Steam` / `SteamManager` — initialization, lifecycle, user info, branch detection
- `SteamLobby` / `SteamLobbyInfo` — multiplayer lobby creation and management
- `SteamRichPresence` — player status/activity display
- `SteamFriend` / `SteamAvatar` — friend list (avatars not ported yet)
- `SteamAchievements` — achievement unlocking
- `SteamOverlay` — Steam overlay control

### Logging (`Debugging/`)

`Log` — structured logger with severity levels (Debug, Info, Warning, Error, Fatal), ANSI color output, and optional verbose mode with caller source location.

### Utilities (`Utils/`)

- `ImGuiGizmos` — ImDrawList extensions for rendering debug arrows (`DrawArrow`) and sun icons (`DrawSunIcon`) in ImGui
- `ColorExtensions` — `System.Drawing.Color` to ImGui `uint` conversion via `ToImColor()`

---

## Tech Stack

| System | Library | Version |
|--------|---------|---------|
| Windowing / Input | Silk.NET (SDL2 backend) | 2.22.0 |
| Vulkan bindings | Silk.NET.Vulkan | 2.22.0 |
| Skeletal animation | Spine Runtime | (Plugin) |
| Debug UI | ImGui.NET | 1.89.9.3 |
| Steam platform | Steamworks.NET | 2024.8.0 |
| Image loading | StbImageSharp | 2.30.15 |
| Model loading | Silk.NET.Assimp | 2.21.0 |
| Networking | ENet-CSharp | 2.4.8 |
| Vulkan on macOS | Silk.NET.MoltenVK.Native | 2.22.0 |

**Planned integrations** (chosen; not integrated yet; see [Milestones](docs/milestones.md)):

| System | Library | Milestone | Design doc |
|--------|---------|-----------|------------|
| Windowing / input | SDL2 via Silk.NET 2.22 (`Silk.NET.Windowing.Sdl`/`.Input.Sdl`) | M0 ✅ | [Build & platforms](docs/design/build-and-platforms.md#windowing-sdl2) |
| Physics 3D | [Jitter2](https://github.com/notgiven688/jitterphysics2) | M6 | [Physics](docs/design/future/physics.md) |
| Physics 2D | [Box2D.NET](https://github.com/ikpil/Box2D.NET) (Box2D v3 port; replaces box2d-netstandard, which is unmaintained) | M6 | [Physics](docs/design/future/physics.md) |
| Audio | [SoundFlow](https://github.com/LSXPrime/SoundFlow) | M7 | [Audio](docs/design/future/audio.md) |
| Game UI | [RmlUi](https://github.com/mikke89/RmlUi) (engine-owned C# binding) | M8 | [Game UI](docs/design/future/game-ui.md) |
| Localization | [GetText.NET](https://github.com/perpetualKid/GetText.NET) | M9 | [Localization](docs/design/future/localization.md) |

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
- **Spine/** — lit, shadow-receiving skeletal animation (`SpineLit.vk.*`)
- **Sky/** — procedural gradient, panoramic equirectangular, and cubemap variants
- **Shadows/** — depth pass shaders for 2D and omnidirectional point light shadow maps
- **SceneGrid/** — debug grid overlay
- **ImGui/** — Vulkan ImGui rendering backend

---

## Sandbox

`MainframeEngine.Sandbox` is the primary test project. `Game.cs` subclasses `Engine` and demonstrates:
- 3D scene with a `Camera3D` fly camera (hold right mouse to look, WASD/QE to move, Shift for speed; Alt toggles the cursor)
- `SkyPanoramic` backdrop
- Directional light with debug gizmos
- Shadow casting/receiving on shapes
- `Box3d`, `Quad`, and `SceneGrid3d` rendering
- `SpineNode` animation (SpineBoy character)
- ImGui debug overlay with FPS, delta time, frame count, frame time, VSync toggle, fullscreen toggle, MaxFPS selector
- Light gizmo visualization via `LightEnvironment.DrawLightGizmos()`
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

**macOS:** Vulkan runs through MoltenVK. A copy is bundled via `Silk.NET.MoltenVK.Native`, so nothing needs to be installed to run. Installing the [Vulkan SDK](https://vulkan.lunarg.com) is still recommended for development — it provides the validation layers and `glslc`. `VulkanLoaderBootstrap` hands the Vulkan library to SDL (`SDL_Vulkan_LoadLibrary`) and Silk.NET explicitly, because modern macOS dyld no longer finds `/usr/local/lib` when the loader is dlopened by name.
