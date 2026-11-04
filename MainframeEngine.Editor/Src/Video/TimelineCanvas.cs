using System.Numerics;
using MainframeEngine.Video.Timelines;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The multi-track timeline drawn with the engine's 2D canvas (docs/design/future/video-nle.md): the ruler with in/out and
/// markers, one lane per track (video Vn…V1 above audio A1…An), the clips (filmstrips on video, waveforms on audio, a
/// tick where a cut is collapsed, a link mark on linked clips, the selection), the marquee, the snap line and the playhead.
/// It only reads the timeline, the view state and the media cache; the controller owns every change. Steady frames allocate
/// nothing: labels are cached and the cut ticks and waveforms walk the clip's segments in place.
/// </summary>
[Tool]
public sealed class TimelineCanvas : EditorCanvas
{
    private const int LabelCacheLimit = 2048;

    private static readonly Vector4 RulerBackground = Rgb(0x18, 0x18, 0x1C);
    private static readonly Vector4 BodyBackground = Rgb(0x12, 0x12, 0x15);
    private static readonly Vector4 LaneVideo = Rgb(0x17, 0x19, 0x1F);
    private static readonly Vector4 LaneAudio = Rgb(0x15, 0x19, 0x18);
    private static readonly Vector4 LaneOff = Rgb(0x10, 0x10, 0x12);
    private static readonly Vector4 VideoClip = Rgb(0x1E, 0x3A, 0x6B);
    private static readonly Vector4 VideoEdge = Rgb(0x3B, 0x82, 0xF6);
    private static readonly Vector4 AudioClip = Rgb(0x14, 0x53, 0x2D);
    private static readonly Vector4 AudioEdge = Rgb(0x22, 0xC5, 0x5E);
    private static readonly Vector4 Wave = Rgb(0x86, 0xEF, 0xAC);
    private static readonly Vector4 ClipText = Rgb(0xE8, 0xEB, 0xF2);
    private static readonly Vector4 Disabled = Rgb(0x12, 0x12, 0x15, 0.6f);
    private static readonly Vector4 Selected = Rgb(0xFF, 0xD1, 0x66);
    private static readonly Vector4 HoverEdge = Rgb(0xFF, 0xD1, 0x66);
    private static readonly Vector4 CutTick = Rgb(0xFF, 0xD1, 0x66, 0.85f);
    private static readonly Vector4 LinkMark = Rgb(0xC8, 0xC8, 0xD2, 0.8f);
    private static readonly Vector4 Playhead = Rgb(0xFF, 0x5C, 0x5C);
    private static readonly Vector4 Tick = Rgb(0x4E, 0x4E, 0x58);
    private static readonly Vector4 Label = Rgb(0x9A, 0x9A, 0xA6);
    private static readonly Vector4 InOut = Rgb(0x3B, 0x82, 0xF6, 0.35f);
    private static readonly Vector4 Marker = Rgb(0xB4, 0x8C, 0xFF);
    private static readonly Vector4 Snap = Rgb(0xFF, 0xFF, 0xFF, 0.8f);
    private static readonly Vector4 MarqueeFill = Rgb(0x3B, 0x82, 0xF6, 0.15f);
    private static readonly Vector4 MarqueeEdge = Rgb(0x3B, 0x82, 0xF6, 0.8f);
    private static readonly Vector4 Hint = Rgb(0x60, 0x60, 0x6A);

    private readonly Func<Timeline> _timeline;
    private readonly TimelineViewState _view;
    private readonly MediaCache? _cache;
    private readonly Func<MediaRef, string> _resolve;
    private readonly Dictionary<string, (int Chars, string Text)> _labels = new(StringComparer.Ordinal);

    public TimelineCanvas(Func<Timeline> timeline, TimelineViewState view, MediaCache? cache, Func<MediaRef, string> resolve)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _cache = cache;
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    protected override void OnDraw()
    {
        var timeline = _timeline();
        var w = ViewSize.X;
        var h = ViewSize.Y;
        var ruler = TimelineViewState.RulerHeight;
        Box(0, 0, w, h, BodyBackground);

        var lanes = _view.Lanes(timeline);
        for (var i = 0; i < lanes.Count; i++) // indexed: foreach over the interface would box its enumerator every frame
            DrawLane(timeline, lanes[i], w, h);
        if (timeline.Clips.Length == 0)
            Text("Drag media here, or double-click it in the media pool and press , (insert)", 12, ruler + 40, 12, Hint);

        DrawMarquee();
        Box(0, 0, w, ruler, RulerBackground);
        DrawRuler(timeline, w);
        DrawSnapLine(h);
        DrawPlayhead(h);
    }

