using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Video;

public enum VideoGesture { None, Pending, Scrub, Pan, Boundary, Section }

/// <summary>
/// The video timeline's gestures, ported from Cutback's TimelineControl. Positions are in dp, local to the canvas.
/// <list type="bullet">
/// <item>Click a segment to toggle it.</item>
/// <item>Drag a boundary to move it. It snaps to the quietest sample on release, and the whole drag is one undo step.</item>
/// <item>Drag in the body to draw a section with the opposite state of where the drag started.</item>
/// <item>Shift+drag pans; pressing or dragging in the ruler scrubs.</item>
/// <item>The wheel zooms about the cursor; Shift or a horizontal wheel pans.</item>
/// <item>Right-click opens the segment's menu; Delete/Backspace dissolves the hovered segment.</item>
/// </list>
/// </summary>
public sealed class VideoTimelineController
{
    private readonly VideoDocument _doc;
    private readonly VideoTimelineView _view;
    private readonly VideoCanvasState _state;
    private Vector2 _press;
    private double _anchor;
    private double _panStart;

    // The last pointer position seen, so the hover can be recomputed when the partition changes without a mouse move
    // (an undo, a Delete that merges neighbours). Without it the hovered index would name a segment that no longer exists.
    private Vector2? _lastPointer;

    public VideoTimelineController(VideoDocument document, VideoTimelineView view, VideoCanvasState state)
    {
        _doc = document ?? throw new ArgumentNullException(nameof(document));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _doc.Changed += RefreshHover;
    }

    public VideoGesture Gesture { get; private set; }

    /// <summary>Called with source seconds when the ruler is scrubbed.</summary>
    public Action<double>? Seek { get; set; }

    /// <summary>Called on a right-click over a segment, with the segment index and the local x and y.</summary>
    public Action<int, float, float>? ShowSegmentMenu { get; set; }

    public void Hover(Vector2 p)
    {
        _lastPointer = p;
        if (Gesture == VideoGesture.None)
            UpdateHover(p);
    }

    public void Press(Vector2 p, EditorModifiers modifiers)
    {
        CancelGesture();
        _press = p;
        if (p.Y < VideoTimelineView.RulerHeight)
        {
            Gesture = VideoGesture.Scrub;
            Seek?.Invoke(_view.ClampTime(_view.XToTime(p.X)));
            return;
        }

        var boundary = _view.BoundaryAt(_doc.Segments.Segments, p.X, p.Y);
        if (boundary >= 0)
        {
            Gesture = VideoGesture.Boundary;
            _state.DragBoundary = boundary;
            _doc.Editor.BeginBoundaryDrag();
            return;
        }

        Gesture = VideoGesture.Pending;
    }

    public void Drag(Vector2 p, EditorModifiers modifiers)
    {
        switch (Gesture)
        {
            case VideoGesture.Scrub:
                Seek?.Invoke(_view.ClampTime(_view.XToTime(p.X)));
                break;
            case VideoGesture.Boundary:
                _doc.Editor.UpdateBoundaryDrag(_state.DragBoundary, _view.XToTime(p.X));
                break;
            case VideoGesture.Pending when Math.Abs(p.X - _press.X) > VideoTimelineView.DragThreshold:
                if ((modifiers & EditorModifiers.Shift) != 0)
                {
                    Gesture = VideoGesture.Pan;
                    _panStart = _view.Start;
                }
                else
                {
                    Gesture = VideoGesture.Section;
                    _anchor = _view.ClampTime(_view.XToTime(_press.X));
                }

                Drag(p, modifiers);
                break;
            case VideoGesture.Section:
                var t = _view.ClampTime(_view.XToTime(p.X));
                _state.PendingSection = (Math.Min(_anchor, t), Math.Max(_anchor, t));
                break;
            case VideoGesture.Pan:
                _view.SetView(_panStart - (p.X - _press.X) * _view.SecondsPerPixel, _view.Span);
                break;
        }
    }

