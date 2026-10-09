namespace MainframeEngine;

/// <summary>Creation settings for <see cref="VulkanRenderer"/>, filled from <see cref="EngineOptions"/>.</summary>
internal readonly record struct VulkanRendererOptions
{
    public bool EnableValidation { get; init; }
    public bool VSync { get; init; }

    /// <summary>Adds transfer-source usage to the swapchain so frames can be read back.</summary>
    public bool EnableFrameCapture { get; init; }

    /// <summary>The initial <see cref="IVulkanContext.AntiAliasing"/>.</summary>
    public AntiAliasing AntiAliasing { get; init; }
}