    private void DrawLane(Timeline timeline, TimelineLane lane, float w, float h)
    {
        var top = _view.LaneTop(lane);
        if (top + lane.Height < TimelineViewState.RulerHeight || top > h)
            return;
        var track = timeline.FindTrack(lane.TrackId);
        var on = track is { Enabled: true, Muted: false };
        Box(0, top, w, lane.Height, !on ? LaneOff : lane.Kind == TrackKind.Video ? LaneVideo : LaneAudio);

        var rate = timeline.Rate;
        foreach (var clip in timeline.Clips)
        {
            if (clip.TrackId != lane.TrackId)
                continue;
            var end = TimelineLayout.End(clip, rate);
            if (end < _view.Start || clip.Start > _view.End)
                continue;
            DrawClip(clip, lane, top, end, rate);
        }
    }

    private void DrawClip(Clip clip, TimelineLane lane, float top, double end, FrameRate rate)
    {
        var x0 = (float)_view.TimeToX(clip.Start);
        var x1 = (float)_view.TimeToX(end);
        var y = top + 2;
        var height = lane.Height - 4;
        var video = lane.Kind == TrackKind.Video;
        var visible0 = MathF.Max(x0, -2);
        var visible1 = MathF.Min(x1, ViewSize.X + 2);
        Box(visible0, y, visible1 - visible0, height, video ? VideoClip : AudioClip);

        var media = clip.Media;
        if (video && _cache?.FilmstripOf(media.Hash, _resolve(media), media.Duration) is { } strip)
            DrawFilmstrip(clip, strip, x0, x1, y, height, rate);
        else if (!video && _cache?.WaveformOf(media.Hash, _resolve(media), media.Duration) is { } wave)
            DrawWave(clip, wave, x0, x1, y, height, rate);

        DrawCutTicks(clip, y, height);
        if (clip.LinkGroup is not null)
            Box(x0 + 3, y + height - 4, MathF.Min(10, x1 - x0 - 6), 2, LinkMark);
        if (x1 - x0 > 24)
            Text(LabelOf(clip, x1 - x0), MathF.Max(x0, 0) + 5, y + 13, 11, ClipText);

        var selected = _view.Selection.Contains(clip.Id);
        Outline(x0, y, x1 - x0, height, selected ? Selected : video ? VideoEdge : AudioEdge, selected ? 2 : 1);
        if (!clip.Enabled)
            Box(visible0, y, visible1 - visible0, height, Disabled);
        if (_view.HoverEdge is { } edge && edge.ClipId == clip.Id)
        {
            var ex = edge.Edge == TrimEdge.Start ? x0 : x1;
            Box(ex - 1.5f, y, 3, height, HoverEdge);
        }
    }

    // Tiles of the filmstrip along the clip: each tile shows the source frame nearest its middle.
    private void DrawFilmstrip(Clip clip, Texture2D strip, float x0, float x1, float y, float height, FrameRate rate)
    {
        var frames = MediaCache.FilmstripFrames;
        var tileW = strip.Width / (float)frames;
        var tileH = strip.Height;
        var drawH = height - 16;
        var drawW = drawH * tileW / MathF.Max(1, tileH);
        if (drawW < 4)
            return;
        var start = MathF.Max(x0, 0);
        var stop = MathF.Min(x1, ViewSize.X);
        for (var x = start - (start - x0) % drawW; x < stop; x += drawW)
        {
            var t = _view.XToTime(x + drawW / 2);
            if (TimelineLayout.SourceTimeAt(clip, rate, Math.Min(t, TimelineLayout.End(clip, rate) - rate.FrameDuration)) is not { } source)
                continue;
            var index = Math.Clamp((int)(source / clip.Media.Duration * frames), 0, frames - 1);
            var w = MathF.Min(drawW, x1 - x);
            DrawTextureRectRegion(strip, new Rect2(x * PixelScale, (y + 14) * PixelScale, w * PixelScale, drawH * PixelScale),
                new Rect2(index * tileW, 0, tileW * w / drawW, tileH));
        }
    }

    private void DrawWave(Clip clip, MainframeEngine.Video.Media.Waveform waveform, float x0, float x1, float y, float height, FrameRate rate)
    {
        var scale = PixelScale;
        var secondsPerColumn = _view.SecondsPerPixel / scale * clip.Speed;
        var level = waveform.LevelFor(secondsPerColumn);
        var peaks = level.Peaks;
        var mid = y + height / 2 + 4;
        var k = (height / 2 - 6) / 32768f;
        var minHalf = 1 / scale;
        var first = (int)(MathF.Max(x0, 0) * scale);
        var last = (int)(MathF.Min(x1, ViewSize.X) * scale);
        var end = TimelineLayout.End(clip, rate);
        for (var c = first; c < last; c++)
        {
            var t = _view.XToTime((c + 0.5) / scale);
            if (t >= end || TimelineLayout.SourceTimeAt(clip, rate, t) is not { } source)
                continue;
            var bucket = (int)(source / level.SecondsPerBucket);
            if (bucket < 0 || bucket >= peaks.Count)
                continue;
            var span = Math.Max(1, (int)(secondsPerColumn / level.SecondsPerBucket));
            var min = short.MaxValue;
            var max = short.MinValue;
            for (var b = bucket; b < Math.Min(peaks.Count, bucket + span); b++)
            {
                if (peaks[b].Min < min)
                    min = peaks[b].Min;
                if (peaks[b].Max > max)
                    max = peaks[b].Max;
            }

            var x = (c + 0.5f) / scale;
            Line(x, mid - MathF.Max(minHalf, max * k), x, mid - MathF.Min(-minHalf, min * k), Wave, minHalf);
        }
    }