    public void Release(Vector2 p)
    {
        switch (Gesture)
        {
            case VideoGesture.Pending:
                _doc.Editor.Toggle(_view.SegmentAt(_doc.Segments, p.X, p.Y));
                break;
            case VideoGesture.Boundary:
                _doc.Editor.EndBoundaryDrag(_state.DragBoundary, _doc.SnapTime(_view.XToTime(p.X)));
                break;
            case VideoGesture.Section when _state.PendingSection is { } section:
                var start = _view.ClampTime(_doc.SnapTime(section.Start));
                var end = _view.ClampTime(_doc.SnapTime(section.End));
                if (end > start)
                    _doc.Editor.CreateSection(start, end, _anchor);
                break;
        }

        Reset();
        _lastPointer = p;
        RefreshHover();
    }

    /// <summary>
    /// Called when the pointer is lost, or another tab or mode activates. A boundary drag ends where it is, unsnapped, as
    /// one step; a section being drawn is dropped.
    /// </summary>
    public void CancelGesture()
    {
        // Guarded as in Cutback: a drag index outside the boundaries must not reach MoveBoundary.
        if (Gesture == VideoGesture.Boundary && _doc.Editor.IsDragging
            && _state.DragBoundary >= 1 && _state.DragBoundary < _doc.Segments.Count)
            _doc.Editor.EndBoundaryDrag(_state.DragBoundary, _doc.Segments.Segments[_state.DragBoundary].Start);
        Reset();
        RefreshHover();
    }

    public void RightClick(Vector2 p)
    {
        if (Gesture != VideoGesture.None)
            return;
        var segment = _view.SegmentAt(_doc.Segments, p.X, p.Y);
        if (segment < 0)
            return;
        _state.HoverSegment = segment;
        ShowSegmentMenu?.Invoke(segment, p.X, p.Y);
    }

    public void Wheel(Vector2 p, Vector2 delta, EditorModifiers modifiers)
    {
        // Negated, as in Cutback: wheel up or left moves the view back in time, so the content slides with the wheel.
        if (delta.X != 0)
            _view.Pan(-delta.X * _view.Span * VideoTimelineView.WheelPanFraction);
        else if ((modifiers & EditorModifiers.Shift) != 0)
            _view.Pan(-delta.Y * _view.Span * VideoTimelineView.WheelPanFraction);
        else if (delta.Y != 0)
            _view.ZoomAbout(p.X, delta.Y);
    }

    /// <summary>Dissolves the hovered segment into its neighbours (Delete, Backspace, or the menu's Delete).</summary>
    public bool DeleteHovered()
    {
        if (!_doc.Editor.Dissolve(_state.HoverSegment))
            return false;
        // The dissolve may have merged neighbours, so the index is recomputed from the pointer rather than cleared.
        _state.HoverSegment = -1;
        RefreshHover();
        return true;
    }

    public bool Key(Key key, EditorModifiers modifiers) =>
        key is Silk.NET.Input.Key.Delete or Silk.NET.Input.Key.Backspace && modifiers == EditorModifiers.None && DeleteHovered();

    private void Reset()
    {
        Gesture = VideoGesture.None;
        _state.DragBoundary = -1;
        _state.PendingSection = null;
    }

    /// <summary>Re-runs the hover hit-test at the last pointer position. Only while no gesture is active.</summary>
    private void UpdateHover(Vector2 p)
    {
        var boundary = _view.BoundaryAt(_doc.Segments.Segments, p.X, p.Y);
        _state.HoverBoundary = boundary;
        _state.HoverSegment = boundary >= 0 ? -1 : _view.SegmentAt(_doc.Segments, p.X, p.Y);
    }

    /// <summary>
    /// Called whenever the document changes (subscribed in the constructor) and at the end of each gesture. A change made
    /// during a gesture is skipped here and picked up when the gesture ends.
    /// </summary>
    private void RefreshHover()
    {
        if (Gesture == VideoGesture.None && _lastPointer is { } p)
            UpdateHover(p);
    }
}
