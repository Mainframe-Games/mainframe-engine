using MainframeEngine.Video.Models;

namespace MainframeEngine.Video;

/// <summary>
/// One editing surface over a <see cref="SegmentList"/>, so the timeline, the transcript and every detector follow the
/// same rules:
/// <list type="bullet">
/// <item>Each edit is one undo step, and an edit that changes nothing leaves none.</item>
/// <item>A boundary drag is one step, from Begin to End.</item>
/// <item>A drawn section takes the opposite state of the segment it was anchored in.</item>
/// <item><see cref="IsDirty"/> compares with the last save, so undoing back to the saved partition is clean.</item>
/// </list>
/// Undo snapshots are arrays of immutable <see cref="Segment"/> records, so a deep history stays cheap.
/// </summary>
public sealed class SegmentEditor
{
    private Segment[] _saved;
    private Segment[]? _dragSnapshot;
    private bool _dirty;

    /// <summary>
    /// Edits <paramref name="segments"/>, with <paramref name="history"/> as its undo stack. The current partition counts
    /// as saved.
    /// </summary>
    public SegmentEditor(SegmentList segments, EditHistory<Segment[]> history)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(history);
        Segments = segments;
        History = history;
        _saved = [.. segments.Segments];
        segments.Changed += (_, _) => UpdateDirty();
    }

    public SegmentList Segments { get; }

    /// <summary>The undo stack; its <c>Changed</c> event drives Undo/Redo availability.</summary>
    public EditHistory<Segment[]> History { get; }

    /// <summary>True while the partition differs from the one last marked saved.</summary>
    public bool IsDirty => _dirty;

    /// <summary>True between <see cref="BeginBoundaryDrag"/> and <see cref="EndBoundaryDrag"/>.</summary>
    public bool IsDragging => _dragSnapshot is not null;

    /// <summary>Raised when <see cref="IsDirty"/> flips.</summary>
    public event EventHandler? DirtyChanged;

    /// <summary>Runs <paramref name="edit"/> as one undo step; false (and no step) when it changed nothing.</summary>
    public bool Edit(Action<SegmentList> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var before = Segments.Segments.ToArray();
        edit(Segments);
        if (before.SequenceEqual(Segments.Segments))
            return false;
        History.Push(before);
        return true;
    }

    /// <summary>Flips the segment at <paramref name="index"/>, which becomes manual; false when out of range.</summary>
    public bool Toggle(int index) => index >= 0 && index < Segments.Count && Edit(s => s.Toggle(index));

    /// <summary>Removes the region at <paramref name="index"/> and lets its neighbours grow over it; false when out of range.</summary>
    public bool Dissolve(int index) => index >= 0 && index < Segments.Count && Edit(s => s.Dissolve(index));

    /// <summary>
    /// Makes [<paramref name="start"/>, <paramref name="end"/>) one manual section with the opposite state of the segment at
    /// <paramref name="anchor"/>, where the drag started. Drawing over kept footage cuts it; drawing inside a cut restores
    /// that part. False when the anchor is off the timeline or nothing changed.
    /// </summary>
    public bool CreateSection(double start, double end, double anchor)
    {
        var anchorIndex = Segments.IndexAt(anchor);
        if (anchorIndex < 0)
            return false;
        var enabled = !Segments.Segments[anchorIndex].Enabled;
        return Edit(s => s.SetRange(start, end, enabled));
    }

    /// <summary>Starts a boundary drag: the partition as it is now is what one undo restores.</summary>
    public void BeginBoundaryDrag() => _dragSnapshot = [.. Segments.Segments];

    /// <summary>Moves the boundary live during a drag; returns the time applied after clamping.</summary>
    public double UpdateBoundaryDrag(int boundaryIndex, double time) => Segments.MoveBoundary(boundaryIndex, time);

    /// <summary>
    /// Ends a boundary drag at <paramref name="time"/>, which the caller has already snapped. Records the whole drag as one
    /// undo step, or none when the partition is back where it started.
    /// </summary>
    public double EndBoundaryDrag(int boundaryIndex, double time)
    {
        var applied = Segments.MoveBoundary(boundaryIndex, time);
        if (_dragSnapshot is { } before && !before.SequenceEqual(Segments.Segments))
            History.Push(before);
        _dragSnapshot = null;
        return applied;
    }

    /// <summary>Restores the partition from before the last edit; false when there is none.</summary>
    public bool Undo()
    {
        if (!History.CanUndo)
            return false;
        _dragSnapshot = null;
        Segments.Restore(History.Undo(Segments.Segments.ToArray()));
        return true;
    }

    /// <summary>Re-applies the last undone edit; false when there is none.</summary>
    public bool Redo()
    {
        if (!History.CanRedo)
            return false;
        _dragSnapshot = null;
        Segments.Restore(History.Redo(Segments.Segments.ToArray()));
        return true;
    }

    /// <summary>Marks the current partition as what is on disk now.</summary>
    public void MarkSaved()
    {
        _saved = [.. Segments.Segments];
        UpdateDirty();
    }

    private void UpdateDirty()
    {
        var dirty = !_saved.SequenceEqual(Segments.Segments);
        if (dirty == _dirty)
            return;
        _dirty = dirty;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }
}
