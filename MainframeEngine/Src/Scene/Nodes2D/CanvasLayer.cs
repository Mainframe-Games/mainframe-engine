using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A separate canvas drawn above (or, with a negative <see cref="Layer"/>, below) the viewport's root canvas, with its
/// own transform and unaffected by the root canvas's camera (Godot's <c>CanvasLayer</c>; the HUD lives on one).
/// </summary>
[EditorIcon("stack-2", Family = EditorIconFamily.Space2D)]
public class CanvasLayer : Node
{
    private SceneViewport? _registeredIn;

    public CanvasLayer()
    {
        Canvas = new Canvas(null, this);
    }

    /// <summary>The canvas this layer's items draw on.</summary>
    public Canvas Canvas { get; }

    /// <summary>Draw order among canvases: the root canvas is layer 0; equal layers draw in tree order.</summary>
    [Export(Range = "-128,128,1")]
    public int Layer { get; set; } = 1;

    [Export]
    public bool Visible { get; set; } = true;

    [Export]
    public Vector2 Offset { get; set; }

    /// <summary>Rotation in radians (clockwise on screen).</summary>
    public float Rotation { get; set; }

    [Export]
    public float RotationDegrees
    {
        get => float.RadiansToDegrees(Rotation);
        set => Rotation = float.DegreesToRadians(value);
    }

    [Export]
    public Vector2 Scale { get; set; } = Vector2.One;

    /// <summary>The layer's own transform (offset, rotation, scale).</summary>
    public Transform2D Transform => Transform2D.FromTrs(Offset, Rotation, Scale);

    /// <summary>Applies the viewport's canvas transform (camera) too, for layers that move with the world.</summary>
    [Export]
    public bool FollowViewportEnabled { get; set; }

    /// <summary>The transform the canvas server draws this layer with.</summary>
    public Transform2D GetFinalTransform() =>
        FollowViewportEnabled && GetViewport() is { } viewport ? Transform * viewport.CanvasTransform : Transform;

    public void Show() => Visible = true;

    public void Hide() => Visible = false;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _registeredIn = GetViewport();
        _registeredIn?.AddCanvasLayer(this);
    }

    protected override void OnExitTree()
    {
        _registeredIn?.RemoveCanvasLayer(this);
        _registeredIn = null;
        base.OnExitTree();
    }
}
