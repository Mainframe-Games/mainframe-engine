using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class SegmentEditorTests
{
    private static SegmentEditor ThreeSegments()
    {
        // kept [0,4) · cut [4,6) · kept [6,10)
        var list = new SegmentList(10.0,
        [
            Segment.Create(0, 4, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4, 6, enabled: false, SegmentOrigin.Auto, "silence 2.00s"),
            Segment.Create(6, 10, enabled: true, SegmentOrigin.Auto),
        ]);
        return new SegmentEditor(list, new EditHistory<Segment[]>(100));
    }

    [Fact]
    public void AnEditThatChangesNothingLeavesNoUndoStep()
    {
        var editor = ThreeSegments();
        Assert.False(editor.Edit(_ => { }));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void AnEditIsOneUndoStepAndUndoRestoresThePartition()
    {
        var editor = ThreeSegments();
        var before = editor.Segments.Segments.ToArray();

        Assert.True(editor.Toggle(1));
        Assert.True(editor.Segments.Segments[1].Enabled);

        Assert.True(editor.Undo());
        Assert.Equal(before, editor.Segments.Segments);
        Assert.False(editor.History.CanUndo);
        Assert.True(editor.History.CanRedo);
    }

    [Fact]
    public void RedoReappliesAnUndoneEdit()
    {
        var editor = ThreeSegments();
        editor.Toggle(0);
        var after = editor.Segments.Segments.ToArray();
        editor.Undo();
        Assert.True(editor.Redo());
        Assert.Equal(after, editor.Segments.Segments);
    }

    [Fact]
    public void UndoAndRedoWithNothingToDoReturnFalse()
    {
        var editor = ThreeSegments();
        Assert.False(editor.Undo());
        Assert.False(editor.Redo());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ToggleAndDissolveOutsideTheListDoNothing(int index)
    {
        var editor = ThreeSegments();
        Assert.False(editor.Toggle(index));
        Assert.False(editor.Dissolve(index));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void DissolveIsOneUndoStep()
    {
        var editor = ThreeSegments();
        Assert.True(editor.Dissolve(1));
        Assert.Equal(1, editor.Segments.Count); // both neighbours are kept, so they merge over the removed cut
        editor.Undo();
        Assert.Equal(3, editor.Segments.Count);
    }

    [Fact]
    public void ASectionDrawnOverKeptFootageCutsIt()
    {
        var editor = ThreeSegments();
        Assert.True(editor.CreateSection(1, 2, anchor: 1));
        var cut = editor.Segments.Segments[editor.Segments.IndexAt(1.5)];
        Assert.False(cut.Enabled);
        Assert.Equal(SegmentOrigin.Manual, cut.Origin);
    }

    [Fact]
    public void ASectionDrawnInsideACutRestoresThatPart()
    {
        var editor = ThreeSegments();
        Assert.True(editor.CreateSection(4.5, 5.5, anchor: 4.5));
        Assert.True(editor.Segments.Segments[editor.Segments.IndexAt(5)].Enabled);
    }

    [Fact]
    public void ASectionAnchoredOffTheTimelineDoesNothing()
    {
        var editor = ThreeSegments();
        Assert.False(editor.CreateSection(1, 2, anchor: 11));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void ABoundaryDragIsOneUndoStep()
    {
        var editor = ThreeSegments();
        var before = editor.Segments.Segments.ToArray();

        editor.BeginBoundaryDrag();
        Assert.True(editor.IsDragging);
        editor.UpdateBoundaryDrag(1, 3.5);
        editor.UpdateBoundaryDrag(1, 3.0);
        Assert.Equal(3.2, editor.EndBoundaryDrag(1, 3.2), 9);

        Assert.False(editor.IsDragging);
        editor.Undo();
        Assert.Equal(before, editor.Segments.Segments); // the whole drag is undone at once
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void APressAndReleaseOnABoundaryLeavesNoStep()
    {
        // A move and back would leave the cut marked manual (MoveBoundary marks disabled neighbours manual), which is a
        // real change; a press and release without moving is not.
        var editor = ThreeSegments();
        editor.BeginBoundaryDrag();
        editor.EndBoundaryDrag(1, 4.0);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void UndoDuringADragDropsTheDragSnapshot()
    {
        var editor = ThreeSegments();
        editor.Toggle(0);
        editor.BeginBoundaryDrag();
        editor.Undo();
        Assert.False(editor.IsDragging);
        Assert.True(editor.History.CanRedo);
    }

    [Fact]
    public void ANewEditorIsClean() => Assert.False(ThreeSegments().IsDirty);

    [Fact]
    public void EditsMakeItDirtyAndMarkSavedMakesItClean()
    {
        var editor = ThreeSegments();
        var flips = 0;
        editor.DirtyChanged += (_, _) => flips++;
        editor.Toggle(1);
        Assert.True(editor.IsDirty);
        editor.MarkSaved();
        Assert.False(editor.IsDirty);
        Assert.Equal(2, flips);
    }

    [Fact]
    public void UndoingBackToTheSavedStateIsClean()
    {
        var editor = ThreeSegments();
        editor.Toggle(1);
        editor.Toggle(0);
        editor.Undo();
        Assert.True(editor.IsDirty);
        editor.Undo();
        Assert.False(editor.IsDirty); // the partition equals the saved one again
    }

    [Fact]
    public void DirtyChangedFiresOnlyWhenTheFlagFlips()
    {
        var editor = ThreeSegments();
        var flips = 0;
        editor.DirtyChanged += (_, _) => flips++;
        editor.Toggle(1);
        editor.Toggle(0);
        editor.Toggle(2);
        Assert.Equal(1, flips);
    }
}
