using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;

namespace MainframeEngine;

/// <summary>
/// Framebuffer size in pixels. On HiDPI displays SDL's window size is in points (a Retina window of
/// 1512×846 pt is 3024×1692 px), and Silk's <see cref="IWindow.FramebufferSize"/> reports the GL
/// drawable, which for a Vulkan window is the point size. The swapchain, viewports and scissors
/// need pixels, so this asks SDL for the Vulkan drawable size.
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

    /// <summary>Pixels per OS window point (2 on Retina); 1 when the window has no area.</summary>
    public static float Scale(IWindow window, Vector2D<int> framebufferSize)
    {
        ArgumentNullException.ThrowIfNull(window);
        var points = window.Size;
        return points.X > 0 && framebufferSize.X > 0 ? (float)framebufferSize.X / points.X : 1f;
    }

    /// <summary>
    /// The content scale (pixels per layout point: the UI's dp ratio): <paramref name="fixedScale"/>
    /// when it is set (<see cref="EngineOptions.ContentScale"/>), otherwise the display's <see cref="Scale"/>.
    /// </summary>
    public static float ContentScale(IWindow window, Vector2D<int> framebufferSize, float fixedScale) =>
        fixedScale > 0f ? fixedScale : Scale(window, framebufferSize);

    /// <summary>
    /// The framebuffer, in pixels, of a window laid out at <paramref name="layoutSize"/> points with
    /// <paramref name="contentScale"/> pixels per point (rounded to whole pixels).
    /// </summary>
    public static Vector2D<int> PixelsFor(Vector2D<int> layoutSize, float contentScale) =>
        new((int)MathF.Round(layoutSize.X * contentScale), (int)MathF.Round(layoutSize.Y * contentScale));

    /// <summary>
    /// The OS window size (points) that gives a <paramref name="pixels"/> framebuffer on a display with
    /// <paramref name="displayScale"/> (pixels per OS point, per axis), rounded to whole points; at least 1×1.
    /// </summary>
    public static Vector2D<int> WindowPointsFor(Vector2D<int> pixels, Vector2D<float> displayScale) => new(
        Math.Max(1, (int)MathF.Round(pixels.X / (displayScale.X > 0f ? displayScale.X : 1f))),
        Math.Max(1, (int)MathF.Round(pixels.Y / (displayScale.Y > 0f ? displayScale.Y : 1f))));

    /// <summary>
    /// Resizes the OS window until its framebuffer is exactly <paramref name="pixels"/>, whatever the backing scale of the
    /// display it is on (1× external monitor, 2× Retina): the window's point size is the request divided by the measured
    /// pixels-per-point ratio. Returns the framebuffer size reached, which differs from the request only when the display
    /// scale does not divide it (fractional scales) or the window manager refuses the size.
    /// </summary>
    public static Vector2D<int> ResizeToPixels(IWindow window, Vector2D<int> pixels)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (pixels.X <= 0 || pixels.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixels), pixels, "The framebuffer size must be positive.");

        var framebuffer = FramebufferSize(window);
        // A few rounds: the first measures the display scale at the current size, later ones absorb rounding.
        for (var attempt = 0; attempt < 4 && framebuffer != pixels; attempt++)
        {
            var points = window.Size;
            var scale = points.X > 0 && points.Y > 0 && framebuffer.X > 0 && framebuffer.Y > 0
                ? new Vector2D<float>((float)framebuffer.X / points.X, (float)framebuffer.Y / points.Y)
                : new Vector2D<float>(1f, 1f);
            var target = WindowPointsFor(pixels, scale);
            if (target == points)
                break; // the display scale does not divide the request: no point size reaches it
            window.Size = target;
            framebuffer = FramebufferSize(window);
        }

        return framebuffer;
    }
}
