# Architecture Overview

## Purpose

Mainframe Engine is a C# (.NET 10) game engine built on Vulkan 1.2 through Silk.NET. It is a
learning-oriented engine with a Godot-style scene model: a game is a project (`project.mfproj`) whose node library
`GameHost` runs (or, for the editor and tests, an `Engine` subclass), with nodes in the engine-owned `SceneTree`,
usually loaded from a scene file. The engine provides windowing, a Vulkan renderer, the node tree with servers behind
it, scene/resource files, materials and model import, lighting, cascaded shadows, skies, Spine skeletal animation,
RmlUi game UI, physics, audio, replication, localization and an RmlUi developer overlay (F12); the editor
(`MainframeEngine.Editor`) is built on the same engine and UI stack.

![Architecture layers](../images/architecture-layers.svg)

## Assemblies

| Project | Kind | Role |
|---|---|---|
| [`MainframeEngine`](../../MainframeEngine/MainframeEngine.csproj) | class library | The engine. Ships `Content/**` (compiled `.spv` shaders, assets) to dependants' output; shaders compile during the build (`build/Shaders.targets`). |
| [`MainframeEngine.Generators`](../../MainframeEngine.Generators/MainframeEngine.Generators.csproj) | Roslyn source generator (netstandard2.0, referenced as an analyzer) | Registers every node/resource type's `[Export]` properties and `[Signal]` events. See [Scene serialization](scene-serialization.md#source-generator). |
| [`MainframeEngine.L10n`](../../Tools/MainframeEngine.L10n/MainframeEngine.L10n.csproj) (`mf-l10n`) | exe (build tool) | Localization tooling: RML/scene extraction, `.po` update, pseudo-locale, `.po` → `.mo` compiler run by `build/Localization.targets`. See [Localization](localization.md). |
| [`Examples/Demo`](../../Examples/Demo/) | game project (own solution, not in `MainframeEngine.slnx`) | The Demo: a `GameHost` game with one scene per engine feature. See [Demo](demo.md). |
| [`MainframeEngine.Editor`](../../MainframeEngine.Editor/MainframeEngine.Editor.csproj) | exe | The editor (`EditorApp : Engine`, RmlUi panels); loads game projects' assemblies into collectible contexts and plays them in separate processes. See [Editor](editor.md). |
| [`Templates/MainframeEngine.Templates`](../../Templates/MainframeEngine.Templates/) | `dotnet new` template package (not in the solution) | `mfgame`: a game's node library + `GameHost` desktop project + `project.mfproj`. See [Projects & GameHost](project-and-gamehost.md). |
| `Tests/*` | xUnit v3 test projects, render-test host, BenchmarkDotNet | See [Testing](testing.md). |
| `Plugins/Spine/spine-csharp` | class library (git submodule) | Spine C# runtime. Vendored — do not modify. |

## Source layout (`MainframeEngine/Src`)

