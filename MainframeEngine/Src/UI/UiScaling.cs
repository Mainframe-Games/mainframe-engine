using System.Numerics;

namespace MainframeEngine;

/// <summary>How the game's UI follows the window (project.mfproj <c>ui.scaleMode</c>; Unity's <c>CanvasScaler.ScaleMode</c>).</summary>
public enum UiScalingMode
{
    /// <summary>1 dp = the content scale (the display's pixels per point, 2 on Retina): the UI keeps its size whatever the window.</summary>
    ConstantPixelSize,

    /// <summary>
    /// 1 dp = framebuffer size ÷ <see cref="UiScaling.ReferenceResolution"/> (blended by
    /// <see cref="UiScaling.MatchWidthOrHeight"/>): a UI authored for the reference resolution grows and shrinks with the window.
    /// </summary>
    ScaleWithScreenSize,
}

/// <summary>
/// The UI scale a game runs with (ADR 0181, docs/design/game-ui.md#scaling): the dp ratio of every
/// <see cref="UiScaleMode.Project"/> <see cref="UiLayer"/> (the default), the developer overlay and the RmlUi debugger.
/// <see cref="UiScalingMode.ScaleWithScreenSize"/> is Unity's "Scale With Screen Size": at the reference resolution
/// 1 dp is one pixel, at twice its size two. It is computed from framebuffer <b>pixels</b>, so a Retina window of
/// 1920×1080 points (3840×2160 pixels) gets a 2× UI, the same physical size as a 1080p display at 1×.
/// </summary>
/// <remarks>Set from project.mfproj's <c>ui</c> section (<see cref="UiProjectSettings"/>) through <see cref="UiServerOptions.Scaling"/>; change it at run time with <see cref="UiServer.Scaling"/>.</remarks>
public sealed record UiScaling
{
    /// <summary>The 1080p reference resolution new projects use.</summary>
    public static readonly Vector2 DefaultReferenceResolution = new(1920, 1080);

    /// <summary>The engine's default (tools, tests, the editor): dp = content scale.</summary>
    public static UiScaling ConstantPixelSize { get; } = new();

    /// <summary>Scale with the screen at 1920×1080, matching height: what new game projects use.</summary>
    public static UiScaling ScaleWithScreenSize { get; } = new() { Mode = UiScalingMode.ScaleWithScreenSize };

    public UiScalingMode Mode { get; init; }

    /// <summary>The resolution (pixels) the UI is authored for: 1 dp = 1 pixel there. Both axes must be positive.</summary>
    public Vector2 ReferenceResolution
    {
        get;
        init
        {
            if (!(value.X >= 1 && value.Y >= 1) || !float.IsFinite(value.X) || !float.IsFinite(value.Y))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The reference resolution must be at least 1×1.");
            field = value;
        }
    } = DefaultReferenceResolution;

    /// <summary>
    /// Which axis drives the scale: 0 = width, 1 = height (default), in between a blend in log space (0.5: the geometric
    /// mean of the two ratios, Unity's formula). Height keeps the layout exactly <see cref="ReferenceResolution"/>.Y dp tall
    /// on every landscape aspect, so vertical menus always fit and wider screens get more horizontal room.
    /// </summary>
    public float MatchWidthOrHeight
    {
        get;
        init
        {
            if (!(value >= 0f && value <= 1f))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Match width or height must be between 0 and 1.");
            field = value;
        }
    } = 1f;

    /// <summary>Smallest dp ratio <see cref="UiScalingMode.ScaleWithScreenSize"/> may give (keeps text readable in small windows); 0 = no limit.</summary>
    public float MinScale
    {
        get;
        init
        {
            if (!(value >= 0f) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The minimum scale must be 0 (none) or positive.");
            field = value;
        }
    }

    /// <summary>Largest dp ratio <see cref="UiScalingMode.ScaleWithScreenSize"/> may give; 0 = no limit.</summary>
    public float MaxScale
    {
        get;
        init
        {
            if (!(value >= 0f) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The maximum scale must be 0 (none) or positive.");
            field = value;
        }
    }

    /// <summary>
    /// The dp ratio (pixels per dp) for a UI laid out at <paramref name="pixels"/> (the framebuffer, or a layer's region)
    /// with <paramref name="contentScale"/> pixels per point. Pure arithmetic: no allocation.
    /// </summary>
    public float ComputeDpRatio(Vector2 pixels, float contentScale)
    {
        if (Mode == UiScalingMode.ConstantPixelSize)
            return MathF.Max(0.01f, contentScale);
        var scale = ScaleWithScreen(pixels, ReferenceResolution, MatchWidthOrHeight);
        if (MinScale > 0f)
            scale = MathF.Max(scale, MinScale);
        if (MaxScale > 0f)
            scale = MathF.Min(scale, MaxScale);
        return MathF.Max(0.01f, scale);
    }

    /// <summary>
    /// Unity's <c>CanvasScaler</c> "match width or height": 2^lerp(log2(w/refW), log2(h/refH), match). 1920×1080 → 1,
    /// 2560×1440 → 1.333, 3840×2160 → 2, 1280×720 → 0.667 against a 1080p reference.
    /// </summary>
    public static float ScaleWithScreen(Vector2 pixels, Vector2 reference, float matchWidthOrHeight)
    {
        if (!(reference.X > 0 && reference.Y > 0 && pixels.X > 0 && pixels.Y > 0))
            return 1f;
        // The ends exactly (no log/pow rounding): one axis's ratio.
        if (matchWidthOrHeight >= 1f)
            return pixels.Y / reference.Y;
        if (matchWidthOrHeight <= 0f)
            return pixels.X / reference.X;
        var logWidth = MathF.Log2(pixels.X / reference.X);
        var logHeight = MathF.Log2(pixels.Y / reference.Y);
        return MathF.Pow(2f, logWidth + ((logHeight - logWidth) * matchWidthOrHeight));
    }
}
