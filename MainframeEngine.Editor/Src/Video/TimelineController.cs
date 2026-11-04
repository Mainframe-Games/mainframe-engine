using System.Numerics;
using MainframeEngine.Video.Timelines;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Video;

public enum TimelineGesture { None, Pending, Scrub, Move, Trim, Marquee }

/// <summary>
/// The multi-track timeline's mouse gestures (positions in canvas dp), Premiere's conventions:
/// <list type="bullet">
/// <item>Ruler: press or drag to scrub.</item>
/// <item>Select tool: click a clip to select it and its linked partners (Shift adds or removes); drag it to move it, onto
/// another track of its kind too; drag a clip edge to trim it (Cmd: ripple trim); drag in empty space for a marquee.</item>
/// <item>Razor tool: click a clip to cut it (and its partners) there.</item>
/// <item>Snapping (S) catches clip edges, the playhead, markers and in/out within 8 dp.</item>
/// <item>The wheel zooms about the pointer; Shift scrolls in time, Alt scrolls the tracks.</item>
/// </list>
/// Gestures re-apply their edit to the timeline as it was at the press under one merge key
/// (<see cref="TimelineDocument.ApplyFrom"/>): one undo step per drag.
/// </summary>
public sealed class TimelineController
{
    private readonly TimelineDocument _doc;
    private readonly TimelineViewState _view;
    private Vector2 _press;
    private Timeline? _base;
    private Clip? _clip;
    private TrimEdge _edge;
    private bool _ripple;
    private bool _toggled;
    private IReadOnlyList<double> _candidates = [];

    public TimelineController(TimelineDocument document, TimelineViewState view)
    {
        _doc = document ?? throw new ArgumentNullException(nameof(document));
        _view = view ?? throw new ArgumentNullException(nameof(view));
    }

    public TimelineGesture Gesture { get; private set; }

    /// <summary>Called with timeline seconds when the ruler is scrubbed.</summary>
    public Action<double>? Seek { get; set; }

    /// <summary>Called on a right-click: the clip under the pointer (null: empty space) and the local position.</summary>
    public Action<Clip?, float, float>? ShowMenu { get; set; }

    private Timeline Timeline => _doc.Timeline;

    public void Hover(Vector2 p)
    {
        if (Gesture != TimelineGesture.None)
            return;
        _view.HoverEdge = _view.Tool == TimelineTool.Select && EdgeAt(p) is { } edge ? (edge.Clip.Id, edge.Edge) : null;
    }

    public void Press(Vector2 p, EditorModifiers modifiers)
    {
        CancelGesture();
        _press = p;
        if (p.Y < TimelineViewState.RulerHeight)
        {
            Gesture = TimelineGesture.Scrub;
            Seek?.Invoke(Math.Max(0, _view.XToTime(p.X)));
            return;
        }

        var clip = ClipAt(p);
        if (_view.Tool == TimelineTool.Razor)
        {
            if (clip is not null)
            {
                var at = SnapTime(_view.XToTime(p.X), Snapping.Candidates(Timeline, _view.Playhead));
                _doc.Apply("Razor", t => TimelineEdits.Razor(t, at, [clip.TrackId]));
                _view.SnapLine = null;
            }

            return;
        }

        _base = Timeline;
        if (EdgeAt(p) is { } edge)
        {
            Gesture = TimelineGesture.Trim;
            _clip = edge.Clip;
            _edge = edge.Edge;
            _ripple = (modifiers & EditorModifiers.Command) != 0;
            SelectGroup(edge.Clip, add: false);
            _candidates = Snapping.Candidates(Timeline, _view.Playhead, GroupIds(edge.Clip));
            return;
        }

        _clip = clip;
        _toggled = false;
        if (clip is not null)
        {
            if ((modifiers & EditorModifiers.Shift) != 0)
            {
                ToggleGroup(clip);
                _toggled = true;
            }
            else if (!_view.Selection.Contains(clip.Id))
            {
                SelectGroup(clip, add: false);
            }
        }
        else if ((modifiers & EditorModifiers.Shift) == 0)
        {
            _view.Selection.Clear();
        }

        Gesture = TimelineGesture.Pending;
    }

