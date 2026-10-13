using MainframeEngine.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The video timeline's visible window and its coordinate maths, in dp. The constants are copied from Cutback's
/// TimelineControl, so the timeline feels the same as the app it came from. Pure, so the canvas, the controller and the
/// tests share one definition.
/// </summary>
public sealed class VideoTimelineView
{
    public const float RulerHeight = 22;
    public const float BoundaryGrab = 6;
    public const float DragThreshold = 4;
    public const double MinSpanSeconds = 0.25;
    public const double WheelZoomBase = 1.25;
    public const double WheelPanFraction = 0.1;
    public const double FollowLead = 0.1;
    public const double LabelSpacing = 80;

    /// <summary>
    /// The ruler's major intervals. Below a second each is a whole number of tenths, so the <c>m:ss.t</c> labels are exact
    /// (Cutback's 0.25 s step is dropped: it would read 0:00.3 for 0.25).
    /// </summary>
    public static readonly double[] NiceIntervals = [0.1, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600];

    private float _width = 1;

    public double Duration { get; private set; }

    public double Start { get; private set; }

    public double Span { get; private set; } = 1;

    public double End => Start + Span;

    /// <summary>The canvas width in dp; never less than 1.</summary>
    public float Width
    {
        get => _width;
        set => _width = MathF.Max(1, value);
    }

    public int Version { get; private set; }

    public double SecondsPerPixel => Span / Width;

    /// <summary>Shows the whole recording. The zoom is not saved.</summary>
    public void Reset(double duration)
    {
        Duration = Math.Max(0, duration);
        SetView(0, Duration);
    }

    public void SetView(double start, double span)
    {
        var min = Math.Min(MinSpanSeconds, Duration);
        span = Math.Clamp(span, Math.Max(min, 1e-6), Math.Max(Duration, 1e-6));
        start = Math.Clamp(start, 0, Math.Max(0, Duration - span));
        if (start == Start && span == Span)
            return;
        Start = start;
        Span = span;
        Version++;
    }

    public double TimeToX(double time) => (time - Start) / Span * Width;

    public double XToTime(double x) => Start + x / Width * Span;

    public double ClampTime(double time) => Math.Clamp(time, 0, Duration);

    /// <summary>Wheel zoom about <paramref name="x"/>: the time under the cursor stays put.</summary>
    public void ZoomAbout(double x, double wheelDeltaY)
    {
        var anchor = XToTime(x);
        var span = Math.Clamp(Span * Math.Pow(WheelZoomBase, -wheelDeltaY), Math.Min(MinSpanSeconds, Duration), Duration);
        SetView(anchor - x / Width * span, span);
    }

    public void Pan(double seconds) => SetView(Start + seconds, Span);

    /// <summary>While playing, re-centres when <paramref name="time"/> leaves the view. Returns true if the view moved.</summary>
    public bool KeepVisible(double time)
    {
        if (Span >= Duration || (time >= Start && time <= End))
            return false;
        var version = Version;
        SetView(time - FollowLead * Span, Span);
        return Version != version;
    }

    /// <summary>The boundary (index ≥ 1, the start of segment i) within <see cref="BoundaryGrab"/> dp of x, below the ruler; else -1.</summary>
    public int BoundaryAt(IReadOnlyList<Segment> segments, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (y < RulerHeight)
            return -1;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 1; i < segments.Count; i++)
        {
            var d = Math.Abs(TimeToX(segments[i].Start) - x);
            if (d < BoundaryGrab && d < bestDistance)
                (best, bestDistance) = (i, d);
        }

        return best;
    }

    /// <summary>The segment under (x, y), when the point is below the ruler and inside the canvas; else -1.</summary>
    public int SegmentAt(SegmentList segments, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (y < RulerHeight || x < 0 || x > Width)
            return -1;
        return segments.IndexAt(ClampTime(XToTime(x)));
    }

    /// <summary>The first nice interval at least <see cref="LabelSpacing"/> dp wide, and its minor tick (a fifth of it; a quarter from a minute up).</summary>
    public static (double Major, double Minor) RulerIntervals(double pixelsPerSecond)
    {
        var major = NiceIntervals[^1];
        foreach (var interval in NiceIntervals)
            if (interval * pixelsPerSecond >= LabelSpacing)
            {
                major = interval;
                break;
            }

        return (major, major >= 60 ? major / 4 : major / 5);
    }
}