    // A tick where footage was cut inside the clip's window (Cutback's cuts, collapsed on the timeline).
    private void DrawCutTicks(Clip clip, float y, float height)
    {
        var kept = 0.0;
        var seenKept = false;
        foreach (var segment in clip.Segments)
        {
            var start = Math.Max(segment.Start, clip.SourceIn);
            var end = Math.Min(segment.End, clip.SourceOut);
            if (end <= start)
                continue;
            if (segment.Enabled)
            {
                kept += end - start;
                seenKept = true;
                continue;
            }

            if (!seenKept || end >= clip.SourceOut)
                continue; // a cut at the window's edge is not inside the clip
            var x = (float)_view.TimeToX(clip.Start + kept / clip.Speed);
            Box(x - 0.75f, y + 2, 1.5f, height - 4, CutTick);
        }
    }

    private string LabelOf(Clip clip, float width)
    {
        var chars = Math.Max(1, (int)((width - 10) / 6.2f));
        if (_labels.TryGetValue(clip.Id, out var cached) && cached.Chars == chars)
            return cached.Text;
        if (_labels.Count >= LabelCacheLimit)
            _labels.Clear();
        var name = Path.GetFileName(clip.Media.Path);
        if (Math.Abs(clip.Speed - 1) > 1e-6)
            name += $" ({clip.Speed:0.##}×)";
        var text = name.Length <= chars ? name : chars <= 1 ? "…" : name[..(chars - 1)] + "…";
        _labels[clip.Id] = (chars, text);
        return text;
    }

    private void DrawRuler(Timeline timeline, float w)
    {
        var ruler = TimelineViewState.RulerHeight;
        var (major, minor) = _view.RulerIntervals();
        if (timeline.InPoint is { } i || timeline.OutPoint is not null)
        {
            var a = (float)_view.TimeToX(timeline.InPoint ?? 0);
            var b = (float)_view.TimeToX(timeline.OutPoint ?? TimelineLayout.Duration(timeline));
            Box(a, ruler - 6, b - a, 6, InOut);
        }

        var perMajor = Math.Max(1, (long)Math.Round(major / minor));
        var firstTick = (long)Math.Floor(_view.Start / minor);
        var lastTick = (long)Math.Ceiling(_view.End / minor);
        for (var k = firstTick; k <= lastTick; k++)
        {
            var t = k * minor;
            var x = PixelCentre(_view.TimeToX(t));
            var isMajor = k % perMajor == 0;
            Line(x, ruler - (isMajor ? 10 : 5), x, ruler, Tick);
            if (isMajor)
                Text(VideoTimelineCanvas.RulerLabel(t, major), x + 3, 11, 10, Label);
        }

        foreach (var marker in timeline.Markers)
        {
            var x = (float)_view.TimeToX(marker.Time);
            if (x < -10 || x > w + 10)
                continue;
            Box(x - 3, ruler - 9, 6, 6, Marker);
        }

        Line(0, ruler - 0.5f, w, ruler - 0.5f, Tick);
    }

    private void DrawMarquee()
    {
        if (_view.Marquee is not { } m)
            return;
        var x = MathF.Min(m.X0, m.X1);
        var y = MathF.Min(m.Y0, m.Y1);
        var mw = MathF.Abs(m.X1 - m.X0);
        var mh = MathF.Abs(m.Y1 - m.Y0);
        Box(x, y, mw, mh, MarqueeFill);
        Outline(x, y, mw, mh, MarqueeEdge);
    }

    private void DrawSnapLine(float h)
    {
        if (_view.SnapLine is not { } t)
            return;
        var x = PixelCentre(_view.TimeToX(t));
        Line(x, 0, x, h, Snap);
    }

    private void DrawPlayhead(float h)
    {
        if (_view.Playhead < _view.Start || _view.Playhead > _view.End)
            return;
        var x = PixelCentre(_view.TimeToX(_view.Playhead));
        Line(x, 0, x, h, Playhead, 1.5f);
        for (var row = 0; row < 8; row++)
        {
            var half = 6 * (1 - row / 8f);
            Line(x - half, row + 0.5f, x + half, row + 0.5f, Playhead);
        }
    }

    private float PixelCentre(double x) => (MathF.Round((float)x * PixelScale) + 0.5f) / PixelScale;
}
