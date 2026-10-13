namespace MainframeEngine.Editor.Video;

/// <summary>
/// What the timeline controller tells the canvas to highlight: the hovered segment and boundary, the boundary being
/// dragged, the section being drawn and the playhead. Plain data, so drawing never reaches back into the controller.
/// </summary>
public sealed class VideoCanvasState
{
    /// <summary>Index of the segment under the pointer, or -1.</summary>
    public int HoverSegment { get; set; } = -1;

    /// <summary>Index of the boundary within grab distance (the start of that segment), or -1.</summary>
    public int HoverBoundary { get; set; } = -1;

    /// <summary>Index of the boundary being dragged, or -1.</summary>
    public int DragBoundary { get; set; } = -1;

    /// <summary>The section being dragged out, in seconds, before it is committed.</summary>
    public (double Start, double End)? PendingSection { get; set; }

    /// <summary>The playhead, in seconds.</summary>
    public double Playhead { get; set; }
}
