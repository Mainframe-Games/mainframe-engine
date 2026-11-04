using MainframeEngine.Video;
using MainframeEngine.Video.Capture;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Timelines;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// What the cut editor edits (<see cref="VideoTimelineController"/>, <see cref="VideoTimelineCanvas"/>): one cut list
/// (Cutback's segment partition) with its waveform and recording sidecar, and the edits the gestures make.
/// </summary>
public interface ICutTarget
{
    /// <summary>The partition shown; replaced after every change (read it again, do not keep it).</summary>
    SegmentList Segments { get; }

    Waveform? Waveform { get; }

    CaptureSidecar? Capture { get; }

    /// <summary>The part of the source the clip shows, shaded outside; null for a whole file.</summary>
    (double Start, double End)? Window { get; }

    bool IsDragging { get; }

    event Action? Changed;

    /// <summary>A boundary time snapped to the waveform's quietest point nearby.</summary>
    double SnapTime(double time);

    bool Toggle(int index);

    bool Dissolve(int index);

    bool CreateSection(double start, double end, double anchor);

    void BeginBoundaryDrag();

    double UpdateBoundaryDrag(int boundaryIndex, double time);

    double EndBoundaryDrag(int boundaryIndex, double time);
}

/// <summary>
/// The cut list of one timeline clip (the cut editor under the tracks): each gesture becomes
/// <see cref="TimelineEdits.SetClipCuts"/> on the timeline, so the clip and its linked partners cut together and the clips
/// after them ripple. A boundary drag re-applies from the timeline at its start under one merge key: one undo step.
/// </summary>
public sealed class ClipCutTarget : ICutTarget
{
    public const double BoundarySnapSeconds = 0.020;

    private readonly TimelineDocument _doc;
    private readonly MediaCache? _cache;
    private SegmentList _segments = new(1);
    private MainframeEngine.Video.Timelines.Timeline? _dragBase;
    private Clip? _clip;

    public ClipCutTarget(TimelineDocument document, MediaCache? cache)
    {
        _doc = document ?? throw new ArgumentNullException(nameof(document));
        _cache = cache;
        _doc.Changed += Refresh;
    }

    /// <summary>The clip shown, or null (the cut editor is empty).</summary>
    public string? ClipId { get; private set; }

    public Clip? Clip => _clip;

    public SegmentList Segments => _segments;

    public Waveform? Waveform => _clip is { } c ? _cache?.WaveformOf(c.Media.Hash, _doc.Resolve(c.Media), c.Media.Duration) : null;

    public CaptureSidecar? Capture => null;

    public (double Start, double End)? Window => _clip is { } c ? (c.SourceIn, c.SourceOut) : null;

    public bool IsDragging => _dragBase is not null;

    public event Action? Changed;

    /// <summary>Shows <paramref name="clipId"/>'s cuts (null: none).</summary>
    public void Show(string? clipId)
    {
        if (ClipId == clipId)
            return;
        _dragBase = null;
        ClipId = clipId;
        Refresh();
    }

    public double SnapTime(double time) => Waveform?.SnapToZeroCrossing(time, BoundarySnapSeconds) ?? time;

    public bool Toggle(int index) => index >= 0 && index < _segments.Count && Edit("Toggle Cut", s => s.Toggle(index));

    public bool Dissolve(int index) => index >= 0 && index < _segments.Count && Edit("Delete Cut", s => s.Dissolve(index));

    public bool CreateSection(double start, double end, double anchor)
    {
        var anchorIndex = _segments.IndexAt(anchor);
        if (anchorIndex < 0)
            return false;
        var enabled = !_segments.Segments[anchorIndex].Enabled;
        return Edit(enabled ? "Restore Section" : "Cut Section", s => s.SetRange(start, end, enabled));
    }

    public void BeginBoundaryDrag() => _dragBase = _doc.Timeline;

    public double UpdateBoundaryDrag(int boundaryIndex, double time) => DragTo(boundaryIndex, time);

    public double EndBoundaryDrag(int boundaryIndex, double time)
    {
        var applied = DragTo(boundaryIndex, time);
        _doc.History.EndMerge();
        _dragBase = null;
        return applied;
    }

    private double DragTo(int boundaryIndex, double time)
    {
        if (_clip is not { } clip || _dragBase is not { } from)
            return time;
        var applied = time;
        _doc.ApplyFrom(from, "Move Cut", t => TimelineEdits.SetClipCuts(t, clip.Id, s => applied = s.MoveBoundary(boundaryIndex, time)), mergeKey: "cut.boundary");
        return applied;
    }

    private bool Edit(string name, Action<SegmentList> edit) =>
        _clip is { } clip && _doc.Apply(name, t => TimelineEdits.SetClipCuts(t, clip.Id, edit));

    private void Refresh()
    {
        _clip = ClipId is null ? null : _doc.Timeline.FindClip(ClipId);
        if (_clip is null)
            ClipId = null;
        _segments = _clip is { } c ? new SegmentList(c.Media.Duration, c.Segments) : new SegmentList(1);
        Changed?.Invoke();
    }
}
