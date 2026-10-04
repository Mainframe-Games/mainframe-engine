using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Exposes Vulkan state to shapes and other renderable objects.
/// Obtain via cast: renderer as IVulkanContext
/// </summary>
public interface IVulkanContext
{
    Vk Vk { get; }
    Device Device { get; }
    PhysicalDevice PhysicalDevice { get; }
    RenderPass RenderPass { get; }
    CommandPool CommandPool { get; }
    Queue GraphicsQueue { get; }

    /// <summary>True while a frame is being recorded (between BeginFrame and EndFrame). False during swapchain recreation.</summary>
    bool FrameStarted { get; }

    /// <summary>The command buffer currently being recorded. Valid only when FrameStarted is true.</summary>
    CommandBuffer CurrentCommandBuffer { get; }

    Extent2D SwapchainExtent { get; }
    uint SwapchainImageCount { get; }
    uint CurrentImageIndex { get; }
    Framebuffer CurrentFramebuffer { get; }

    /// <summary>Begins the main render pass. Called by Engine after shadow passes complete.</summary>
    void BeginRenderPass();

    /// <summary>Validation-layer warnings and errors reported since startup (or the last reset).</summary>
    VulkanValidationLog Validation { get; }
}
