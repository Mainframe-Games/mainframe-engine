using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Tints its whole canvas (Godot's <c>CanvasModulate</c>): every item on the canvas is multiplied by <see cref="Color"/>
/// after its own shading, before 2D lights. With several on one canvas the last visible one wins.
/// </summary>
[EditorIcon("contrast", Family = EditorIconFamily.Space2D)]
public class CanvasModulate : Node2D
{
    private Canvas? _canvas;

    [Export]
    public Vector4 Color { get; set; } = Vector4.One;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _canvas = GetCanvas();
        _canvas?.AddModulate(this);
    }

    protected override void OnExitTree()
    {
        _canvas?.RemoveModulate(this);
        _canvas = null;
        base.OnExitTree();
    }
}
