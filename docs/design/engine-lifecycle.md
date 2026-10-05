# Engine Lifecycle

## Purpose

`Engine` owns the window, input, renderer, ImGui, the engine servers and a `SceneTree`, and drives the
game loop through Silk.NET's window events. Games subclass it, put nodes in the tree (usually a scene
loaded from a file) and let the tree run the frame; four optional legacy hooks remain for code that draws
or updates by hand.

## Key types

| Type | File | Notes |
|---|---|---|
| `Engine` | [Engine.cs](../../MainframeEngine/Src/Core/Engine.cs) | `public abstract class Engine : IDisposable`; `Tree` (`SceneTree`), `Root`, `Servers` |
| `EngineOptions` | [Engine.cs](../../MainframeEngine/Src/Core/Engine.cs) | `struct` with `required GameName`, `RenderingBackend = Vulkan`, `WindowSize = 800×600`, `IconPath`, `VSync = true`, `EnableValidation = DefaultEnableValidation` (**true in Debug, false in Release**), `EnableFrameCapture`, `WindowVisible = true`, `MaxFrames` (0 = until closed), `FixedDeltaTime` (0 = wall clock), `PhysicsTicksPerSecond = 60`, `Physics3D`/`Physics2D` (physics settings; `Physics2D.PixelsPerMeter = 100`), `DebugCollisionShapes`, `SteamAppId` (0 = no Steam), `Audio` (`AudioOptions`: `Enabled = true`, `Device = Auto`, 48 kHz, 10 ms, `BusLayoutPath`), `EnableUi = true` (registers the `UiServer`, M8), `Ui` (`UiServerOptions`), `DevOverlayVisible = true` (ImGui; F12 toggles `Engine.DevOverlayVisible`) |
| `FrameCapture` | [FrameCapture.cs](../../MainframeEngine/Src/Rendering/FrameCapture.cs) | RGBA8 pixels of a rendered frame, `SavePng(path)` |
| `GameTime` | [GameTime.cs](../../MainframeEngine/Src/Core/GameTime.cs) | `FrameCount`, `DeltaTime`, `FramesPerSecond`, `FramesTimeMs` |
| `FPSCounter` | [FPSCounter.cs](../../MainframeEngine/Src/Core/FPSCounter.cs) | 500 ms sampling window |
| `ExitCode` | [ExitCode.cs](../../MainframeEngine/Src/Core/ExitCode.cs) | `Ok = 0`, `Error = 1` |
| `RenderingBackend` | [RenderingBackend.cs](../../MainframeEngine/Src/Core/RenderingBackend.cs) | `Vulkan` only |

## Writing a game

Game projects normally do not subclass `Engine`: `GameHost : Engine` runs a `project.mfproj` (window, physics,
input map, autoloads, main scene) — see [Projects & GameHost](project-and-gamehost.md). Subclassing `Engine` (as the
editor and render tests do) remains supported:

```csharp
public sealed class Game() : Engine(new EngineOptions { GameName = "My Game" })
{
    protected override void OnLoad()
    {
        base.OnLoad();                                          // input, renderer, ImGui, servers
        Tree.ChangeSceneToFile("Content/Scenes/Main.mscene");   // the tree processes and renders it
    }

    // Optional legacy hooks (virtual since M2): OnImGui, OnUpdate, OnShadowPass, OnRenderMainPass.
    protected override void OnImGui(in GameTime t) { }
}
```

- Call `base.OnLoad()` **first** (it creates input, renderer, ImGui, the `RenderServer` — and the
  `SteamServer` when `SteamAppId` is set, and `MultiplayerApi` as `Engine.Multiplayer` — and routes input into the tree).
- Call `base.OnClose()` **last** (it frees the scene tree, disposes the servers, clears the resource
  cache, then disposes ImGui, input and the renderer).
- Behaviour lives in node types (`OnProcess`, `OnPhysicsProcess`, `OnInput`, …), see
  [Scene graph & nodes](scene-graph-and-nodes.md). A game can still keep everything in the legacy hooks:
  an empty tree costs nothing.
