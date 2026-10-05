using System.Numerics;

namespace MainframeEngine;

/// <summary>How the root viewport's content follows the window size (Godot's <c>Window.ContentScaleMode</c>).</summary>
public enum ContentScaleMode : byte
{
    /// <summary>One canvas unit per window pixel.</summary>
    Disabled,

    /// <summary>The 2D canvas is drawn at the window's resolution, scaled from <see cref="SceneViewport.ContentScaleSize"/> (Godot's <c>canvas_items</c>).</summary>
    CanvasItems,

    /// <summary>Godot's <c>viewport</c> mode (rendered at the base size, then scaled); treated like canvas items for now.</summary>
    Viewport,
}

/// <summary>How the base aspect ratio is kept (Godot's <c>Window.ContentScaleAspect</c>).</summary>
public enum ContentScaleAspect : byte
{
    Ignore,
    Keep,
    KeepWidth,
    KeepHeight,
    Expand,
}

/// <summary>Fractional or whole-number scaling (Godot's <c>Window.ContentScaleStretch</c>).</summary>
public enum ContentScaleStretch : byte
{
    Fractional,
#pragma warning disable CA1720 // Godot's name for whole-number scaling
    Integer,
#pragma warning restore CA1720
}

/// <summary>The result of <see cref="ContentScale.Compute"/>.</summary>
/// <param name="RenderSize">The drawn area in window pixels (Godot's <c>final_size</c>).</param>
/// <param name="VisibleSize">The visible area in canvas units (Godot's <c>size_2d_override</c>: the visible rect).</param>
/// <param name="Margin">Letterbox offset of the drawn area inside the window, in pixels.</param>
public readonly record struct ContentScaleResult(Vector2 RenderSize, Vector2 VisibleSize, Vector2 Margin)
{
    /// <summary>Canvas units → window pixels: scale by RenderSize / VisibleSize, then offset by the margin.</summary>
    public Transform2D StretchTransform =>
        Transform2D.FromTrs(Margin, 0f, new Vector2(
            VisibleSize.X > 0 ? RenderSize.X / VisibleSize.X : 1f,
            VisibleSize.Y > 0 ? RenderSize.Y / VisibleSize.Y : 1f));
}

/// <summary>Godot 4.7's <c>Window::_update_viewport_size</c> for the root viewport, as a pure function.</summary>
public static class ContentScale
{
    public static ContentScaleResult Compute(Vector2 windowPixels, ContentScaleMode mode, ContentScaleAspect aspect, Vector2 baseSize,
        float factor = 1f, ContentScaleStretch stretch = ContentScaleStretch.Fractional)
    {
        if (stretch == ContentScaleStretch.Integer)
        {
            factor = MathF.Floor(factor);
            if (factor < 1)
                factor = 1;
        }

        if (mode == ContentScaleMode.Disabled || baseSize.X == 0 || baseSize.Y == 0)
            return new ContentScaleResult(windowPixels, windowPixels / factor, Vector2.Zero);

        var videoMode = windowPixels;
        var desired = baseSize;
        Vector2 viewportSize, screenSize;
        var viewportAspect = desired.X / desired.Y;
        var videoAspect = videoMode.X / videoMode.Y;

        if (aspect == ContentScaleAspect.Ignore || IsEqualApprox(viewportAspect, videoAspect))
        {
            viewportSize = desired;
            screenSize = videoMode;
        }
        else if (viewportAspect < videoAspect)
        {
            if (aspect is ContentScaleAspect.KeepHeight or ContentScaleAspect.Expand)
            {
                viewportSize = new Vector2(desired.Y * videoAspect, desired.Y);
                screenSize = videoMode;
            }
            else
            {
                viewportSize = desired;
                screenSize = new Vector2(videoMode.Y * viewportAspect, videoMode.Y);
            }
        }
        else
        {
            if (aspect is ContentScaleAspect.KeepWidth or ContentScaleAspect.Expand)
            {
                viewportSize = new Vector2(desired.X, desired.X / videoAspect);
                screenSize = videoMode;
            }
            else
            {
                viewportSize = desired;
                screenSize = new Vector2(videoMode.X, videoMode.X / viewportAspect);
            }
        }

        screenSize = FloorV(screenSize);
        viewportSize = FloorV(viewportSize);

        if (stretch == ContentScaleStretch.Integer)
        {
            var scale = FloorV(screenSize / viewportSize);
            var s = Math.Max(1f, MathF.Min(scale.X, scale.Y));
            screenSize = viewportSize * s;
        }

        var margin = Vector2.Zero;
        if (screenSize.X < videoMode.X)
            margin.X = MathF.Round((videoMode.X - screenSize.X) / 2f);
        if (screenSize.Y < videoMode.Y)
            margin.Y = MathF.Round((videoMode.Y - screenSize.Y) / 2f);

        return new ContentScaleResult(screenSize, viewportSize / factor, margin);
    }

    private static Vector2 FloorV(Vector2 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y));

    private static bool IsEqualApprox(float a, float b)
    {
        if (a == b)
            return true;
        var tolerance = 0.00001f * MathF.Abs(a);
        if (tolerance < 0.00001f)
            tolerance = 0.00001f;
        return MathF.Abs(a - b) < tolerance;
    }
}
