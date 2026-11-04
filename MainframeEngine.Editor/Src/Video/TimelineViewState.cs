using MainframeEngine.Video.Timelines;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Video;

/// <summary>The timeline's tools (Premiere's V and C).</summary>
public enum TimelineTool
{
    Select,
    Razor,
}

/// <summary>One track's band on the timeline canvas, in dp from the canvas top (before scrolling).</summary>
public readonly record struct TimelineLane(string TrackId, TrackKind Kind, float Y, float Height);

/// <summary>
/// The timeline's view and gesture state (plain data the canvas draws and the controller changes): zoom and scroll, the
/// playhead, the selected clips, the tool, snapping, the target tracks for insert and overwrite, and what a gesture
/// shows (the snap line, the marquee). Lanes stack video tracks top-down from the highest (Vn … V1), then audio
/// (A1 … An), as in Resolve and Premiere.
/// </summary>
public sealed class TimelineViewState
{
    public const float RulerHeight = 24;
    public const float VideoLaneHeight = 44;
    public const float AudioLaneHeight = 34;
    public const float LaneGap = 2;
    public const float EdgeGrab = 6;
    public const float DragThreshold = 4;
    public const float SnapPixels = 8;
    public const double MinPixelsPerSecond = 0.5;
    public const double MaxPixelsPerSecond = 2400;
    public const double WheelZoomBase = 1.25;

    private readonly List<TimelineLane> _lanes = [];
    private Timeline? _lanesFor;

    /// <summary>The canvas size in dp.</summary>
    public float Width { get; set; } = 800;

    public float Height { get; set; } = 200;

    /// <summary>The time at the canvas's left edge.</summary>
    public double Start { get; private set; }

    public double PixelsPerSecond { get; private set; } = 40;

    /// <summary>The lanes' vertical scroll in dp (0 = the first lane under the ruler).</summary>
    public float ScrollY { get; set; }

    public double Playhead { get; set; }

    public HashSet<string> Selection { get; } = new(StringComparer.Ordinal);

    public TimelineTool Tool { get; set; }

    public bool Snapping { get; set; } = true;

    /// <summary>The video track insert and overwrite place on (the header's target), or null for the first.</summary>
    public string? TargetVideo { get; set; }

    /// <summary>The audio track insert and overwrite place on, or null for the first.</summary>
    public string? TargetAudio { get; set; }

    /// <summary>Where the last snap caught, drawn as a line while a gesture runs.</summary>
    public double? SnapLine { get; set; }

    /// <summary>The marquee being dragged, in canvas dp.</summary>
    public (float X0, float Y0, float X1, float Y1)? Marquee { get; set; }

    /// <summary>The clip edge under the pointer (a trim would grab it).</summary>
    public (string ClipId, TrimEdge Edge)? HoverEdge { get; set; }

    /// <summary>Bumped when the view moves (zoom, scroll), so cached drawing can tell.</summary>
    public int Version { get; private set; }

    public double SecondsPerPixel => 1 / PixelsPerSecond;

    public double End => Start + Width * SecondsPerPixel;

    public double TimeToX(double time) => (time - Start) * PixelsPerSecond;

    public double XToTime(double x) => Start + x / PixelsPerSecond;

    /// <summary>Shows <paramref name="seconds"/> across the width (at least 10 s), from 0.</summary>
    public void Fit(double seconds)
    {
        Start = 0;
        PixelsPerSecond = Math.Clamp(Width / Math.Max(10, seconds * 1.05), MinPixelsPerSecond, MaxPixelsPerSecond);
        Version++;
    }

    public void SetView(double start, double pixelsPerSecond)
    {
        Start = Math.Max(0, start);
        PixelsPerSecond = Math.Clamp(pixelsPerSecond, MinPixelsPerSecond, MaxPixelsPerSecond);
        Version++;
    }

    /// <summary>Zooms about <paramref name="x"/> by a wheel step (up zooms in).</summary>
    public void ZoomAbout(double x, double wheelDeltaY)
    {
        var at = XToTime(x);
        var pps = Math.Clamp(PixelsPerSecond * Math.Pow(WheelZoomBase, wheelDeltaY), MinPixelsPerSecond, MaxPixelsPerSecond);
        SetView(at - x / pps, pps);
    }

    public void Pan(double seconds) => SetView(Start + seconds, PixelsPerSecond);

    /// <summary>Scrolls so <paramref name="time"/> is visible (playback follows the playhead). True when it moved.</summary>
    public bool KeepVisible(double time)
    {
        var span = End - Start;
        if (time >= Start && time <= End - span * 0.05)
            return false;
        SetView(time - span * 0.1, PixelsPerSecond);
        return true;
    }

    /// <summary>The lanes of <paramref name="timeline"/>'s tracks (cached per track list).</summary>
    public IReadOnlyList<TimelineLane> Lanes(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (_lanesFor is { } cached && cached.Tracks == timeline.Tracks)
            return _lanes;
        _lanes.Clear();
        var y = RulerHeight + LaneGap;
        var video = timeline.TracksOf(TrackKind.Video);
        for (var i = video.Count - 1; i >= 0; i--)
        {
            _lanes.Add(new TimelineLane(video[i].Id, TrackKind.Video, y, VideoLaneHeight));
            y += VideoLaneHeight + LaneGap;
        }

        foreach (var track in timeline.TracksOf(TrackKind.Audio))
        {
            _lanes.Add(new TimelineLane(track.Id, TrackKind.Audio, y, AudioLaneHeight));
            y += AudioLaneHeight + LaneGap;
        }

        _lanesFor = timeline;
        return _lanes;
    }

    /// <summary>The lanes' total height below the ruler, for the scroll range.</summary>
    public float ContentHeight(Timeline timeline)
    {
        var lanes = Lanes(timeline);
        return lanes.Count == 0 ? 0 : lanes[^1].Y + lanes[^1].Height - RulerHeight;
    }

    /// <summary>The lane under canvas y (scroll applied), or null.</summary>
    public TimelineLane? LaneAt(Timeline timeline, float y)
    {
        if (y < RulerHeight)
            return null;
        Lanes(timeline);
        foreach (var lane in _lanes) // the list itself: its enumerator is a struct (hover asks every pointer move)
            if (y + ScrollY >= lane.Y && y + ScrollY < lane.Y + lane.Height + LaneGap)
                return lane;
        return null;
    }

    /// <summary>The on-screen top of a lane (scroll applied).</summary>
    public float LaneTop(TimelineLane lane) => lane.Y - ScrollY;

    /// <summary>The track insert and overwrite place on, of <paramref name="kind"/>: the target, else the first unlocked one.</summary>
    public string? TargetOf(Timeline timeline, TrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var wanted = kind == TrackKind.Video ? TargetVideo : TargetAudio;
        if (wanted is not null && timeline.FindTrack(wanted) is { Locked: false } chosen && chosen.Kind == kind)
            return chosen.Id;
        return timeline.TracksOf(kind).FirstOrDefault(t => !t.Locked)?.Id;
    }

    /// <summary>Drops selected ids that no longer exist (after an undo or a delete).</summary>
    public void PruneSelection(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (Selection.Count > 0)
            Selection.RemoveWhere(id => timeline.FindClip(id) is null);
    }

    /// <summary>The ruler's major and minor intervals for this zoom: majors at least 80 dp apart.</summary>
    public (double Major, double Minor) RulerIntervals() => VideoTimelineView.RulerIntervals(PixelsPerSecond);
}
