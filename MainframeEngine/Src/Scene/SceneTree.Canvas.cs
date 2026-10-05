namespace MainframeEngine;

public sealed partial class SceneTree
{
    private readonly List<CanvasItem> _canvasRedraws = [];
    private CanvasItem[] _canvasRedrawSnapshot = [];

    /// <summary>Canvas items waiting for their end-of-frame draw.</summary>
    public int PendingCanvasRedraws => _canvasRedraws.Count;

    internal void QueueCanvasRedraw(CanvasItem item) => _canvasRedraws.Add(item);

    /// <summary>
    /// Runs every queued canvas item draw (Godot's deferred <c>_draw</c> at the end of the frame). The canvas server calls
    /// this after the tree's process step; items queued while drawing wait for the next flush.
    /// </summary>
    public void FlushCanvasRedraws()
    {
        var count = _canvasRedraws.Count;
        if (count == 0)
            return;
        if (_canvasRedrawSnapshot.Length < count)
            Array.Resize(ref _canvasRedrawSnapshot, Math.Max(count, _canvasRedrawSnapshot.Length * 2));
        _canvasRedraws.CopyTo(_canvasRedrawSnapshot);
        _canvasRedraws.Clear();
        for (var i = 0; i < count; i++)
        {
            var item = _canvasRedrawSnapshot[i];
            _canvasRedrawSnapshot[i] = null!;
            if (item.IsRedrawQueued && !item.IsFreed && item.Tree == this)
                item.RunDraw();
        }
    }
}
