namespace MainframeEngine;

/// <summary>
/// Thrown when a Vulkan call fails or the device lacks something the renderer requires.
/// </summary>
public sealed class VulkanException : Exception
{
    public VulkanException()
    {
    }

    public VulkanException(string message) : base(message)
    {
    }

    public VulkanException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
