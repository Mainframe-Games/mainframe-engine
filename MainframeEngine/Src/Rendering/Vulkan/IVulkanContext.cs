using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Exposes Vulkan state to shapes and other renderable objects.
/// Obtain via cast: renderer as IVulkanContext
/// </summary>
/// <remarks>
/// Per-frame resources (UBOs, dynamic vertex buffers, descriptor sets that point at them) must be keyed
/// by <see cref="FrameSlot"/> and sized <see cref="MaxFramesInFlight"/>, never by swapchain image: the
/// image count is chosen by the driver and can change on any swapchain recreation, while a frame slot's
/// resources are guaranteed idle once <see cref="FrameStarted"/> (its fence has been waited on).
/// </remarks>
public interface IVulkanContext
{
    /// <summary>Frames the CPU may record ahead of the GPU; per-frame resources come in this many copies.</summary>
    const int MaxFramesInFlight = 2;

    /// <summary>
    /// Exposure the renderer starts with. Calibrated (ADR "ACES fitted tonemap") so a white surface lit at about
    /// 0.75 shows as bright as it did before the HDR pipeline; ACES' toe and shoulder then do the rest.
    /// </summary>
    const float DefaultExposure = 1.3f;

    /// <summary>The sharpen after TAA the renderer starts with (<see cref="TaaSharpness"/>, ADR 0166).</summary>
    const float DefaultTaaSharpness = 0.25f;

    Vk Vk { get; }
    Device Device { get; }
    PhysicalDevice PhysicalDevice { get; }
    /// <summary>
    /// The HDR scene pass (<see cref="SceneTarget"/>: <c>R16G16B16A16_SFLOAT</c> colour + depth). Scene pipelines
    /// (sky, grid, shapes, Spine, meshes) are built against it and write linear colour.
    /// </summary>
    RenderPass RenderPass { get; }
    CommandPool CommandPool { get; }
    Queue GraphicsQueue { get; }

    /// <summary>True while a frame is being recorded (between BeginFrame and EndFrame). False during swapchain recreation.</summary>
    bool FrameStarted { get; }

    /// <summary>
    /// Milliseconds the CPU spent blocked on the GPU or the swapchain during the last BeginFrame/EndFrame: the frame
    /// slot's fence wait, acquiring the swapchain image, presenting and a frame capture's read-back.
    /// <see cref="Engine.LastFrameCpuMilliseconds"/> is the frame's time without it.
    /// </summary>
    double LastFrameWaitMilliseconds { get; }

    /// <summary>
    /// Index (0 .. <see cref="MaxFramesInFlight"/> - 1) of the frame being recorded. Key per-frame resources
    /// by this; they are not in use by the GPU while <see cref="FrameStarted"/> is true.
    /// </summary>
    int FrameSlot { get; }

    /// <summary>The command buffer currently being recorded. Valid only when FrameStarted is true.</summary>
    CommandBuffer CurrentCommandBuffer { get; }

    /// <summary>Swapchain size in pixels.</summary>
    Extent2D SwapchainExtent { get; }

    /// <summary>Number of swapchain images; driver-chosen and may change on recreation. Do not size per-frame resources by it.</summary>
    uint SwapchainImageCount { get; }

    /// <summary>The acquired swapchain image. Do not key per-frame resources by it; use <see cref="FrameSlot"/>.</summary>
    uint CurrentImageIndex { get; }

    /// <summary>The scene target's framebuffer (the scene pass renders offscreen; see <see cref="SceneTarget"/>).</summary>
    Framebuffer CurrentFramebuffer { get; }

    /// <summary>Begins the main (HDR scene) render pass. Called by Engine after shadow passes complete.</summary>
    void BeginRenderPass();

    /// <summary>
    /// The offscreen HDR scene target (colour + depth) of the main view, at <see cref="RenderExtent"/>: the swapchain's
    /// size, or less with a <see cref="Scaling3DScale"/> below 1 (ADR 0174).
    /// </summary>
    RenderTarget SceneTarget { get; }

    /// <summary>
    /// The main view's render size in pixels (ADR 0174): the scene pass, the depth prepass, SSAO, contact shadows,
    /// volumetric fog, the water scene copy and depth of field run at it. <see cref="SwapchainExtent"/> × the applied
    /// <see cref="Scaling3DScale"/> (the swapchain's size at 1); the upscale, glow, exposure, tonemap and the overlay run
    /// at the swapchain's.
    /// </summary>
    Extent2D RenderExtent { get; }

    /// <summary>
    /// The pass UI is drawn in after tonemapping (canvas, UI, dev overlay): it targets the swapchain image in the encoding the UI was
    /// authored for. Build overlay pipelines against it.
    /// </summary>
    RenderPass OverlayRenderPass { get; }

    /// <summary>
    /// True when the overlay target encodes linear → sRGB on write, so sRGB-authored overlay colours must be
    /// linearised in the shader; false when they are written as-is (the usual case).
    /// </summary>
    bool OverlayEncodesSrgb { get; }

    /// <summary>
    /// Ends the scene pass, tonemaps it into the swapchain and begins the overlay pass. Idempotent; EndFrame does it
    /// when nobody else did. Registered <see cref="IOverlayRenderer"/>s record their offscreen work between the scene
    /// pass and the tonemap, and draw first in the overlay pass.
    /// </summary>
    void BeginOverlayPass();

