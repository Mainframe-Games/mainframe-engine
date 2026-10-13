using System.Numerics;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Models;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoTimelineControllerTests
{
    // 10 s over 1000 dp: 100 dp per second; the body starts at y 22
    private readonly VideoDocument _doc;
    private readonly VideoTimelineView _view = new() { Width = 1000 };
    private readonly VideoCanvasState _state = new();
    private readonly VideoTimelineController _controller;
    private readonly List<double> _seeks = [];

    public VideoTimelineControllerTests()
    {
        var project = VideoDocumentTests.Project("/r/a.mp4", duration: 10) with
        {
            Segments =
            [
                Segment.Create(0, 4, true, SegmentOrigin.Auto),
                Segment.Create(4, 6, false, SegmentOrigin.Auto),
                Segment.Create(6, 10, true, SegmentOrigin.Auto),
            ],
        };
        _doc = VideoDocument.FromProject(project, null, null, []);
        _view.Reset(10);
        _controller = new VideoTimelineController(_doc, _view, _state) { Seek = _seeks.Add };
    }

    private void Click(float x, float y)
    {
        _controller.Press(new Vector2(x, y), EditorModifiers.None);
        _controller.Release(new Vector2(x, y));
    }

    [Fact]
    public void AClickTogglesTheSegmentUnderIt()
    {
        Click(500, 50);
        Assert.True(_doc.Segments.Segments[1].Enabled);
        Assert.Equal(SegmentOrigin.Manual, _doc.Segments.Segments[1].Origin);
    }

    [Fact]
    public void ASmallWobbleIsStillAClick()
    {
        _controller.Press(new Vector2(500, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(503, 50), EditorModifiers.None);
        _controller.Release(new Vector2(503, 50));
        Assert.True(_doc.Segments.Segments[1].Enabled);
    }

    [Fact]
    public void DraggingABoundaryMovesItAsOneUndoStep()
    {
        _controller.Press(new Vector2(402, 50), EditorModifiers.None);
        Assert.Equal(VideoGesture.Boundary, _controller.Gesture);
        Assert.Equal(1, _state.DragBoundary);
        _controller.Drag(new Vector2(380, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(350, 50), EditorModifiers.None);
        _controller.Release(new Vector2(350, 50));
        Assert.Equal(3.5, _doc.Segments.Segments[1].Start, 6); // no waveform: unsnapped
        Assert.Equal(-1, _state.DragBoundary);
        Assert.True(_doc.Editor.Undo());
        Assert.Equal(4, _doc.Segments.Segments[1].Start, 6);
        Assert.False(_doc.Editor.Undo());
    }

    [Fact]
    public void DraggingInTheBodyDrawsASectionWithTheOppositeState()
    {
        _controller.Press(new Vector2(100, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(300, 50), EditorModifiers.None);
        Assert.Equal(VideoGesture.Section, _controller.Gesture);
        Assert.Equal((1.0, 3.0), _state.PendingSection);
        _controller.Release(new Vector2(300, 50));
        var cut = _doc.Segments.Segments[_doc.Segments.IndexAt(2)];
        Assert.False(cut.Enabled);
        Assert.Equal((1.0, 3.0), (Math.Round(cut.Start, 6), Math.Round(cut.End, 6)));
        Assert.Null(_state.PendingSection);
    }

    [Fact]
    public void ADragRightToLeftDrawsTheSameSection()
    {
        _controller.Press(new Vector2(300, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(100, 50), EditorModifiers.None);
        _controller.Release(new Vector2(100, 50));
        Assert.False(_doc.Segments.Segments[_doc.Segments.IndexAt(2)].Enabled);
    }

    [Fact]
    public void ShiftDragPans()
    {
        _view.SetView(2, 5); // 0.005 s per dp
        _controller.Press(new Vector2(500, 50), EditorModifiers.Shift);
        _controller.Drag(new Vector2(600, 50), EditorModifiers.Shift);
        Assert.Equal(VideoGesture.Pan, _controller.Gesture);
        Assert.Equal(1.5, _view.Start, 6);
        _controller.Release(new Vector2(600, 50));
        Assert.False(_doc.Editor.History.CanUndo);
    }

    [Fact]
    public void PressingTheRulerScrubs()
    {
        _controller.Press(new Vector2(250, 10), EditorModifiers.None);
        _controller.Drag(new Vector2(300, 10), EditorModifiers.None);
        _controller.Release(new Vector2(300, 10));
        Assert.Equal([2.5, 3.0], _seeks);
        Assert.False(_doc.Editor.History.CanUndo);
    }

    [Fact]
    public void TheWheelZoomsAboutTheCursorAndShiftWheelPans()
    {
        _controller.Wheel(new Vector2(500, 50), new Vector2(0, 1), EditorModifiers.None);
        Assert.Equal(8, _view.Span, 6);
        Assert.Equal(1, _view.Start, 6);
        // Wheel up with Shift pans the view back in time, so the content slides with the wheel: 1 - 0.8
        _controller.Wheel(new Vector2(500, 50), new Vector2(0, 1), EditorModifiers.Shift);
        Assert.Equal(0.2, _view.Start, 6);
        _view.SetView(1, 8);
        _controller.Wheel(new Vector2(500, 50), new Vector2(1, 0), EditorModifiers.None);
        Assert.Equal(0.2, _view.Start, 6);
    }

    [Fact]
    public void HoverPrefersTheBoundaryThenTheSegment()
    {
        _controller.Hover(new Vector2(401, 50));
        Assert.Equal((1, -1), (_state.HoverBoundary, _state.HoverSegment));
        _controller.Hover(new Vector2(500, 50));
        Assert.Equal((-1, 1), (_state.HoverBoundary, _state.HoverSegment));
    }

    [Fact]
    public void DeleteDissolvesTheHoveredSegment()
    {
        _controller.Hover(new Vector2(500, 50));
        Assert.True(_controller.Key(Key.Delete, EditorModifiers.None));
        Assert.Single(_doc.Segments.Segments);
        _controller.Hover(new Vector2(500, 5));
        Assert.False(_controller.Key(Key.Backspace, EditorModifiers.None));
    }

    [Fact]
    public void AClickRefreshesTheHoverToTheSegmentUnderThePointer()
    {
        _controller.Hover(new Vector2(500, 50));
        _state.HoverSegment = 3; // stale on purpose: the press and release must leave it at the segment under the pointer
        Click(500, 50);
        Assert.Equal((-1, 1), (_state.HoverBoundary, _state.HoverSegment));
    }

    [Fact]
    public void DeleteRefreshesTheHoverWhenItMergesNeighbours()
    {
        _controller.Hover(new Vector2(500, 50));
        Assert.True(_controller.Key(Key.Delete, EditorModifiers.None));
        Assert.Single(_doc.Segments.Segments);
        Assert.Equal(0, _state.HoverSegment); // the merged segment is now under the pointer
    }

    [Fact]
    public void UndoRefreshesTheHoverWithoutAPointerMove()
    {
        _controller.Hover(new Vector2(500, 50));
        Assert.True(_controller.Key(Key.Delete, EditorModifiers.None));
        Assert.Equal(0, _state.HoverSegment);
        Assert.True(_doc.Editor.Undo());
        Assert.Equal(1, _state.HoverSegment);
    }

    [Fact]
    public void RightClickDuringAGestureDoesNothing()
    {
        var asked = false;
        _controller.ShowSegmentMenu = (_, _, _) => asked = true;
        _controller.Press(new Vector2(500, 50), EditorModifiers.None);
        _controller.RightClick(new Vector2(500, 50));
        Assert.False(asked);
    }

    [Fact]
    public void RightClickOnASegmentAsksForItsMenu()
    {
        (int Index, float X, float Y)? asked = null;
        _controller.ShowSegmentMenu = (i, x, y) => asked = (i, x, y);
        _controller.RightClick(new Vector2(500, 50));
        Assert.Equal((1, 500f, 50f), asked);
        asked = null;
        _controller.RightClick(new Vector2(500, 5));
        Assert.Null(asked);
    }

    [Fact]
    public void CancelDuringABoundaryDragRecordsOneStep()
    {
        _controller.Press(new Vector2(402, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(350, 50), EditorModifiers.None);
        _controller.CancelGesture();
        Assert.Equal(VideoGesture.None, _controller.Gesture);
        Assert.False(_doc.Editor.IsDragging);
        Assert.Equal(3.5, _doc.Segments.Segments[1].Start, 6);
        Assert.True(_doc.Editor.Undo());
        Assert.Equal(4, _doc.Segments.Segments[1].Start, 6);
    }

    [Fact]
    public void CancelWhileDrawingDropsTheSection()
    {
        _controller.Press(new Vector2(100, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(300, 50), EditorModifiers.None);
        _controller.CancelGesture();
        Assert.Null(_state.PendingSection);
        Assert.False(_doc.Editor.History.CanUndo);
    }
}
