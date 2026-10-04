# Current state — mainframe-engine

_Last updated: 2026-10-05 (M3a GPU resources/colour pipeline integrated onto M2, branch `integrate/m3a`)_

## Where things left off

- Engine runs on macOS (Apple Silicon: Sandbox ~121 fps Release, 2 dir + 1 point + 2 spot shadow
  casters, Spine, ImGui). Vulkan goes through MoltenVK; `Silk.NET.MoltenVK.Native` is bundled so no
  SDK install is needed to run. Windowing/input are SDL2 (Silk.NET SDL backend; GLFW removed).
- M0 and M1 are done (see `docs/milestones.md`, ADRs 0003/0004).
- M2 is done (lane `m2`, ADRs 0010–0012): Godot-style `SceneTree` owned by `Engine` (`Engine.Tree`/`Root`),
  servers (`RenderServer`) instead of `Node.Initialize`, light/camera/sky/grid nodes, `[Export]`/`[Signal]`
  registered by `MainframeEngine.Generators`, `.mscene`/`.mres` JSON with UIDs + `AssetDatabase`. The Sandbox
  loads `Content/Scenes/Sandbox.mscene` (regenerate with `--write-scene`).
- M6 physics is done (lane `m6`, ADRs 0020–0025): `PhysicsServer3D` (Jitter2 2.9.0) / `PhysicsServer2D`
  (Box2D.NET 3.1.654, pixels, 100 px/m), body/area/shape nodes, interpolated node transforms, layers (Godot OR),
  signals after the step, `DirectSpaceState` queries, engine-side `MoveAndSlide`, `DebugLines` collision-shape draw.

## Known gotchas

- Physics: keep Jitter2/Box2D calls inside `Src/Physics` (pinned APIs). Box2D keeps worlds in a process-wide table —
  2D unit tests share the `SerialBox2D` collection; Box2D.NET allocates inside `b2World_Step` (ADR 0023). Jitter2's
  regular solver isn't reproducible even single-threaded: render tests use `PhysicsSettings3D.Deterministic`.

- Scene tree: node constructors must stay cheap (the type registry instantiates every serialized type);
  acquire GPU objects through the render server (`VisualInstance3D.InitializeRenderResources`). Projects
  declaring node/resource types need the generator analyzer reference or they cannot be saved/loaded.
  `MainframeEngine.Timer` shadows `System.Threading.Timer` inside `MainframeEngine.*` namespaces. The old math
  cameras are `PerspectiveCamera`/`OrthographicCamera`; `Camera3D`/`Camera2D` are nodes.

- macOS: SDL and Silk.NET must bind the SAME Vulkan library — `VulkanLoaderBootstrap` enforces this
  (`Probe()` then `HandOffToSdl()` → `SDL_Vulkan_LoadLibrary`; Silk via `TryCreateVk`). Don't add a
  bare `Vk.GetApi()` call anywhere; take `Vk` from `IVulkanContext`. `MAINFRAME_VULKAN_LIBRARY=<path>`
  forces a library for QA.
- HiDPI: SDL reports window size AND Silk's `IWindow.FramebufferSize` in points for Vulkan windows; use
  `Engine.FramebufferSize` (pixels, `SDL_Vulkan_GetDrawableSize`).
- Per-frame GPU resources are keyed by `IVulkanContext.FrameSlot` (2 slots), never by swapchain image.
- `ShadowSystem.RenderShadows` once per frame (per-frame-slot dynamic-offset light-VP ring).
- Shadow comparison samplers are IMMUTABLE (baked into the set-2 layout, shared by ShadowSystem and
  the renderer's ShadowFallback) — required by MoltenVK (mutableComparisonSamplers=false).
  MaxShadowSpot is 7, one less than LightEnvironment.MaxSpot, to fit MoltenVK's 16 per-stage sampler
  limit (4 dir + 7 spot + 4 point + 1 material texture); the 8th spot light casts no shadow.
  The limits live only in `Content/Shaders/limits.json` (generated C# + `include/limits.glsl`); shaders
  compile in `dotnet build`, but run `just shaders` after shader/include edits to refresh the committed
  `.spv` fallback + `shaders.lock`.
- M3 (lane m3a): GPU memory only through `GpuAllocator`/`GpuBuffer`/`GpuImage`/`GpuTexture`, uploads
  through `UploadQueue`, `Dispose` through `DeletionQueue` (no Queue/DeviceWaitIdle). Scene renders into
  an HDR target, tonemapped (exposure 1.3, ACES) into a UNORM swapchain; ImGui after the tonemap.
  Authored colours are sRGB (converted to linear by the engine). MoltenVK: mutable-format swapchain
  UNORM views go stale — keep the default UNORM swapchain. Node-side shapes still use the old raw
  Vulkan path (m3b). Integrated with M2: `RenderServer.RenderMain` writes set 0 (`Frame.Begin`) per frame;
  `WorldEnvironment.AmbientColor` defaults to `LightEnvironment.DefaultAmbientColor`; ADRs are 0005–0007
  (m3a) and 0010–0012 (M2); unused: 0008, 0009, 0013+.
- Validation is on by default only in Debug builds (`EngineOptions.EnableValidation`).
- Render tests / `just qa` need the display awake (`caffeinate -u`). The unbundled `dotnet` Sandbox
  process can't be driven by computer-use; use the `--qa-*` scripted flags instead.
- ENet macOS natives are x86_64-only (won't load on Apple Silicon); Steamworks.NET has
  no osx-arm64 assets.

## Next steps

- Record lavapipe goldens for the new render tests (multi-light, spine-no-shadows) from CI.
- Replace/rebuild ENet natives for osx-arm64 before using networking on macOS.