| Folder | Contents | Doc |
|---|---|---|
| `Core/` | `Engine`, `EngineOptions`, `GameTime`, `FPSCounter`, `ExitCode`, `RenderingBackend` | [Engine lifecycle](engine-lifecycle.md) |
| `Scene/` | `Node`, `SceneTree`, `SceneViewport`, `World3D`, `Node3D`/`Node2D`, `Transform3D`/`Transform2D`, `NodePath`, `NodeId`, `Timer`; `Nodes3D/` (`VisualInstance3D`, `Camera3D`, lights, `WorldEnvironment`, `Grid3D`), `Nodes2D/` (`Camera2D`), `Input/` | [Scene graph & nodes](scene-graph-and-nodes.md) |
| `Nodes/` | `SpineNode`, `NetworkNode` | [Scene graph & nodes](scene-graph-and-nodes.md) |
| `Scene/Nodes3D/GeometryInstance3D.cs`, `Scene/SubViewport.cs` | `GeometryInstance3D`, `MeshInstance3D`, `Sprite3D`, `SubViewport` | [Materials & meshes](materials-and-meshes.md) |
| `Rendering/Resources/` | `Mesh`/`ArrayMesh`/`MeshSurface`, primitive meshes, `StandardMaterial3D`, `Texture2D` | [Materials & meshes](materials-and-meshes.md) |
| `Rendering/Meshes/` | `MeshRenderer` (batching), `PipelineStateCache`, `DrawList`, `Aabb`/`Frustum`, picking, sub-viewport compositor | [Materials & meshes](materials-and-meshes.md) |
| `Resources/Import/` | `AssetImporters`, `TextureImporter`, `ModelImporter` (Assimp) | [Asset pipeline](asset-pipeline.md) |
| `Servers/` | `IServer`, `ServerRegistry`, `RenderServer` | [Scene graph & nodes](scene-graph-and-nodes.md#servers-and-render-nodes) |
| `Resources/` | `Resource`, `PackedScene`, `ResourceLoader`, `SceneSaver`/`ResourceSaver`, `AssetDatabase`, `AssetUid` | [Scene serialization](scene-serialization.md) |
| `Serialization/` | attributes, `TypeRegistry`, `NodeTypeInfo`, codecs, scene reader/writer | [Scene serialization](scene-serialization.md) |
| `Rendering/Vulkan/` | `VulkanRenderer`, `IVulkanContext`, `VulkanLoaderBootstrap` | [Vulkan renderer](vulkan-renderer.md) |
| `Rendering/Shadows/` | `ShadowSystem` | [Shadow system](shadow-system.md) |
| `Rendering/Sky/` | `SkyEnvironment` + Procedural/Panoramic/Cubemap | [Sky](sky.md) |
| `Rendering/Spine/` | `SpineRenderer`, `SpineTextureLoader` | [Spine](spine.md) |
| `Rendering/SceneGrid/` | `SceneGrid`, `SceneGrid2d`, `SceneGrid3d` | [Scene grid](scene-grid.md) |
| `Rendering/Camera/` | `ICamera`, `PerspectiveCamera`, `OrthographicCamera` (math; the nodes wrap them) | [Cameras & input](cameras-and-input.md) |
| `Rendering/Gizmos/`, `Debugging/DevOverlay/` | `ScreenGizmoBatch`, `LightGizmos`, `AxisGizmo`, `DevOverlay` | [Developer overlay](dev-overlay.md) |
| `Debugging/`, `Utils/` | `Log` and its sinks | [Project & GameHost](project-and-gamehost.md#log-routing) |
| `Lighting/` | `LightEnvironment`, `Light` + Directional/Point/Spot | [Lighting](lighting.md) |
| `Networking/` | `MultiplayerApi` (replication, RPCs), messages, transports (ENet, loopback, simulated), `PeerId`, `NetBuffer*` | [Networking](networking.md) |
| `Physics/` | `PhysicsServer3D` (Jitter2), `PhysicsServer2D` (Box2D.NET), body/area/shape nodes, queries, `DebugLines` | [Physics](physics.md) |
| `Audio/` | `AudioServer` (SoundFlow), buses, audio nodes, streams, decoders | [Audio](audio.md) |
| `UI/` | `Rml/` binding over `mfrmlui`, `VulkanUiRenderer`, `UiServer`/`UiLayer`/`UiDocument` | [Game UI](game-ui.md) |
| `Project/`, `EditorLink/` | `ProjectSettings`, `GameHost`/`GameSession`, `InputMap`, `GameAssemblyLoader`; the game ↔ editor protocol | [Projects & GameHost](project-and-gamehost.md) |
| `Imaging/` | `Png` codec | [Testing](testing.md#frame-capture) |
| `Steamworks/` | Static Steam wrappers, `SteamServer` | [Steamworks](steamworks.md) |
| `Localization/` | `Tr`, `LocalizationOptions`, `LocaleId`, catalogs, `RmlLocalization`, `ITextTranslator`, `FontFallbackTable` | [Localization](localization.md) |

## Dependency graph

```mermaid
flowchart LR
    Demo["Examples/Demo<br/>(GameHost desktop project)"] --> Engine["MainframeEngine"]
    Editor["MainframeEngine.Editor"] --> Engine
    Game["mfgame projects<br/>(GameHost desktop project)"] --> Engine
    Game -. analyzer .-> Gen
    Demo -. analyzer .-> Gen["MainframeEngine.Generators"]
    Demo -. build tool .-> L10n["mf-l10n<br/>(Tools/MainframeEngine.L10n)"]
    L10n --> Engine
    Engine -. analyzer .-> Gen
    Engine --> SpineRT["spine-csharp<br/>(submodule)"]
    Engine --> Silk["Silk.NET<br/>Windowing + Input (SDL2) · Vulkan · MoltenVK"]
    Engine --> Stb["StbImageSharp"]
    Engine --> ENet["ENet-CSharp"]
    Engine --> Steam["Steamworks.NET"]
    Engine --> Jitter["Jitter2 (3D physics)"]
    Engine --> Box2D["Box2D.NET (2D physics)"]
    Engine --> GetText["GetText.NET"]
    Engine --> Audio["SoundFlow · NVorbis"]
    Engine --> Rml["mfrmlui<br/>(RmlUi + FreeType, in-house native shim)"]
    Engine --> Assimp["Silk.NET.Assimp"]
```

## Design principles in the current code

- **Games are node libraries.** A game project's code is node types; `GameHost` runs it from `project.mfproj` (no
  `Engine` subclass). An `Engine` subclass is still supported (the editor, the render-test host); its three legacy hooks
  (`OnUpdate`, `OnShadowPass`, `OnRenderMainPass`) are optional.
- **The engine owns the scene.** A `SceneTree` (Godot model) runs lifecycle, physics/process, deferred
  calls, transform sync and input for every node; behaviour is C# node subclasses. Scenes are data
  (`.mscene`) loaded through `ResourceLoader`; node properties are discovered by a source generator, not
  reflection.
- **Nodes are front-ends to servers.** Nodes hold editable state; servers (`RenderServer`, the physics servers
  `PhysicsServer3D`/`PhysicsServer2D` (M6), `AudioServer` (M7), `UiServer` (M8)) hold GPU, simulation, audio and native
  objects and are reached through `SceneTree.Servers`. Physics library calls (Jitter2, Box2D.NET) never leave the
  physics spaces ([Physics](physics.md)).
- **Vulkan is the only backend.** `IRenderer` is backend-neutral in name, but everything that draws
  casts to `IVulkanContext`. `EngineOptions.RenderingBackend` is not consulted.
- **Shared GPU state.** Meshes are batched by the render server with pipelines from a state-hash
  `PipelineStateCache`, shared per-frame descriptor sets (set 0/1) and memory from the in-house `GpuAllocator`
  ([GPU resources](gpu-resources.md), [Materials & meshes](materials-and-meshes.md)); the sky, grid and Spine
  renderers keep their own pipelines, created through the render server when their node enters the tree.
- **Per-frame-slot resources.** Uniform buffers, dynamic vertex buffers and their descriptor sets are
  indexed by `IVulkanContext.FrameSlot` (`MaxFramesInFlight = 2`), never by swapchain image, so they
  survive swapchain image-count changes (see [Vulkan renderer](vulkan-renderer.md#per-frame-slot-resources)).
- **No static service locators for nodes.** The old `Node.Initialize(renderer, shadowSystem)` globals are
  gone; per-world state lives in `World3D`, so the editor can render several worlds.

## Ownership & disposal

| Object | Created by | Disposed by |
|---|---|---|
| Window | `Engine` ctor | `Engine.Dispose` |
| `SceneTree` (root viewport, `World3D` with its `LightEnvironment`) | `Engine` ctor | `Engine.OnClose` (`Tree.Shutdown()` frees every node) |
| Input context, `VulkanRenderer`, servers (`RenderServer` → `ShadowSystem`, `DebugLinesRenderer`; `PhysicsServer3D/2D` → one space per world) | `Engine.OnLoad` | `Engine.OnClose` (servers after the tree, before the renderer) |
| Nodes and their GPU objects (shapes, Spine, sky, grid) | the game / `PackedScene.Instantiate` (GPU objects: the render server on enter) | `Free`/`QueueFree`, else the tree shutdown; orphans by the render server |
| Loaded resources (`PackedScene`, `.mres`) | `ResourceLoader.Load` | last `Release()`, else `ResourceLoader.ClearCache()` in `Engine.OnClose` |

## Related docs

[Engine lifecycle](engine-lifecycle.md) · [Build & platforms](build-and-platforms.md) ·
[Milestones](../milestones.md)
