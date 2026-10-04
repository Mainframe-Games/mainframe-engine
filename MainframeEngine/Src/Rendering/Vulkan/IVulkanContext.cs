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

    Vk Vk { get; }
    Device Device { get; }
    PhysicalDevice PhysicalDevice { get; }
    RenderPass RenderPass { get; }
    CommandPool CommandPool { get; }
    Queue GraphicsQueue { get; }

    /// <summary>True while a frame is being recorded (between BeginFrame and EndFrame). False during swapchain recreation.</summary>
    bool FrameStarted { get; }

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

    Framebuffer CurrentFramebuffer { get; }

    /// <summary>Begins the main render pass. Called by Engine after shadow passes complete.</summary>
    void BeginRenderPass();

    /// <summary>Validation-layer warnings and errors reported since startup (or the last reset).</summary>
    VulkanValidationLog Validation { get; }
}