- `Run()` blocks until the window closes and returns an `ExitCode`. `Quit(code)` records the code and
  requests shutdown; the window closes at the end of that iteration's render (SDL raises `Closing`
  synchronously inside `Close()`, and `OnClose` disposes the renderer, so closing mid-update would
  dispose it under a running frame). `Run()` then returns the code — covered by the
  `QuitWithErrorReturnsErrorExitCode` render test. `MaxFrames` closes the same way.
- `Engine.FramebufferSize` is the drawable size in **pixels**; `Window.Size` is in points. For a
  camera's aspect ratio inside `OnRenderMainPass`, use `IVulkanContext.SwapchainExtent` — the image
  actually being rendered (it can lag the window by a frame during a resize).

## Startup

1. **Constructor:** stores options → selects SDL (`SdlWindowing`/`SdlInput.RegisterPlatform()`,
   `Window.PrioritizeSdl()`) → `VulkanLoaderBootstrap.Initialize()` (macOS probe +
   `SDL_Vulkan_LoadLibrary` handoff, see [Build & platforms](build-and-platforms.md#windowing-sdl2))
   → creates the window from
   `WindowOptions.DefaultVulkan` (title `"{GameName} ({RenderingBackend})"`, API 1.2) → subscribes
   `Load`, `FramebufferResize`, `Update`, `Render`, `Closing`.
2. **`Load`:** centres the window on its monitor (skipped when none is reported, e.g. a sleeping
   macOS display), then **`OnLoad()`** (base): `Window.CreateInput()` (SDL input) →
   `new VulkanRenderer(Window, { EnableValidation, VSync, EnableFrameCapture })` →
   `new VulkanImGuiController(...)` → `Servers.Register(new RenderServer(Renderer))` (+ `SteamServer`, + `MultiplayerApi.Attach(Tree)`)
   → `AudioServer.Create(options.Audio, Tree)` when `Audio.Enabled` (never fails startup: no device means the
   silent null device; see [Audio](audio.md)) → `PhysicsServer3D`, `PhysicsServer2D` ([Physics](physics.md))
   → `new InputRouter(InputContext, Tree)` → `SetWindowIcon(IconPath)` (StbImageSharp, RGBA).
   The `SceneTree` itself is created in the constructor (no GPU needed), so nodes can be built before
   `OnLoad`; visuals that enter the tree before the render server exists get their GPU objects lazily.

### Deterministic runs and frame capture

Tests and QA tools set `FixedDeltaTime` (every update gets that delta instead of wall-clock time) and
`MaxFrames` (the window closes after that many rendered frames; `RenderedFrameCount` excludes skipped
frames). `CaptureFrame()` — legal from `OnImGui`, `OnUpdate` or a render hook, and only with
`EnableFrameCapture` — copies the frame being built back to the CPU; after `EndFrame` the engine calls
`protected virtual OnFrameCaptured(FrameCapture)`. See [Testing](testing.md).

## The frame

Update and Render are separate Silk.NET window events. ImGui is built **before** game update, the
scene tree ticks **after** the legacy `OnUpdate` hook, and the shadow pass runs with the command buffer
open but no render pass active.

```mermaid
sequenceDiagram
    autonumber
    participant W as Silk Window
    participant E as Engine
    participant G as Game (subclass)
    participant R as VulkanRenderer
    participant I as ImGui controller

    W->>E: Update(delta)
    E->>E: FPSCounter.Update(), fill GameTime
    E->>I: Update(delta) → ImGui.NewFrame()
    E->>G: OnImGui(gameTime) — only while DevOverlayVisible (F12)
    E->>G: OnUpdate(gameTime)
    E->>E: Tree.Tick(gameTime) — physics steps, OnProcess, deferred/QueueFree, transform sync, frame servers (UiServer: UI update + render into its command list)

    W->>E: Render(delta)
    alt minimised (WindowState or 0×0 drawable)
        E->>I: DiscardFrame() → ImGui.EndFrame()
        Note over E,W: IsEventDriven = true: the loop blocks on window events until restored
    else
        E->>R: BeginFrame() (slot fence wait, acquire, begin cmd buffer)
        alt FrameStarted
            E->>G: OnShadowPass(gameTime) — no render pass active
            E->>R: RenderServer.RenderShadows(Root) — the tree's shadow casters
            E->>R: BeginRenderPass() — HDR scene target: clear color (linear) + depth
            E->>R: RenderServer.RenderMain(Root) — set 0 (camera + lights), sky, then the tree's visuals
            E->>G: OnRenderMainPass(gameTime) — anything drawn by hand
            E->>I: Render() — BeginOverlayPass (UI layers offscreen, tonemap into the swapchain, UI composite), then ImGui (DiscardFrame when the overlay is hidden; EndFrame then runs BeginOverlayPass)
        else swapchain out of date / being rebuilt
            E->>I: DiscardFrame()
        end
        E->>R: EndFrame() (end pass, submit, present)
    end
```

### Minimise

While the window is minimised (`WindowState.Minimized`, or `Engine.FramebufferSize` is 0×0) `OnRender`
renders nothing, closes the ImGui frame, and switches the window to `IsEventDriven` so Silk's loop
blocks in `SDL_WaitEvent` instead of spinning; the first render after restore switches it back. The
renderer likewise refuses to rebuild a 0×0 swapchain and keeps the request pending (no busy wait).
Minimised for 1.5 s on macOS this ran 2 updates and ~0 frames (measured with the old test game's `--qa-minimize`
flag, which was removed with it; no automated test covers it).

### `MaxFPS` and VSync

- `Engine.MaxFPS` sets both `Window.FramesPerSecond` and `Window.UpdatesPerSecond` (≤ 0 → unlimited).
  It has no effect while VSync is on.
- `Renderer.VSync` changes the present mode by forcing a swapchain rebuild. See
  [Vulkan renderer](vulkan-renderer.md#swapchain-recreation).

### `GameTime` and `FPSCounter`

`FPSCounter.Update()` counts frames and, every ≥ 500 ms, computes `Fps = frames / seconds` and
`Ms = 1000 / Fps` (integer). It is ticked from the **Update** event, so it measures update rate,
not presented frames.

`Engine.LastFrameCpuMilliseconds` is the CPU cost of the last rendered frame: its updates (ImGui, `OnUpdate`, the
tree tick) plus `RenderFrame` (draw-list build, command recording, submit), minus the time the renderer was blocked
on the GPU or the swapchain (`IVulkanContext.LastFrameWaitMilliseconds`: the frame slot's fence, acquire, present and
a capture's read-back). It does not depend on GPU speed, so the render tests' frame-rate gate uses it on CPU Vulkan
devices ([Testing](testing.md#render-tests)).

## Shutdown

`Closing` → `OnClose()` (base): dispose the input router → `Tree.Shutdown()` (frees every node, so
visuals release their GPU objects and audio players stop) → `Servers.Dispose()` (reverse registration order — registered Render, Steam, Audio, Physics 3D/2D, UI: the UI server
releases RmlUi and its GPU objects first, the physics servers their worlds, the audio server stops the device and
streaming thread; the render server releases anything still alive and its `ShadowSystem` last) → `ResourceLoader.ClearCache()` → dispose ImGui controller →
dispose input → dispose renderer.
`Run()` returns the exit code (`Ok` unless `Quit(code)` set another). `Dispose()` disposes the window.

## Invariants

- `Renderer` is `null!` until `base.OnLoad()` runs.
- `Servers.Render` is null until `base.OnLoad()` runs; nodes reach servers through `Tree.Servers`
  (there is no static `Node.Initialize` any more). See [Scene graph & nodes](scene-graph-and-nodes.md).
- Anything drawn by hand must be recorded inside `OnShadowPass` or `OnRenderMainPass`; the command
  buffer is only valid while `IVulkanContext.FrameStarted` is true.

## Known issues

- **`EngineOptions.RenderingBackend` is ignored**; `VulkanRenderer` is always created.
- **FPS counts updates, not presents.**
- README/CLAUDE.md show `Game(in EngineOptions options)`; a parameterless primary constructor that passes options
  to `Engine` directly works too.
- The tree runs one fixed-step loop per update event, so physics process follows `UpdatesPerSecond`
  frames (accumulated), not a separate thread.

## Related docs

[Architecture overview](architecture-overview.md) · [Vulkan renderer](vulkan-renderer.md) ·
[Demo](demo.md) · [Build & platforms](build-and-platforms.md) · [Scene graph & nodes](scene-graph-and-nodes.md)
