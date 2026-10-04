using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;

namespace MainframeEngine;

/// <summary>
/// Framebuffer size in pixels. On HiDPI displays SDL's window size is in points (a Retina window of
/// 1512×846 pt is 3024×1692 px), and Silk's <see cref="IWindow.FramebufferSize"/> reports the GL
/// drawable, which for a Vulkan window is the point size. The swapchain, viewports, scissors and
/// ImGui's framebuffer scale need pixels, so this asks SDL for the Vulkan drawable size.
/// </summary>
internal static unsafe class WindowPixels
{
    /// <summary>The Vulkan drawable size in pixels (0×0 while minimised on some platforms).</summary>
    public static Vector2D<int> FramebufferSize(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!SdlWindowing.IsViewSdl(window))
            return window.FramebufferSize;

        var handle = SdlWindowing.GetHandle(window);
        var sdl = SdlWindowing.GetExistingApi(window);
        if (handle is null || sdl is null)
            return window.FramebufferSize;

        int width, height;
        sdl.VulkanGetDrawableSize(handle, &width, &height);
        return new Vector2D<int>(width, height);
    }

    /// <summary>Pixels per point (2 on Retina); 1 when the window has no area.</summary>
    public static float Scale(IWindow window, Vector2D<int> framebufferSize)
    {
        ArgumentNullException.ThrowIfNull(window);
        var points = window.Size;
        return points.X > 0 && framebufferSize.X > 0 ? (float)framebufferSize.X / points.X : 1f;
    }
}
