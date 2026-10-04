# CLAUDE.md — Mainframe Engine

## Build & Run

Local commands are `just` recipes (run `just` to list them); CI calls `dotnet` directly.

```bash
just build            # dotnet build MainframeEngine.sln — 0 warnings, warnings are errors
just test             # unit tests (Tests/MainframeEngine.Tests)
just test-render      # render tests: goldens + validation gate + allocation gate (needs a GPU/display)
just sandbox          # dotnet run --project MainframeEngine.Sandbox
just qa               # Sandbox --qa-capture → PNG screenshots in artifacts/qa
                      #   (Sandbox also takes --qa-resize WxH@frame, --qa-minimize frame, --qa-input frame)
just golden-update    # re-record render-test goldens for this driver; inspect the PNGs before committing
just format           # dotnet format (format-check is what CI runs)
just bench            # benchmarks vs Tests/MainframeEngine.Benchmarks/baseline.json
```

Build settings live in `Directory.Build.props` / `Directory.Packages.props` (central package
versions — never put `Version` on a `PackageReference`); the SDK is pinned in `global.json`.

Shaders must be recompiled to SPIR-V after any change, and the `.spv` files plus
`MainframeEngine/Content/Shaders/shaders.lock` committed (CI fails on stale `.spv`):

```bash
just shaders          # glslc --target-env=vulkan1.2 + spirv-val on every shader, rewrites shaders.lock
just shaders-check
```

Rendering changes must keep the render tests green; if output changes intentionally, run
`just golden-update`, inspect the PNGs and commit them (see `docs/design/testing.md`). Per-frame code
must not allocate (allocation gate) and must produce no validation warnings.

### macOS

Vulkan runs through MoltenVK, bundled via the `Silk.NET.MoltenVK.Native` package — no install
needed to run. The Vulkan SDK (lunarg.com) is recommended for development (validation layers,
`glslc`). Windowing and input are SDL2 (Silk.NET SDL backend; GLFW is not referenced).
`VulkanLoaderBootstrap` (called in the `Engine` constructor after SDL is selected, before the
window exists) probes for a Vulkan library and hands the same one to SDL (`SDL_Vulkan_LoadLibrary`)
and to Silk.NET's `Vk` — required because modern macOS dyld no longer searches `/usr/local/lib` for
leaf-name dlopen, and the two would otherwise bind different libraries whose instances are not
interchangeable. HiDPI: use `Engine.FramebufferSize` (pixels), not `Window.FramebufferSize` (points
under SDL). The renderer enables `VK_KHR_portability_enumeration`/`VK_KHR_portability_subset`
capability-conditionally; Windows/Linux are unaffected. Render tests and `just qa` need the display
awake (`caffeinate -u -t 600 &`).

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
- `Tests/` — unit tests, render tests (+ host), benchmarks; see `docs/design/testing.md`
- `build/` — scripts shared by `justfile` and CI (`shaders.sh`)
- `docs/` — design docs (`docs/design/`, one topic per file) and the roadmap (`docs/milestones.md`); update the matching doc when changing a subsystem

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

Call `base.OnLoad()` at the start of any `OnLoad` override. Call `base.OnClose()` at the end of any `OnClose` override (disposes renderer, ImGui, input; keeps the exit code set by `Quit`).

`EngineOptions.EnableValidation` defaults to on in Debug builds and off in Release; set it to override.

### Node Initialization

`Node.Initialize(Renderer, shadowSystem)` must be called in `OnLoad` after the renderer (and the `ShadowSystem`, if any) are created, before any nodes/shapes are constructed. The shadow system is optional: without one, lit pipelines bind the renderer's "no shadows" fallback set (same set indices).

### Frame Order

1. `OnImGui` — build ImGui windows (called before update, inside ImGui frame)
2. `OnUpdate` — game logic
3. `OnShadowPass` — depth pre-pass (no render pass active; use raw command buffers)
4. `OnRenderMainPass` — sky first, then geometry (`node.Draw(camera, lights)`)

Shadow pass and main pass only run when `IVulkanContext.FrameStarted` (a frame can be skipped while the
swapchain is rebuilt; while minimised the engine renders nothing and blocks on window events).

### Per-frame resources

Key per-frame GPU resources (UBOs, dynamic vertex buffers, their descriptor sets) by
`IVulkanContext.FrameSlot` and size them `IVulkanContext.MaxFramesInFlight` — never by swapchain image
(`SwapchainImageCount`/`CurrentImageIndex` are driver-chosen and change on recreation).

### Shadow Pass

`ShadowSystem.RenderShadows` takes two draw callbacks — one for 2D (directional and spot) shadows and one for point light shadows (called per cube face). Call it at most once per frame; each sub-pass gets its own light matrix from a per-frame-slot dynamic-offset ring. Use the `RenderShadows<TState>` overload with static lambdas in per-frame code (no closure allocations).

### SpineNode

Use `SpineNode` (not `SpineRenderer` directly) for scene integration. Call `OnUpdate` in your update loop, `DrawShadow2D`/`DrawShadowPoint` from the shadow callbacks, and `Draw(camera, lights)` in `OnRenderMainPass`. `SpineScale` applies immediately; `SetAnimation` replaces track 0 (`QueueAnimation` appends).

## Rendering Backend

Vulkan 1.2 only for now. `IVulkanContext` must be cast from `IRenderer` to access Vulkan-specific operations (begin render pass, shadow system, ImGui controller). Always guard casts:

```csharp
if (Renderer is IVulkanContext vk) { ... }
```

## Unsafe Code

The engine uses `unsafe` for Vulkan buffer/matrix operations. This is expected — do not remove `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` from the csproj.

## Dependencies

- Silk.NET — windowing + input (SDL2 backend: `Silk.NET.Windowing.Sdl`, `Silk.NET.Input.Sdl`), Vulkan bindings, Assimp
- ImGui.NET — debug UI (Vulkan backend in `VulkanImGuiController`)
- Steamworks.NET — Steam platform (optional; only activate if Steam is running)
- StbImageSharp — texture/image loading

Do not add NuGet packages without discussing the dependency first. Versions are central in
`Directory.Packages.props`.

## Project memory

@import memory/README.md
@import memory/context/current-state.md
@import memory/context/progress-log.md
