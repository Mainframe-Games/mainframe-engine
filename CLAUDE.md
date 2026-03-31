# CLAUDE.md — Mainframe Engine

## Build & Run

```bash
dotnet build MainframeEngine.sln
dotnet run --project MainframeEngine.Sandbox
```

Shaders must be compiled to SPIR-V before running if changed:

```bash
glslc path/to/shader.vk.vert -o path/to/shader.vk.vert.spv
glslc path/to/shader.vk.frag -o path/to/shader.vk.frag.spv
```

## Project Structure

- `MainframeEngine/Src/Core/` — `Engine` base class, `GameTime`, `FPSCounter`
- `MainframeEngine/Src/Nodes/` — scene graph: `Node`, `Node3D`, `SpineNode`, shapes
- `MainframeEngine/Src/Rendering/` — Vulkan renderer, cameras, sky, shadows, scene grids, Spine renderer
- `MainframeEngine/Src/Lighting/` — `LightEnvironment`, `DirectionalLight`, `PointLight`, `SpotLight`
- `MainframeEngine/Src/Steamworks/` — Steam API wrappers
- `MainframeEngine/Src/Debugging/` — `Log`
- `MainframeEngine.Sandbox/` — test game; the only executable project
- `Plugins/Spine/` — Spine C# runtime (vendored, do not modify)
- `Examples/` — standalone tutorial projects, not part of the engine

## Key Patterns

### Extending the Engine

Games subclass `Engine` (not an interface). Override the four abstract methods:

```csharp
public sealed class Game(in EngineOptions options) : Engine(options)
{
    protected override void OnImGui(in GameTime gameTime) { }
    protected override void OnUpdate(in GameTime gameTime) { }
    protected override void OnShadowPass(in GameTime gameTime) { }
    protected override void OnRenderMainPass(in GameTime gameTime) { }
}
```

Call `base.OnLoad()` at the start of any `OnLoad` override. Call `base.OnClose()` at the end of any `OnClose` override (disposes renderer, ImGui, input).

### Node Initialization

`Node.Initialize(Renderer, shadowSystem)` must be called in `OnLoad` after both the renderer and `ShadowSystem` are created, before any nodes/shapes are constructed.

### Frame Order

1. `OnImGui` — build ImGui windows (called before update, inside ImGui frame)
2. `OnUpdate` — game logic
3. `OnShadowPass` — depth pre-pass (no render pass active; use raw command buffers)
4. `OnRenderMainPass` — sky first, then geometry

### Shadow Pass

`ShadowSystem.RenderShadows` takes two draw callbacks — one for 2D (directional) shadows and one for point light shadows. Lambdas are called per-light internally.

### SpineNode

Use `SpineNode` (not `SpineModel` or `SpineRenderer` directly) for scene integration. Call `OnUpdate` in your update loop and `OnRender` in your render pass.

## Rendering Backend

Vulkan 1.2 only for now. `IVulkanContext` must be cast from `IRenderer` to access Vulkan-specific operations (begin render pass, shadow system, ImGui controller). Always guard casts:

```csharp
if (Renderer is IVulkanContext vk) { ... }
```

## Unsafe Code

The engine uses `unsafe` for Vulkan buffer/matrix operations. This is expected — do not remove `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` from the csproj.

## Dependencies

- Silk.NET — windowing, input, Vulkan bindings, Assimp
- ImGui.NET — debug UI (Vulkan backend in `VulkanImGuiController`)
- Steamworks.NET — Steam platform (optional; only activate if Steam is running)
- StbImageSharp — texture/image loading

Do not add NuGet packages without discussing the dependency first.
