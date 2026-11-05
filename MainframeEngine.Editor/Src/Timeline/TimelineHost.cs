using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// The shared timeline component (G1.5, docs/design/editor.md#timelines): how a timeline canvas is hosted. A canvas
/// draws into its own <see cref="SubViewport"/>, sized to a rect of the UI in pixels; the UI shows it as an
/// <c>engine://</c> image. The song tab, the video timeline and the Animation panel all use it.
/// </summary>
public static class TimelineHost
{
    /// <summary>
    /// Sizes <paramref name="viewport"/> to <paramref name="rect"/> (dp) in pixels and redraws <paramref name="canvas"/> in
    /// it; a hidden view stops rendering.
    /// </summary>
    public static void Place(SubViewport viewport, EditorCanvas canvas, LayoutRect rect, float scale, bool show) =>
        Place(viewport, canvas, new Vector2(rect.Width, rect.Height), scale, show);

    /// <summary>As <see cref="Place(SubViewport, EditorCanvas, LayoutRect, float, bool)"/>, for a size in dp.</summary>
    public static void Place(SubViewport viewport, EditorCanvas canvas, Vector2 size, float scale, bool show)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(canvas);
        var mode = show ? SubViewportUpdateMode.Always : SubViewportUpdateMode.Disabled;
        if (viewport.UpdateMode != mode)
            viewport.UpdateMode = mode;
        if (!show)
            return;
        var w = Math.Max(1, (int)MathF.Round(size.X * scale));
        var h = Math.Max(1, (int)MathF.Round(size.Y * scale));
        if (viewport.Width != w || viewport.Height != h)
        {
            viewport.Width = w;
            viewport.Height = h;
        }

        canvas.PixelScale = scale;
        canvas.ViewSize = size;
        canvas.QueueRedraw();
    }
}

/// <summary>One <c>engine://</c> texture a timeline publishes to the UI (registered when its viewport changes).</summary>
public sealed class TimelineTexture(string name)
{
    public string Name { get; } = name;

    public SubViewport? Registered { get; private set; }

    /// <summary>Shows <paramref name="viewport"/> under <see cref="Name"/> (null: unregisters).</summary>
    public void Publish(UiServer? ui, SubViewport? viewport)
    {
        if (ui is null || ReferenceEquals(Registered, viewport))
            return;
        if (viewport is null)
            ui.UnregisterTexture(Name);
        else
            ui.RegisterTexture(Name, viewport);
        Registered = viewport;
    }
}

/// <summary>
/// A timeline's horizontal view (G1.5): the time at its left edge, pixels per unit (seconds for animations, ticks for
/// songs), and zooming around a point. Pure math.
/// </summary>
public sealed class TimelineZoom
{
    public double Scroll { get; set; }

    public double PixelsPerUnit { get; set; } = 100;

    public double MinPixelsPerUnit { get; set; } = 1e-4;

    public double MaxPixelsPerUnit { get; set; } = 1e6;

    /// <summary>dp before time <see cref="Scroll"/> at the left edge, so a key at the start is drawn whole.</summary>
    public float Inset { get; set; }

    /// <summary>The x (dp, from the timeline's left) of <paramref name="time"/>.</summary>
    public float X(double time) => Inset + (float)((time - Scroll) * PixelsPerUnit);

    /// <summary>The time at <paramref name="x"/> (dp).</summary>
    public double TimeAt(float x) => Scroll + (x - Inset) / PixelsPerUnit;

    /// <summary>Zooms by <paramref name="factor"/> keeping the time under <paramref name="x"/> where it is.</summary>
    public void ZoomAround(float x, double factor)
    {
        var at = TimeAt(x);
        PixelsPerUnit = Math.Clamp(PixelsPerUnit * factor, MinPixelsPerUnit, MaxPixelsPerUnit);
        Scroll = Math.Max(0, at - (x - Inset) / PixelsPerUnit);
    }

    /// <summary>Scrolls by <paramref name="pixels"/> (dp), never before 0.</summary>
    public void ScrollBy(float pixels) => Scroll = Math.Max(0, Scroll + pixels / PixelsPerUnit);
}

/// <summary>Snapping to a step (G1.5): seconds or frames for animations.</summary>
public static class TimelineSnap
{
    public static double Round(double value, double step) => step > 0 ? Math.Round(value / step) * step : value;

    public static double Floor(double value, double step) => step > 0 ? Math.Floor(value / step + 1e-9) * step : value;