    public void Drag(Vector2 p, EditorModifiers modifiers)
    {
        switch (Gesture)
        {
            case TimelineGesture.Scrub:
                Seek?.Invoke(Math.Max(0, _view.XToTime(p.X)));
                break;
            case TimelineGesture.Pending when Vector2.Distance(p, _press) > TimelineViewState.DragThreshold:
                if (_clip is not null && !_toggled)
                {
                    Gesture = TimelineGesture.Move;
                    _candidates = Snapping.Candidates(Timeline, _view.Playhead, _view.Selection);
                }
                else if (_clip is null)
                {
                    Gesture = TimelineGesture.Marquee;
                }

                Drag(p, modifiers);
                break;
            case TimelineGesture.Move:
                Move(p);
                break;
            case TimelineGesture.Trim:
                Trim(p);
                break;
            case TimelineGesture.Marquee:
                _view.Marquee = (_press.X, _press.Y, p.X, p.Y);
                break;
        }
    }

    public void Release(Vector2 p)
    {
        if (Gesture == TimelineGesture.Marquee && _view.Marquee is { } m)
            SelectInRect(MathF.Min(m.X0, m.X1), MathF.Min(m.Y0, m.Y1), MathF.Max(m.X0, m.X1), MathF.Max(m.Y0, m.Y1));
        _doc.History.EndMerge();
        Reset();
        Hover(p);
    }

    /// <summary>The pointer was lost, or the tab hidden: a drag stays where it is (one step), a marquee is dropped.</summary>
    public void CancelGesture()
    {
        if (Gesture is TimelineGesture.Move or TimelineGesture.Trim)
            _doc.History.EndMerge();
        Reset();
    }

    public void RightClick(Vector2 p)
    {
        if (Gesture != TimelineGesture.None || p.Y < TimelineViewState.RulerHeight)
            return;
        var clip = ClipAt(p);
        if (clip is not null && !_view.Selection.Contains(clip.Id))
            SelectGroup(clip, add: false);
        ShowMenu?.Invoke(clip, p.X, p.Y);
    }

    public void Wheel(Vector2 p, Vector2 delta, EditorModifiers modifiers)
    {
        if ((modifiers & EditorModifiers.Alt) != 0)
        {
            var max = MathF.Max(0, _view.ContentHeight(Timeline) - (_view.Height - TimelineViewState.RulerHeight));
            _view.ScrollY = Math.Clamp(_view.ScrollY - delta.Y * 30, 0, max);
        }
        else if (delta.X != 0)
        {
            _view.Pan(-delta.X * (_view.End - _view.Start) * 0.1);
        }
        else if ((modifiers & EditorModifiers.Shift) != 0)
        {
            _view.Pan(-delta.Y * (_view.End - _view.Start) * 0.1);
        }
        else if (delta.Y != 0)
        {
            _view.ZoomAbout(p.X, delta.Y);
        }
    }

    // ── Gestures ─────────────────────────────────────────────────────────────────────────────────────────────

    private void Move(Vector2 p)
    {
        var clip = _clip!;
        var length = TimelineLayout.Length(clip, Timeline.Rate);
        var start = Math.Max(0, clip.Start + _view.XToTime(p.X) - _view.XToTime(_press.X));
        _view.SnapLine = null;
        if (_view.Snapping)
        {
            var (snapped, did) = Snapping.SnapSpan(start, length, _candidates, Tolerance);
            if (did)
                _view.SnapLine = Snapping.Snap(snapped, _candidates, 1e-6).Snapped ? snapped : snapped + length; // the edge that caught
            start = snapped;
        }

        var lane = _view.LaneAt(Timeline, p.Y);
        var track = lane is { } l && l.Kind == KindOf(clip) ? l.TrackId : clip.TrackId;
        var shift = start - clip.Start;
        // The other selected clips (one per link group, not the dragged clip's group) move by the same time, on their tracks.
        var others = Groups(_view.Selection).Where(id => _base!.FindClip(id) is { } c && c.Id != clip.Id
            && (c.LinkGroup is null || c.LinkGroup != clip.LinkGroup)).ToList();
        _doc.ApplyFrom(_base!, "Move Clip", t =>
        {
            var moved = TimelineEdits.Move(t, clip.Id, start, track);
            foreach (var id in others)
                moved = TimelineEdits.Move(moved, id, _base!.FindClip(id)!.Start + shift);
            return moved;
        }, mergeKey: "timeline.move");
    }

