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
| Scene model | Godot-style nodes: `SceneTree`, lifecycle callbacks, signals, groups, `.mscene` scene files | [Scene graph & nodes](docs/design/scene-graph-and-nodes.md), [Scene serialization](docs/design/scene-serialization.md) |
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
│       ├── Scene/            # Node, SceneTree, Node2D/Node3D, transforms, cameras/lights/sky nodes, input
│       ├── Resources/        # Resource, PackedScene, ResourceLoader/Saver, AssetDatabase, UIDs
│       ├── Serialization/    # [Export]/[Signal] attributes, TypeRegistry, JSON scene reader/writer
│       ├── Servers/          # ServerRegistry, RenderServer
│       ├── Rendering/        # Vulkan renderer, camera math, spine, sky, shadows, scene grids
│       ├── Nodes/            # Drawable and network nodes (SpineNode, Box3d, Quad, NetworkNode)
│       ├── Lighting/         # Directional, point, and spot lights
│       ├── Networking/       # ENet client/server, buffered serialization, object pooling
│       ├── Steamworks/       # Steam API wrappers
│       ├── Debugging/        # Structured logging
│       └── Utils/            # ImGui gizmos, color extensions
│
├── MainframeEngine.Generators/  # Source generator: [Export]/[Signal] type registration (analyzer)
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

Games subclass `Engine`, load a scene into the engine-owned scene tree, and let it run the frame:

```csharp
public sealed class Game(in EngineOptions options) : Engine(options)
{
    protected override void OnLoad()
    {
        base.OnLoad();                                      // window, renderer, ImGui, servers
        Tree.ChangeSceneToFile("Content/Scenes/Main.mscene");
    }

    // Optional legacy hooks: OnImGui, OnUpdate, OnShadowPass, OnRenderMainPass.
    protected override void OnImGui(in GameTime gameTime) { }
}
```

`Engine` manages the SDL window, Vulkan renderer, input context, ImGui and the `SceneTree`, and drives the game loop. `GameTime` provides per-frame timing (DeltaTime, FPS, FrameTimeMs, FrameCount).

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
2. `OnUpdate` — game logic (legacy hook)
3. Scene tree tick — fixed-step `OnPhysicsProcess`, `OnProcess`, deferred calls and `QueueFree`, transform sync
4. Shadow pass — `OnShadowPass`, then the render server's shadow casters
5. Main pass — the render server draws the sky and visuals, then `OnRenderMainPass`, then ImGui

### Nodes and scenes (`Scene/`, `Resources/`)

Everything in a game is a node in a Godot-style tree: `Node` (name, parent, children, owner, groups, signals,
`ProcessMode`), `Node3D` / `Node2D` (cached, dirty-flagged transforms; quaternion rotation), and front-ends
for the renderer: `Camera3D`, `DirectionalLight3D` / `OmniLight3D` / `SpotLight3D`, `WorldEnvironment` (sky),
`Box3d`, `Quad`, `SpineNode`, `Grid3D`. The `SceneTree` runs `OnEnterTree` / `OnReady` / `OnPhysicsProcess` /
`OnProcess` / `OnExitTree`, input (`OnInput`), groups, `CallDeferred` and `QueueFree`; servers
(`RenderServer`, …) own the GPU objects behind the nodes.

```csharp
public sealed class Spinner : Node3D
{
    [Export] public float DegreesPerSecond { get; set; } = 45;
    [Signal] public event Action? Spun;

    protected override void OnProcess(in GameTime time) =>
        RotationDegrees += new Vector3(0, DegreesPerSecond * time.DeltaTime, 0);
}

var level = ResourceLoader.Load<PackedScene>("Content/Scenes/Level.mscene").Instantiate();
Tree.Root.AddChild(level);
level.GetNode<SpineNode>("Player/Body").SetAnimation("walk");
SceneSaver.Save(level, "Content/Scenes/Level.mscene");
```

Scenes (`.mscene`) and resources (`.mres`) are JSON with stable UIDs, nested scene instances with overrides,
and only non-default values. `[Export]` members are registered by the `MainframeEngine.Generators` source
generator (no runtime reflection). See [Scene graph & nodes](docs/design/scene-graph-and-nodes.md) and
[Scene serialization](docs/design/scene-serialization.md).

**`NetworkNode`** manages the ENet library lifecycle and wraps the server and client message buses as a node;
it polls them every frame in `OnProcess` (or call `Poll()`).

**`NodeId`** is a runtime-unique node identifier (starts at 1; 0 = invalid); `SceneTree.Find(id)` looks a node up.

### Rendering (`Rendering/`)

**Vulkan Backend:**
- `IRenderer` / `VulkanRenderer` — full Vulkan 1.2 implementation with swapchain management (recreated on resize/VSync/image-count changes), 2 frames-in-flight synchronization with per-frame-slot resources, validation layers (on in Debug builds), and depth buffer support
- `IVulkanContext` — exposes Vulkan primitives (device, queues, render pass, command buffers) to renderable objects

**Cameras:**
- `Camera3D` / `Camera2D` nodes — the viewport's active camera; they drive the camera math below
- `PerspectiveCamera` / `OrthographicCamera` — `ICamera` (ViewMatrix, ProjectionMatrix), usable without a tree

**Shape Primitives (`Nodes/Shapes/`):**
- `Box3d` — 3D cube with shadow casting/receiving
- `Quad` — 2D quad with shadow casting/receiving
- `SceneGrid3d` / `SceneGrid2d` — debug grid overlays
- All shapes inherit from `ShapeBase` → `VisualInstance3D` → `Node3D`; the render server creates their GPU objects when they enter the tree

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
// Server (add the node to the scene tree, or call net.Poll() every frame, to pump ENet)
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