    public static double Ceiling(double value, double step) => step > 0 ? Math.Ceiling(value / step - 1e-9) * step : value;
}

/// <summary>
/// A seconds ruler's steps (G1.5): the largest of 0.01/0.05/0.1/0.25/0.5/1/2/5/10/30/60 s whose labels stay at least
/// <c>minPixels</c> apart, and labels formatted once and cached (drawing allocates nothing).
/// </summary>
public static class TimelineRuler
{
    private static readonly double[] Steps = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 120, 300];
    private static readonly Dictionary<long, string> Labels = [];

    /// <summary>The labelled step for <paramref name="pixelsPerSecond"/>, and how many ticks divide it.</summary>
    public static (double Step, int Subdivisions) StepFor(double pixelsPerSecond, float minPixels = 60f)
    {
        foreach (var step in Steps)
            if (step * pixelsPerSecond >= minPixels)
                return (step, step >= 1 ? 4 : 5);
        return (Steps[^1], 5);
    }

    /// <summary>"1.5", "12", "0.25" (seconds), or a frame number when <paramref name="framesPerSecond"/> &gt; 0.</summary>
    public static string Label(double seconds, double framesPerSecond = 0)
    {
        var key = framesPerSecond > 0 ? -1 - (long)Math.Round(seconds * framesPerSecond) : (long)Math.Round(seconds * 1000);
        if (Labels.TryGetValue(key, out var label))
            return label;
        label = framesPerSecond > 0
            ? Math.Round(seconds * framesPerSecond).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : (key / 1000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (Labels.Count < 4096)
            Labels[key] = label;
        return label;
    }
}

/// <summary>
/// Base of the timeline canvases (G1.5): the colours and the ruler, region and playhead drawing they share. The song
/// tab's canvases and the Animation panel's derive from it.
/// </summary>
public abstract class TimelineCanvasBase : EditorCanvas
{
    protected static readonly Vector4 RulerFill = Rgb(0x26, 0x29, 0x31);
    protected static readonly Vector4 RulerTextColor = Rgb(0x8b, 0x93, 0xa5);
    protected static readonly Vector4 GridMajor = Rgb(0x3b, 0x3f, 0x4b);
    protected static readonly Vector4 GridMinor = Rgb(0x2a, 0x2d, 0x36);
    protected static readonly Vector4 PlayheadColor = Rgb(0xf8, 0x71, 0x71);

    /// <summary>A seconds ruler <paramref name="height"/> dp tall across the canvas, with grid lines down to <paramref name="gridBottom"/>.</summary>
    protected void DrawSecondsRuler(TimelineZoom view, float height, float gridBottom, double framesPerSecond = 0)
    {
        var w = ViewSize.X;
        Box(0, 0, w, height, RulerFill);
        var (step, subdivisions) = TimelineRuler.StepFor(view.PixelsPerUnit);
        var minor = step / subdivisions;
        var first = TimelineSnap.Floor(view.Scroll, minor);
        for (var i = 0; ; i++)
        {
            var t = first + i * minor;
            var x = view.X(t);
            if (x > w)
                break;
            if (x < 0)
                continue;
            var major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            Line(x, major ? 0 : height * 0.6f, x, height, RulerTextColor, 1);
            Line(x, height, x, gridBottom, major ? GridMajor : GridMinor, 1);
            if (major)
                Text(TimelineRuler.Label(t, framesPerSecond), x + 3, height * 0.55f, 10, RulerTextColor);
        }
    }

    /// <summary>A shaded region from time <paramref name="from"/> to <paramref name="to"/> (a loop band, past the end).</summary>
    protected void DrawRegion(TimelineZoom view, double from, double to, float top, float bottom, Vector4 color)
    {
        var x0 = Math.Max(0f, view.X(from));
        var x1 = Math.Min(ViewSize.X, view.X(to));
        if (x1 > x0)
            Box(x0, top, x1 - x0, bottom - top, color);
    }

    /// <summary>The playhead at <paramref name="time"/>: a line and a marker on the ruler.</summary>
    protected void DrawPlayhead(TimelineZoom view, double time, float rulerHeight, float bottom)
    {
        var x = view.X(time);
        if (x < -4 || x > ViewSize.X + 4)
            return;
        Line(x, 0, x, bottom, PlayheadColor, 1.5f);
        Box(x - 4, rulerHeight - 7, 8, 7, PlayheadColor);
    }
}
