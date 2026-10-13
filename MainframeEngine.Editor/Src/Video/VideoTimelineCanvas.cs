using System.Numerics;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The video timeline drawn with the engine's 2D canvas: kept and removed regions, the waveform, segment boundaries, the
/// section being drawn, the ruler, recording markers and the playhead. Ported from Cutback's <c>TimelineControl</c> draw
/// operation (Skia there, <see cref="EditorCanvas"/> helpers here), in the same order and with the same palette, so it
/// looks like the app it came from. It only reads the document, the view and the state; the controller owns every change.
/// Drawing allocates nothing in steady state: ruler labels come from a cache.
/// </summary>
[Tool]
public sealed class VideoTimelineCanvas : EditorCanvas
{
    private const int LabelCacheLimit = 4096;

    private static readonly Vector4 RulerBackground = Rgb(0x18, 0x18, 0x1C);
    private static readonly Vector4 BodyBackground = Rgb(0x12, 0x12, 0x15);
    private static readonly Vector4 Kept = Rgb(0x1D, 0x2E, 0x4D);
    private static readonly Vector4 KeptHover = Rgb(0x26, 0x40, 0x6A);
    private static readonly Vector4 Removed = Rgb(0x1F, 0x1F, 0x24);
    private static readonly Vector4 RemovedHover = Rgb(0x2A, 0x2A, 0x31);
    private static readonly Vector4 KeptWave = Rgb(0x6F, 0xA3, 0xF0);
    private static readonly Vector4 RemovedWave = Rgb(0x45, 0x45, 0x4E);
    private static readonly Vector4 Boundary = Rgb(0xC8, 0xC8, 0xD2, 0xB0 / 255f);
    private static readonly Vector4 BoundaryActive = Rgb(0xFF, 0xD1, 0x66);
    private static readonly Vector4 SectionFill = Rgb(0xFF, 0xD1, 0x66, 0x38 / 255f);
    private static readonly Vector4 Playhead = Rgb(0xFF, 0x5C, 0x5C);
    private static readonly Vector4 Tick = Rgb(0x4E, 0x4E, 0x58);
    private static readonly Vector4 Label = Rgb(0x9A, 0x9A, 0xA6);
    private static readonly Vector4 ReasonText = Rgb(0x7C, 0x7C, 0x88);
    private static readonly Vector4 Hint = Rgb(0x60, 0x60, 0x6A);
    private static readonly Vector4 Marker = Rgb(0xB4, 0x8C, 0xFF);

    private static readonly Dictionary<long, string> RulerLabels = [];

    private readonly VideoDocument _document;
    private readonly VideoTimelineView _view;
    private readonly VideoCanvasState _state;

