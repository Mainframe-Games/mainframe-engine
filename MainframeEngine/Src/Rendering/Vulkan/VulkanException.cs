namespace MainframeEngine;

/// <summary>
/// Thrown when a Vulkan call fails or the device lacks something the renderer requires.
/// </summary>
public class VulkanException : Exception
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

/// <summary>
/// The GPU stopped responding (<c>VK_ERROR_DEVICE_LOST</c>): a driver reset, a hang or a fault on the GPU. Crash reports
/// give it the kind <see cref="CrashKind.DeviceLost"/> with what the GPU was doing (<see cref="Loss"/>: the passes in
/// flight from the renderer's breadcrumbs, and <c>VK_EXT_device_fault</c>'s report where the driver has one). The game
/// cannot recover the device: it reports the loss and exits; the renderer's <c>Dispose</c> then skips its waits.
/// </summary>
public sealed class VulkanDeviceLostException : VulkanException
{
    private static volatile bool s_occurred;

    public VulkanDeviceLostException() : this("[Vulkan] The device was lost.")
    {
    }

    public VulkanDeviceLostException(string message) : base(message)
    {
        s_occurred = true;
        // Read here, on the throwing thread, while the device's breadcrumb buffer is still mapped.
        try
        {
            Loss = CrashContext.DeviceLoss?.Invoke();
        }
        catch (Exception)
        {
            Loss = null;
        }
    }

    public VulkanDeviceLostException(string message, Exception innerException) : base(message, innerException)
    {
        s_occurred = true;
    }

    /// <summary>What the GPU was doing, when the renderer could tell (null without breadcrumbs or fault info).</summary>
    public CrashGpuLoss? Loss { get; }

    /// <summary>Some device in this process was lost (the renderer then disposes without waiting on the GPU).</summary>
    internal static bool Occurred => s_occurred;

    internal static void ResetForTests() => s_occurred = false;
}

internal static class VulkanResultExtensions
{
    /// <summary>
    /// Throws <see cref="VulkanException"/> unless <paramref name="result"/> is <c>VK_SUCCESS</c>
    /// (<see cref="VulkanDeviceLostException"/> for <c>VK_ERROR_DEVICE_LOST</c>).
    /// </summary>
    /// <param name="result">The call's result.</param>
    /// <param name="what">What was attempted, e.g. "vkCreateBuffer (shadow ring)"; pass a literal so success costs nothing.</param>
    public static void Check(this Silk.NET.Vulkan.Result result, string what)
    {
        if (result != Silk.NET.Vulkan.Result.Success)
            Throw(result, what);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Throw(Silk.NET.Vulkan.Result result, string what)
    {
        if (result == Silk.NET.Vulkan.Result.ErrorDeviceLost)
            throw new VulkanDeviceLostException($"[Vulkan] {what} failed: the device was lost (VK_ERROR_DEVICE_LOST).");
        throw new VulkanException($"[Vulkan] {what} failed: {result}");
    }
}
