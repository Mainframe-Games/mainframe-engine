namespace MainframeEngine;

/// <summary>
/// Draws its children into one offscreen image and puts that on the canvas as a single item (Godot's
/// <c>CanvasGroup</c>), so a fade does not show where children overlap. As in Godot, fade the group with
/// <see cref="CanvasItem.SelfModulate"/>: <see cref="CanvasItem.Modulate"/> also reaches the children before they are
/// grouped (overlaps then show), and the composite applies it again. The image covers the children's drawn bounds grown
/// by <see cref="FitMargin"/>. Shares the clip-children group path (ADR 0119): a CanvasGroup inside another group, or
/// inside an item that clips its children, draws its children ungrouped. The group itself draws nothing else, and
/// <see cref="UseMipmaps"/> is not supported yet.
/// </summary>
[EditorIcon("stack-2", Family = EditorIconFamily.Space2D)]
public class CanvasGroup : Node2D
{
    /// <summary>Pixels added around the children's bounds for the composited image (Godot's default 10).</summary>
    [Export(Range = "0,1024,1")]
    public float FitMargin
    {
        get;
        set
        {
            field = Math.Max(0f, value);
            QueueRedraw();
        }
    } = 10f;

    /// <summary>Godot's margin cleared around the group in its back buffer; kept for scenes (the whole target is cleared here).</summary>
    [Export(Range = "0,1024,1")]
    public float ClearMargin
    {
        get;
        set => field = Math.Max(0f, value);
    } = 10f;

    /// <summary>Godot's mipmapped group image for blur shaders. Not supported yet: the image has no mipmaps.</summary>
    [Export]
    public bool UseMipmaps { get; set; }

    /// <summary>The composited rect this frame (target pixels): the children's drawn bounds, set by the canvas server.</summary>
    internal Rect2 GroupRect { get; set; }
}