    private void Trim(Vector2 p)
    {
        var clip = _clip!;
        var time = SnapTime(_view.XToTime(p.X), _candidates);
        _doc.ApplyFrom(_base!, _ripple ? "Ripple Trim" : "Trim", t => _ripple
            ? TimelineEdits.RippleTrim(t, clip.Id, _edge, time)
            : TimelineEdits.Trim(t, clip.Id, _edge, time), mergeKey: "timeline.trim");
    }

    private double SnapTime(double time, IReadOnlyList<double> candidates)
    {
        time = Math.Max(0, time);
        if (!_view.Snapping)
            return time;
        var (snapped, did) = Snapping.Snap(time, candidates, Tolerance);
        _view.SnapLine = did ? snapped : null;
        return snapped;
    }

    private double Tolerance => TimelineViewState.SnapPixels * _view.SecondsPerPixel;

    private void SelectInRect(float x0, float y0, float x1, float y1)
    {
        var t0 = _view.XToTime(x0);
        var t1 = _view.XToTime(x1);
        foreach (var lane in _view.Lanes(Timeline))
        {
            var top = _view.LaneTop(lane);
            if (top + lane.Height < y0 || top > y1)
                continue;
            foreach (var clip in Timeline.Clips)
                if (clip.TrackId == lane.TrackId && clip.Start < t1 && TimelineLayout.End(clip, Timeline.Rate) > t0)
                    SelectGroup(clip, add: true);
        }
    }

    // ── Hit tests ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The clip under the canvas point, or null.</summary>
    public Clip? ClipAt(Vector2 p)
    {
        if (_view.LaneAt(Timeline, p.Y) is not { } lane)
            return null;
        var time = _view.XToTime(p.X);
        return TimelineLayout.ClipAt(Timeline, lane.TrackId, time);
    }

    /// <summary>
    /// The clip edge within grab distance of the point, or null. At a cut (two edges as near), the side the pointer is on
    /// wins: left of the cut trims the outgoing clip's end, right of it the incoming clip's start.
    /// </summary>
    public (Clip Clip, TrimEdge Edge)? EdgeAt(Vector2 p)
    {
        if (_view.LaneAt(Timeline, p.Y) is not { } lane)
            return null;
        (Clip, TrimEdge)? best = null;
        var bestDistance = TimelineViewState.EdgeGrab;
        var bestOnItsSide = false;
        foreach (var clip in Timeline.Clips)
        {
            if (clip.TrackId != lane.TrackId)
                continue;
            var startX = (float)_view.TimeToX(clip.Start);
            var endX = (float)_view.TimeToX(TimelineLayout.End(clip, Timeline.Rate));
            Consider(clip, TrimEdge.Start, MathF.Abs(startX - p.X), p.X >= startX);
            Consider(clip, TrimEdge.End, MathF.Abs(endX - p.X), p.X < endX);
        }

        return best;

        void Consider(Clip clip, TrimEdge edge, float distance, bool onItsSide)
        {
            if (distance > bestDistance || (distance == bestDistance && best is not null && (bestOnItsSide || !onItsSide)))
                return;
            best = (clip, edge);
            bestDistance = distance;
            bestOnItsSide = onItsSide;
        }
    }

    // ── Selection ────────────────────────────────────────────────────────────────────────────────────────────

    private void SelectGroup(Clip clip, bool add)
    {
        if (!add)
            _view.Selection.Clear();
        foreach (var member in Timeline.LinkedWith(clip))
            _view.Selection.Add(member.Id);
    }

    private void ToggleGroup(Clip clip)
    {
        var remove = _view.Selection.Contains(clip.Id);
        foreach (var member in Timeline.LinkedWith(clip))
            if (remove)
                _view.Selection.Remove(member.Id);
            else
                _view.Selection.Add(member.Id);
    }

    private HashSet<string> GroupIds(Clip clip) => Timeline.LinkedWith(clip).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

    // One clip id per link group among the ids.
    private List<string> Groups(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in ids)
            if (_base?.FindClip(id) is { } clip && seen.Add(clip.LinkGroup ?? clip.Id))
                result.Add(id);
        return result;
    }

    private TrackKind KindOf(Clip clip) => Timeline.FindTrack(clip.TrackId)?.Kind ?? TrackKind.Video;

    private void Reset()
    {
        Gesture = TimelineGesture.None;
        _base = null;
        _clip = null;
        _view.SnapLine = null;
        _view.Marquee = null;
        _candidates = [];
    }
}
