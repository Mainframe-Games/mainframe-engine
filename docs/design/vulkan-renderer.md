# Vulkan Renderer

## Purpose

`VulkanRenderer` owns the Vulkan instance, device, swapchain, main render pass, command buffers and
frame synchronization. Everything else draws by recording into `IVulkanContext.CurrentCommandBuffer`
between `BeginFrame()` and `EndFrame()`.

## Key types

| Type | File | Visibility |
|---|---|---|
| `IRenderer` | [Rendering/IRenderer.cs](../../MainframeEngine/Src/Rendering/IRenderer.cs) | public |
| `IVulkanContext` | [Rendering/Vulkan/IVulkanContext.cs](../../MainframeEngine/Src/Rendering/Vulkan/IVulkanContext.cs) | public |
| `VulkanRenderer` | [Rendering/Vulkan/VulkanRenderer.cs](../../MainframeEngine/Src/Rendering/Vulkan/VulkanRenderer.cs) | internal, implements both |
| `VulkanLoaderBootstrap` | [Rendering/Vulkan/VulkanLoaderBootstrap.cs](../../MainframeEngine/Src/Rendering/Vulkan/VulkanLoaderBootstrap.cs) | internal — see [Build & platforms](build-and-platforms.md#macos-vulkan-loader-bootstrap) |
| `VulkanImGuiController` | [Rendering/Vulkan/VulkanImGuiController.cs](../../MainframeEngine/Src/Rendering/Vulkan/VulkanImGuiController.cs) | internal — see [ImGui & debug tools](imgui-and-debug-tools.md) |

### `IRenderer`

`Backend`, `VSync {get;set;}`, `OnResize(size)`, `BeginFrame()`, `EndFrame()`,
`SetClearColor(r,g,b,a=1)`, `Clear()`, `EnableDepthTest()`, `DisableDepthTest()`.
In Vulkan, `Clear` and the depth toggles are **no-ops**: clearing is the render pass `LoadOp`, and depth
state is baked into each pipeline.

### `IVulkanContext`

`Vk`, `Device`, `PhysicalDevice`, `RenderPass` (main), `CommandPool`, `GraphicsQueue`,
`FrameStarted`, `CurrentCommandBuffer` (`default` outside a frame), `SwapchainExtent`,
`SwapchainImageCount`, `CurrentImageIndex`, `CurrentFramebuffer`, `BeginRenderPass()`.

Always guard the cast: `if (Renderer is IVulkanContext vk) { ... }`.

## Initialization

```mermaid
flowchart LR
    A[CreateInstance] --> B[SetupDebugMessenger] --> C[CreateSurface] --> D[PickPhysicalDevice]
    D --> E[CreateLogicalDevice] --> F[CreateSwapchain] --> G[CreateImageViews]
    G --> H[CreateRenderPass] --> I[CreateDepthResources] --> J[CreateFramebuffers]
    J --> K[CreateCommandPool] --> L[CreateCommandBuffers] --> M[CreateSyncObjects]
```

| Step | Detail |
|---|---|
| Instance | API 1.2, GLFW-required extensions, `VK_EXT_debug_utils` if validating, `VK_KHR_portability_enumeration` + flag when available (macOS). Layer `VK_LAYER_KHRONOS_validation` (dropped with a warning if missing). |
| Debug messenger | Warning/Error × General/Performance/Validation → static `[UnmanagedCallersOnly]` callback → `VulkanValidationLog` (`IVulkanContext.Validation`: counts + first 64 messages) and `Log.Warning`/`Log.Error`. Verbose/info are not subscribed (they would allocate a string per message). |
| Physical device | First device with graphics + present queue families. No feature or extension scoring. |
| Logical device | One queue per unique family. Extensions: `VK_KHR_swapchain` (+ `VK_KHR_portability_subset` if advertised). **No features enabled.** |
| Swapchain | Prefers `B8G8R8A8Unorm` / `SrgbNonlinear`. VSync (initially `EngineOptions.VSync`) → FIFO; else Mailbox → Immediate → FIFO. `minImageCount + 1`. Usage `ColorAttachment`, plus `TransferSrc` when `EngineOptions.EnableFrameCapture` is set and supported (frame capture, see [Testing](testing.md#frame-capture)). |
| Depth | One image shared by all framebuffers: first of `D32Sfloat`, `D32SfloatS8Uint`, `D24UnormS8Uint`. |
| Command buffers | One primary per **swapchain image**, from a `ResetCommandBuffer` pool on the graphics family. |
| Sync | `MaxFramesInFlight = 2`: `imageAvailable[2]`, `inFlight[2]` (signalled). `renderFinished[imageCount]`, `imagesInFlight[imageCount]`. |

## Main render pass

![Main render pass](../images/main-render-pass.svg)

| # | Attachment | Load / Store | Initial → Final layout |
|---|---|---|---|
| 0 | color, swapchain format | Clear / Store | Undefined → PresentSrcKhr |
| 1 | depth | Clear / DontCare | Undefined → DepthStencilAttachmentOptimal |

One subpass. One external dependency: src `ColorAttachmentOutput | EarlyFragmentTests` (access 0) →
dst the same stages (`ColorAttachmentWrite | DepthStencilAttachmentWrite`). Clear values are the
`SetClearColor` color and depth 1.0.

## Frame synchronization

![Frame sync timeline](../images/frame-sync-timeline.svg)

**`BeginFrame()`**
1. If `_framebufferResized` → `RecreateSwapchain()`.
2. Wait `inFlight[currentFrame]`.
3. `AcquireNextImage(imageAvailable[currentFrame])`. On `ErrorOutOfDateKhr`: recreate and return
   with `FrameStarted = false`.
4. If `imagesInFlight[image]` is set, wait on it; then set it to `inFlight[currentFrame]`.
5. Reset and begin `commandBuffers[image]`. `FrameStarted = true`.

**`EndFrame()`**
1. `CmdEndRenderPass`, `EndCommandBuffer`.
2. Reset `inFlight[currentFrame]`. Submit, waiting `imageAvailable[currentFrame]` at `ColorAttachmentOutput`
   and signalling `renderFinished[image]`.
3. Present, waiting on `renderFinished[image]`. On OutOfDate, Suboptimal or resize: recreate.
4. `currentFrame = (currentFrame + 1) % 2`.

### Per-image vs per-frame resources

Subsystems key their uniform buffers and descriptor sets by `CurrentImageIndex`. This is safe because
`BeginFrame` waits on the fence that last used that image before recording. It does mean every
subsystem allocates `SwapchainImageCount` copies (usually 3), and those arrays are sized once at
construction.

## Swapchain recreation

Triggered by OutOfDate/Suboptimal, `OnResize`, or setting `VSync`.

1. While the framebuffer is 0×0 (minimized), pump `Window.DoEvents()`.
2. `DeviceWaitIdle`.
3. Destroy depth, framebuffers, command buffers, image views and the swapchain.
4. Recreate them and reallocate `imagesInFlight`.

**Not recreated:** the render pass (formats are assumed stable), `renderFinished[]` (sized from the
original image count), and every subsystem's per-image arrays. `OldSwapchain` is not passed.

## Disposal

`DeviceWaitIdle` → swapchain cleanup → semaphores and fences → command pool → render pass → device →
debug messenger → surface → instance → `Vk.Dispose()`. The game must dispose its own GPU objects
(shadow system, sky, grid, nodes) **before** `base.OnClose()` disposes the renderer.

## Known issues

- **Depth write-after-write hazard** *(inferred)*: the external dependency has `SrcAccessMask = 0`
  ([VulkanRenderer.cs:710](../../MainframeEngine/Src/Rendering/Vulkan/VulkanRenderer.cs)), and one depth
  image is shared by two frames in flight.
- **No device features enabled** ([VulkanRenderer.cs:499](../../MainframeEngine/Src/Rendering/Vulkan/VulkanRenderer.cs)),
  yet the lit shaders index sampler arrays with a loop variable, which requires
  `shaderSampledImageArrayDynamicIndexing` *(inferred)*.
- **Image-count changes are not handled:** `renderFinished[]` and every subsystem's per-image arrays
  are fixed at construction.
- Physical device selection does not prefer discrete GPUs or check swapchain support.
- Every upload in the engine is a one-time submit followed by `QueueWaitIdle`.
- Validation defaults to on in every configuration (`EngineOptions.EnableValidation = true`).
- `EndFrame` ends the render pass without checking that one was begun.

## Related docs

[Engine lifecycle](engine-lifecycle.md) · [Coordinate conventions](coordinate-conventions.md) ·
[Shadow system](shadow-system.md) · [Future: renderer stabilization](future/renderer-stabilization.md) ·
[Future: GPU resource management](future/gpu-resource-management.md)
