⚠️ Under Heavy Construction ⚠️


# Mainframe Engine

A modular C# game engine built on Vulkan via [Silk.NET](https://github.com/dotnet/Silk.NET), with [Spine](https://en.esotericsoftware.com/) skeletal animation, an advanced lighting/shadow system, dynamic sky rendering, Steam platform integration, and real-time ImGui debugging.

This engine is mostly for educational purposes. One day I will make a game using it but for now its mostly for learning how engines work and setting up a framework in which I like to work. Feel free to use as you wish and submit pull requests or feature requests in Issues, but this engine is mostly for me 😁

---

## Overview

Mainframe Engine provides a layered architecture for building 2D/3D games in C#. The primary rendering backend is Vulkan 1.2, with a WebGPU backend planned. It wraps the Spine animation runtime for skeletal animation, has server-authoritative multiplayer replication over ENet, and optional Steamworks wrappers (see the roadmap).

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
| Physics (M6 ✅) | [Jitter2](https://github.com/notgiven688/jitterphysics2) for 3D, [Box2D.NET](https://github.com/ikpil/Box2D.NET) (Box2D v3) for 2D: Godot-style bodies, areas, `MoveAndSlide`, queries | [Physics](docs/design/physics.md) |
| Audio | [SoundFlow](https://github.com/LSXPrime/SoundFlow) 1.4.1 + [NVorbis](https://github.com/NVorbis/NVorbis) (M7 ✅): `AudioServer`, bus mixer, 2D/3D audio nodes, streaming | [Audio](docs/design/audio.md) |
| Game UI | [RmlUi](https://github.com/mikke89/RmlUi) 6.3, engine-owned binding, Vulkan renderer (M8 ✅) | [Game UI](docs/design/game-ui.md) |
| Localization | [GetText.NET](https://github.com/perpetualKid/GetText.NET) runtime, in-house `mf-l10n` tooling (M9 ✅) | [Localization](docs/design/localization.md) |
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
│       ├── Audio/            # AudioServer, buses, audio nodes, streams, decoders, mixer graph (SoundFlow)
│       ├── Rendering/        # Vulkan renderer, meshes/materials/textures, camera math, spine, sky, shadows, scene grids
│       ├── Nodes/            # Drawable and network nodes (SpineNode, NetworkNode)
│       ├── Lighting/         # Directional, point, and spot lights
│       ├── Networking/       # Replication (MultiplayerApi), messages, transports, pooled buffers
│       ├── Localization/     # Tr (gettext catalogs, locale switching), RML text contract, font fallback
│       ├── Steamworks/       # Steam API wrappers
│       ├── Debugging/        # Structured logging
│       └── Utils/            # ImGui gizmos, color extensions
│
├── MainframeEngine.Generators/  # Source generator: [Export]/[Signal] type registration (analyzer)
├── MainframeEngine.Sandbox/  # Test game demonstrating full engine features
├── Tools/
│   └── MainframeEngine.L10n/ # mf-l10n: extract (RML, scenes), update, pseudo-locale, .po -> .mo compiler
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
`MeshInstance3D`, `Sprite3D`, `SpineNode`, `Grid3D`, `SubViewport`. The `SceneTree` runs `OnEnterTree` / `OnReady` / `OnPhysicsProcess` /
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
- `IRenderer` / `VulkanRenderer` — full Vulkan 1.2 implementation with swapchain management (recreated on resize/VSync/image-count changes), 2 frames-in-flight synchronization with per-frame-slot resources, validation layers (on in Debug builds), an HDR scene target with exposure + ACES tonemapping and linear lighting, and a persisted pipeline cache
- GPU resources — in-house `GpuAllocator` (64 MiB blocks), staging `UploadQueue`, frame-deferred `DeletionQueue`, `GpuBuffer`/`GpuImage`/`GpuTexture`, offscreen `RenderTarget`s
- `IVulkanContext` — exposes Vulkan primitives (device, queues, render pass, command buffers) to renderable objects

**Cameras:**
- `Camera3D` / `Camera2D` nodes — the viewport's active camera; they drive the camera math below
- `PerspectiveCamera` / `OrthographicCamera` — `ICamera` (ViewMatrix, ProjectionMatrix), usable without a tree

**Meshes and materials (`Rendering/Resources/`, `Rendering/Meshes/`):**
- `MeshInstance3D` / `Sprite3D` draw `Mesh`es (`ArrayMesh`, `BoxMesh`, `PlaneMesh`, `QuadMesh`, `SphereMesh`, `CylinderMesh`, `CapsuleMesh`) with `StandardMaterial3D` (Blinn-Phong: albedo/normal/emission textures, opaque/cutout/blend, culling, double-sided) and `Texture2D`
- The render server culls, sorts and batches them into instanced draws (10 000 instances in a couple of draws); shared pipelines come from a state-hash cache
- Models (glTF/FBX/OBJ) import through Assimp into scenes; `RenderServer.PickAsync` picks objects on the GPU; `SubViewport` renders a world offscreen (ImGui texture)
- `SceneGrid3d` / `SceneGrid2d` — debug grid overlays
- See [Materials & meshes](docs/design/materials-and-meshes.md) and [Asset pipeline](docs/design/asset-pipeline.md)

**Spine Renderer (`Rendering/Spine/`):**
- `SpineRenderer` — batch-renders Spine skeletons (RegionAttachment, MeshAttachment) with per-slot tinting, premultiplied alpha handled once in the shader, and multi-texture atlas support
- `SpineTextureLoader` — loads Spine atlas textures via StbImageSharp

**Sky Rendering (`Rendering/Sky/`):**
- `SkyProcedural` — gradient sky with animated sun disk, configurable zenith/horizon/ground colors
- `SkyPanoramic` — equirectangular panoramic image sky
- `SkyCubemap` — cubemap-based sky (6 faces)
- All sky types are subtypes of `SkyEnvironment` and render as full-screen backdrops with UBO-driven inverse view/projection matrices

**Shadow System (`Rendering/Shadows/`):**
- `ShadowSystem` — cascaded shadow maps (texel-snapped, blended) for the sun, a shadow atlas for spot and secondary directional lights, cube maps for point lights; PCF filtering, per-light `CastsShadows`/`ShadowResolution`, per-pass caster culling, alpha-tested cutout casters. Optional: without it, lit nodes bind a "no shadows" fallback
- See [Shadow system](docs/design/shadow-system.md)

**ImGui:**
- `VulkanImGuiController` — ImGui backend for Vulkan

### Lighting (`Lighting/`)

- `LightEnvironment` — container for all scene lights with ambient color (max 4 directional, 16 point, 8 spot)
- `DirectionalLight` — parallel sun-like light
- `PointLight` — omnidirectional light with range and falloff
- `SpotLight` — cone light with inner/outer angles
- ImGui debug gizmos for visualizing light positions, ranges, and cone shapes

### Networking (`Networking/`)

Server-authoritative multiplayer over [ENet-CSharp](https://github.com/nxrighthere/ENet-CSharp) (UDP) or in-process
loopback. See [Networking](docs/design/networking.md).

- **Replication (`MultiplayerApi`, `Engine.Multiplayer`):** spawn scenes on clients by `PackedScene` UID,
  `[Replicated]` members sent as delta snapshots (30 Hz default) and interpolated on clients, `[Rpc]` methods with
  per-node authority checks, late join, handshake with protocol/replication fingerprints, timeouts, kicks and
  bandwidth stats. Change detection, serialization and RPC dispatch are generated by `MainframeEngine.Generators`:
  no reflection, and 0 bytes allocated per network tick.
- **Messages (`MessageBus`, `MessageRegistry`):** typed `INetworkTransferable` structs with a 7-byte header, for game
  messages beside replication (or alone, through `NetworkNode`).
- **Transports (`ITransport`):** `EnetTransport`, `LoopbackTransport` (many clients in one process),
  `SimulatedTransport` (seeded loss/latency/jitter/duplication for tests), `SteamSocketsTransport` (stub until Steam
  natives ship). `TransportSelector` turns a lobby's connect string (`steam:…;enet:host:port`) into a transport.
- **Buffers:** `NetBufferWriter` / `NetBufferReader` (pooled, span-based, little-endian).

```csharp
public sealed class Ship : Node3D
{
    [Replicated(Interpolate = true)] public Vector3 NetPosition { get => Position; set => Position = value; }
    [Replicated] public int Hull { get; set; } = 100;

    [Rpc(RpcMode.Authority)] public void Thrust(Vector3 input) => NetPosition += input; // owner → server
}

var mp = engine.Multiplayer;
var shipScene = mp.RegisterScene("Content/Scenes/Ship.mscene"); // same scenes, same order, on both ends

// Server
mp.Host(7777, maxClients: 8);
mp.PeerJoined += peer => mp.Spawn<Ship>(shipScene, authority: peer);

// Client
mp.Connect("127.0.0.1", 7777);
myShip.RpcThrust(Vector3.UnitZ); // generated sender
```

Try it: `dotnet run --project MainframeEngine.Sandbox -- --server`, then `-- --client 127.0.0.1` in a second
terminal.

### Audio (`Audio/`)

`AudioServer` (registered by `Engine` at startup; a silent null device when there is no audio device) mixes Godot-style
audio nodes through a bus tree (Master → Music, SFX, UI, Voice: volume, mute, solo, effects) on
[SoundFlow](https://github.com/LSXPrime/SoundFlow):

```csharp
var hum = new AudioPlayer3D
{
    Stream = AudioStream.Load("Content/Audio/engine.ogg"), // WAV, OGG, MP3, FLAC; memory or streamed
    Bus = "SFX", VolumeDb = -6, Loop = true, Autoplay = true,
    UnitSize = 3, MaxDistance = 50, AttenuationModel = AttenuationModel.Inverse,
};
car.AddChild(hum);                                          // attenuated and panned around the listener
Servers.Get<AudioServer>()!.PlayOneShot(click, bus: "UI");  // fire and forget
```

Positional sounds are projected into the listener's frame (`AudioListener3D`, else the active camera), attenuated with
engine curves and smoothed on the audio thread; voices are pooled per bus with priority stealing; pause follows
`SceneTree.Paused` and `ProcessMode`. See [Audio](docs/design/audio.md).

### Steam Integration (`Steamworks/`)

Optional Steamworks.NET integration through an engine-owned `Steam` service, pumped every frame by `SteamServer`
when `EngineOptions.SteamAppId` is set; every wrapper is a no-op without Steam. See
[Steamworks](docs/design/steamworks.md).
- `Steam` — `TryInitialize` / `RunCallbacks` / `Shutdown`, status, user info, branch, friends, DLC
- `SteamLobby` / `SteamLobbyInfo` — create, join (including invites), search; `ConnectAddress` hands members to the
  network transport
- `SteamRichPresence`, `SteamOverlay`, `SteamAchievements` (with `StoreStats`), `SteamFriend`
- **Limitation:** no `steam_api` native ships (the SDK needs a partner login, and Steamworks.NET 2024.8.0 is
  x86-64 only), so Steam does not start yet; Steam Networking Sockets and avatar textures wait for it.

### Localization (`Localization/`)

gettext translations with [GetText.NET](https://github.com/perpetualKid/GetText.NET) catalogs managed by the engine. See
[Localization](docs/design/localization.md).

```csharp
label  = Tr._("Settings");                                // allocation-free lookup
status = Tr.N("{0} enemy left", "{0} enemies left", n);    // plural rules per language; n is {0}
open   = Tr.P("menu", "Open");                            // context
Tr.SetLocale("es");                                       // runtime switch: nodes re-translate (OnLocaleChanged)

[Export(Translatable = true)] public string Title { get; set; } = "";   // scene text, shown with Atr(Title)
```

- Catalogs: `Content/locale/<locale>/LC_MESSAGES/messages.mo`, fallback chain `pt_BR → pt → en`; compiled from `.po` at
  build time by the in-house `mf-l10n` (byte-identical to GNU `msgfmt`, so no gettext install is needed).
- `just l10n-extract` (C# via the GetText.NET extractor, RML documents and `[Export(Translatable)]` scene strings →
  `messages.pot` → every `.po` + the `qps` pseudo-locale), `just l10n-compile`, `just l10n-check`, `just l10n-stats`.

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
| Audio | SoundFlow (miniaudio) | 1.4.1 |
| OGG Vorbis decoding | NVorbis | 0.10.5 |
| Physics 3D | [Jitter2](https://github.com/notgiven688/jitterphysics2) | 2.9.0 |
| Physics 2D | [Box2D.NET](https://github.com/ikpil/Box2D.NET) (Box2D v3 port) | 3.1.654 |
| Vulkan on macOS | Silk.NET.MoltenVK.Native | 2.22.0 |

**Planned integrations** (chosen; not integrated yet; see [Milestones](docs/milestones.md)):

| System | Library | Milestone | Design doc |
|--------|---------|-----------|------------|
| Windowing / input | SDL2 via Silk.NET 2.22 (`Silk.NET.Windowing.Sdl`/`.Input.Sdl`) | M0 ✅ | [Build & platforms](docs/design/build-and-platforms.md#windowing-sdl2) |
| Physics 3D | [Jitter2](https://github.com/notgiven688/jitterphysics2) 2.9.0 | M6 ✅ | [Physics](docs/design/physics.md) |
| Physics 2D | [Box2D.NET](https://github.com/ikpil/Box2D.NET) 3.1.654 (Box2D v3 port; replaces box2d-netstandard, which is unmaintained) | M6 ✅ | [Physics](docs/design/physics.md) |
| Audio | [SoundFlow](https://github.com/LSXPrime/SoundFlow) 1.4.1 + [NVorbis](https://github.com/NVorbis/NVorbis) 0.10.5 for OGG | M7 ✅ | [Audio](docs/design/audio.md) |
| Game UI | [RmlUi](https://github.com/mikke89/RmlUi) (engine-owned C# binding) | M8 ✅ | [Game UI](docs/design/game-ui.md) |
| Localization | [GetText.NET](https://github.com/perpetualKid/GetText.NET) 10.0.1 (runtime), GetText.NET.Extractor (C# extraction tool) | M9 ✅ | [Localization](docs/design/localization.md) |

---

## Project Configuration

- **Target Framework:** `net10.0`
- **Nullable:** enabled
- **Unsafe:** enabled (required for Vulkan buffer/matrix operations)
- **Implicit usings:** enabled
- Content files (shaders, textures) are auto-copied to build output

---

## Shaders

GLSL sources in `Content/Shaders/` are compiled to SPIR-V by `dotnet build` (`glslc`, with shared includes in `include/`; the committed `.spv` files are the fallback when the Vulkan SDK is missing) and loaded at runtime through `ContentPaths`. Light/shadow limits come from `limits.json`. See [Shaders](docs/design/shaders.md).

**Engine shaders** (`MainframeEngine/Content/Shaders/`):
- **Mesh/** — batched mesh instances (`Mesh.vk.*`: StandardMaterial3D) and the object-ID pass (`MeshId.vk.frag`)
- **Spine/** — lit, shadow-receiving skeletal animation (`SpineLit.vk.*`)
- **Sky/** — procedural gradient, panoramic equirectangular, and cubemap variants
- **Shadows/** — depth pass shaders for 2D and omnidirectional point light shadow maps
- **SceneGrid/** — debug grid overlay
- **ImGui/** — Vulkan ImGui rendering backend
- **Post/** — fullscreen tonemap (exposure + ACES)

---

## Sandbox

`MainframeEngine.Sandbox` is the primary test project. `Game.cs` subclasses `Engine` and demonstrates:
- 3D scene with a `Camera3D` fly camera (hold right mouse to look, WASD/QE to move, Shift for speed; Alt toggles the cursor)
- `SkyPanoramic` backdrop
- Directional light with debug gizmos
- Shadow casting/receiving on meshes
- `MeshInstance3D` primitives with textured, blended and emissive materials, an imported glTF model, and `SceneGrid3d`
- `SpineNode` animation (SpineBoy character)
- ImGui debug overlay with FPS, delta time, frame count, frame time, VSync toggle, fullscreen toggle, MaxFPS selector
- Localization: English, Spanish and the `qps` pseudo-locale, switched from the HUD's language dropdown or the F12 overlay's Language menu (`--locale es`)
- Light gizmo visualization via `LightEnvironment.DrawLightGizmos()`
- Input handling (keyboard/mouse via Silk.NET)
- A quiet streamed, looping 3D hum attached to the spinning box, and ImGui bus faders/meters (`--qa-audio` plays a
  test melody through the real device and checks the audio server)

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
