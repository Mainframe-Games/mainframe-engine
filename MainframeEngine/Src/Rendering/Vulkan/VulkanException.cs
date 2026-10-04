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

internal static class VulkanResultExtensions
{
    /// <summary>Throws <see cref="VulkanException"/> unless <paramref name="result"/> is <c>VK_SUCCESS</c>.</summary>
    /// <param name="result">The call's result.</param>
    /// <param name="what">What was attempted, e.g. "vkCreateBuffer (shadow ring)"; pass a literal so success costs nothing.</param>
    public static void Check(this Silk.NET.Vulkan.Result result, string what)
    {
        if (result != Silk.NET.Vulkan.Result.Success)
            throw new VulkanException($"[Vulkan] {what} failed: {result}");
    }
}