    /// <summary>
    /// Adds a renderer drawn after tonemapping. Renderers draw in ascending <paramref name="order"/> (see
    /// <see cref="OverlayOrder"/>); equal orders draw in registration order. Renderers at or below <see cref="OverlayOrder.Ui"/>
    /// draw below the UI (which includes the dev overlay); higher orders draw above it. Adding a renderer twice is a no-op.
    /// </summary>
    void AddOverlayRenderer(IOverlayRenderer renderer, int order = OverlayOrder.Ui);

    /// <summary>Removes a renderer added with <see cref="AddOverlayRenderer"/>.</summary>
    bool RemoveOverlayRenderer(IOverlayRenderer renderer);

    /// <summary>A format with a stencil aspect usable as a depth/stencil attachment (UI clip masks).</summary>
    Format StencilFormat { get; }

    /// <summary>Validation-layer warnings and errors reported since startup (or the last reset).</summary>
    VulkanValidationLog Validation { get; }

    /// <summary>Device-memory sub-allocator for every buffer and image (see <see cref="GpuBuffer"/>, <see cref="GpuImage"/>).</summary>
    GpuAllocator Allocator { get; }

    /// <summary>Staging uploads, recorded at the start of the next frame's command buffer.</summary>
    UploadQueue Uploads { get; }

    /// <summary>Destroys GPU objects once the frames that may use them have finished (use instead of vkDeviceWaitIdle).</summary>
    DeletionQueue Deletions { get; }

    /// <summary>The persisted <c>VkPipelineCache</c>; create every pipeline through it.</summary>
    PipelineCache Pipelines { get; }

    /// <summary>One shader module per <c>.spv</c> path, shared by every pipeline.</summary>
    ShaderModuleCache Shaders { get; }

    /// <summary>The per-frame shared descriptor set 0 (camera + lights); see <see cref="FrameContext"/>.</summary>
    FrameContext Frame { get; }

    /// <summary>Frames that have started recording since startup (1-based; 0 before the first frame).</summary>
    ulong FrameNumber { get; }

    /// <summary>
    /// Exposure multiplier applied to the HDR scene before tonemapping (1 = neutral). See
    /// docs/design/color-pipeline.md.
    /// </summary>
    float Exposure { get; set; }

    /// <summary>
    /// The tonemap and glow of the frame (ADR 0124), set each frame by the render server from the tree's root world's
    /// <see cref="WorldEnvironment"/>. <see cref="PostProcessSettings.Default"/> keeps the engine's own tonemap pass.
    /// </summary>
    PostProcessSettings PostProcess { get; set; }

    /// <summary>
    /// The sun's screen position for light shafts (ADR 0160), set each frame by the render server from the tree's root
    /// camera and world while <see cref="PostProcessSettings.LightShaftsEnabled"/>; default (fade 0) draws none.
    /// </summary>
    LightShaftsSun LightShaftsSun { get; set; }

    /// <summary>
    /// Anti-aliasing of the main view (ADR 0154, ADR 0166): FXAA runs on the tonemapped image, TAA on the HDR image before
    /// auto exposure and glow; both before the 2D canvas, gizmos and UI. Starts at <see cref="EngineOptions.AntiAliasing"/>;
    /// may change between frames (switching to TAA starts a new history).
    /// </summary>
    AntiAliasing AntiAliasing { get; set; }

    /// <summary>
    /// How much the image is sharpened after TAA (ADR 0166), 0 (off) to 1: a contrast-adaptive sharpen (RCAS, from AMD
    /// FSR 1) on the tonemapped image, under the UI, that restores the detail the resolve's filtering softens. Only with
    /// <see cref="AntiAliasing.Taa"/>. Starts at <see cref="EngineOptions.TaaSharpness"/> (<see cref="DefaultTaaSharpness"/>).
    /// </summary>
    float TaaSharpness { get; set; }

    /// <summary>
    /// How the main view upscales from <see cref="RenderExtent"/> to the swapchain (<c>rendering.scaling3DMode</c>,
    /// ADR 0174): bilinear, FSR 1 or TAAU. Only below a <see cref="Scaling3DScale"/> of 1; may change between frames.
    /// </summary>
    Scaling3DMode Scaling3DMode { get; set; }

    /// <summary>
    /// The main view's render scale, <see cref="RenderScaling.MinScale"/> to 1 (<c>rendering.scaling3DScale</c>, ADR 0174;
    /// default 1, native). Sub-viewports and the editor's views always render at 1, the 2D canvas and UI at the window's
    /// size. A change applies at the start of the next frame (the render targets are resized, histories restart).
    /// </summary>
    float Scaling3DScale { get; set; }

    /// <summary>
    /// RCAS's attenuation in stops after an FSR 1 upscale (<c>rendering.fsrSharpness</c>, Godot's; 0 is the sharpest, 2 the
    /// softest; default <see cref="RenderScaling.DefaultFsrSharpness"/>).
    /// </summary>
    float FsrSharpness { get; set; }

    /// <summary>
    /// Seconds since the previous frame (<see cref="Engine"/> sets it from <see cref="GameTime.DeltaTime"/> before each
    /// frame): how far auto exposure adapts (ADR 0154).
    /// </summary>
    float FrameDeltaTime { get; set; }

    /// <summary>Largest anisotropic filtering level samplers may use (1 when the device lacks samplerAnisotropy).</summary>
    float MaxSamplerAnisotropy { get; }
}