    public VideoTimelineCanvas(VideoDocument document, VideoTimelineView view, VideoCanvasState state)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(state);
        _document = document;
        _view = view;
        _state = state;
    }

    /// <summary>
    /// The waveform buckets under one device-pixel column, or (-1, -1) when the column is past the end. A column covers at
    /// least one bucket, so zooming in past the finest level repeats a bucket instead of leaving gaps.
    /// </summary>
    public static (int First, int Last) BucketsForColumn(double viewStart, double secondsPerPixel, double secondsPerBucket, int column, int bucketCount)
    {
        var first = (int)Math.Floor((viewStart + column * secondsPerPixel) / secondsPerBucket);
        if (first >= bucketCount || first < 0)
            return (-1, -1);
        var last = (int)Math.Floor((viewStart + (column + 1) * secondsPerPixel) / secondsPerBucket) - 1;
        return (first, Math.Clamp(last, first, bucketCount - 1));
    }

    protected override void OnDraw()
    {
        var w = ViewSize.X;
        var h = ViewSize.Y;
        var ruler = VideoTimelineView.RulerHeight;

        Box(0, 0, w, ruler, RulerBackground);
        Box(0, ruler, w, h - ruler, BodyBackground);
        if (_view.Duration <= 0)
        {
            Text("Open a recording to see its timeline", 12, h / 2, 13, Hint);
            return;
        }

        DrawSegments(w, h);
        DrawWaveform(w, h);
        DrawBoundaries(h);
        DrawPendingSection(h);
        DrawRuler(w);
        DrawMarkers();
        DrawPlayhead(h);
    }

    private static string RulerLabel(double seconds)
    {
        var key = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (RulerLabels.TryGetValue(key, out var label))
            return label;
        if (RulerLabels.Count >= LabelCacheLimit)
            RulerLabels.Clear();
        return RulerLabels[key] = VideoFormat.Ruler(key);
    }

    /// <summary>The dp position of the centre of the device pixel containing <paramref name="x"/>, so a 1-pixel line stays crisp.</summary>
    private float PixelCentre(double x) => (MathF.Round((float)x * PixelScale) + 0.5f) / PixelScale;

    private void DrawSegments(float w, float h)
    {
        var top = VideoTimelineView.RulerHeight;
        var segments = _document.Segments.Segments;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.End < _view.Start || segment.Start > _view.End)
                continue;

            var x0 = MathF.Max(0, (float)_view.TimeToX(segment.Start));
            var x1 = MathF.Min(w, (float)_view.TimeToX(segment.End));
            var hover = i == _state.HoverSegment;
            Box(x0, top, x1 - x0, h - top, segment.Enabled ? (hover ? KeptHover : Kept) : (hover ? RemovedHover : Removed));

            if (!segment.Enabled && segment.Reason is { Length: > 0 } reason && x1 - x0 > 70)
                Text(reason, x0 + 6, top + 14, 11, ReasonText);
        }
    }

    private void DrawWaveform(float w, float h)
    {
        if (_document.Waveform is not { } waveform)
            return;

        var ruler = VideoTimelineView.RulerHeight;
        var segments = _document.Segments.Segments;
        var scale = PixelScale;
        var secondsPerPixel = _view.SecondsPerPixel / scale;
        var level = waveform.LevelFor(secondsPerPixel);
        var peaks = level.Peaks;
        var mid = ruler + (h - ruler) / 2;
        var k = ((h - ruler) / 2 - 4) / 32768f;
        var minHalf = 1 / scale;
        var columns = (int)(w * scale);

        // One vertical line per device-pixel column, coloured by the segment under it. The segment index only moves
        // forward, so the whole pass is linear.
        var segmentIndex = Math.Max(0, _document.Segments.IndexAt(_view.ClampTime(_view.Start)));
        for (var c = 0; c < columns; c++)
        {
            var (first, last) = BucketsForColumn(_view.Start, secondsPerPixel, level.SecondsPerBucket, c, peaks.Count);
            if (first < 0)
                continue;

            var min = short.MaxValue;
            var max = short.MinValue;
            for (var b = first; b <= last; b++)
            {
                var peak = peaks[b];
                if (peak.Min < min)
                    min = peak.Min;
                if (peak.Max > max)
                    max = peak.Max;
            }

            var t = _view.Start + c * secondsPerPixel;
            while (segmentIndex < segments.Count - 1 && segments[segmentIndex].End <= t)
                segmentIndex++;

            var enabled = segments.Count == 0 || segments[segmentIndex].Enabled;
            var x = (c + 0.5f) / scale;
            Line(x, mid - MathF.Max(minHalf, max * k), x, mid - MathF.Min(-minHalf, min * k), enabled ? KeptWave : RemovedWave, minHalf);
        }
    }

    private void DrawBoundaries(float h)
    {
        var ruler = VideoTimelineView.RulerHeight;
        var segments = _document.Segments.Segments;
        for (var i = 1; i < segments.Count; i++)
        {
            var t = segments[i].Start;
            if (t < _view.Start || t > _view.End)
                continue;

            var active = i == _state.HoverBoundary || i == _state.DragBoundary;
            var x = PixelCentre(_view.TimeToX(t));
            Line(x, ruler, x, h, active ? BoundaryActive : Boundary, active ? 2 : 1);
        }
    }

    private void DrawPendingSection(float h)
    {
        if (_state.PendingSection is not { } section)
            return;

        var ruler = VideoTimelineView.RulerHeight;
        var x0 = (float)_view.TimeToX(Math.Min(section.Start, section.End));
        var x1 = (float)_view.TimeToX(Math.Max(section.Start, section.End));
        Box(x0, ruler, x1 - x0, h - ruler, SectionFill);
        var e0 = PixelCentre(x0);
        var e1 = PixelCentre(x1);
        Line(e0, ruler, e0, h, BoundaryActive);
        Line(e1, ruler, e1, h, BoundaryActive);
    }

    private void DrawRuler(float w)
    {
        var ruler = VideoTimelineView.RulerHeight;
        var (major, minor) = VideoTimelineView.RulerIntervals(1 / _view.SecondsPerPixel);

        // Integer tick indices, not accumulated doubles: 0.1 + 0.1 + ... drifts enough to mislabel seconds after a few dozen ticks.
        var perMajor = (long)Math.Round(major / minor);
        var firstTick = (long)Math.Floor(_view.Start / minor);
        var lastTick = (long)Math.Ceiling(_view.End / minor);
        for (var k = firstTick; k <= lastTick; k++)
        {
            var t = k * minor;
            var x = PixelCentre(_view.TimeToX(t));
            var isMajor = k % perMajor == 0;
            Line(x, ruler - (isMajor ? 10 : 5), x, ruler, Tick);
            if (isMajor)
                Text(RulerLabel(t), x + 3, 11, 10, Label);
        }

        Line(0, ruler - 0.5f, w, ruler - 0.5f, Tick);
    }

    private void DrawMarkers()
    {
        if (_document.Capture?.Markers is not { Count: > 0 } markers)
            return;

        var ruler = VideoTimelineView.RulerHeight;
        for (var i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            if (marker.Time < _view.Start || marker.Time > _view.End)
                continue;

            var x = (float)_view.TimeToX(marker.Time);
            DrawDownTriangle(x, ruler - 9, 8, 8, Marker);
            Text(marker.Label, x + 6, ruler - 2, 10, Marker);
        }
    }

    private void DrawPlayhead(float h)
    {
        if (_state.Playhead < _view.Start || _state.Playhead > _view.End)
            return;

        var x = PixelCentre(_view.TimeToX(_state.Playhead));
        Line(x, 0, x, h, Playhead, 1.5f);
        DrawDownTriangle(x, 0, 12, 8, Playhead);
    }

    /// <summary>A triangle pointing down, centred on <paramref name="x"/>, drawn as one 1 dp line per row (the canvas has no filled polygon helper).</summary>
    private void DrawDownTriangle(float x, float top, float width, int height, Vector4 color)
    {
        for (var row = 0; row < height; row++)
        {
            var half = width * (1 - (float)row / height) / 2;
            var y = top + row + 0.5f;
            Line(x - half, y, x + half, y, color);
        }
    }
}
