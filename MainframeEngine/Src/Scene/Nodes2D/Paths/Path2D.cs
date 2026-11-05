using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// A <see cref="Curve2D"/> in the scene (Godot's <c>Path2D</c>, G13): its <see cref="PathFollow2D"/> children place
/// themselves (and their children) along it. The curve is in this node's local space, in pixels. Paths draw only in the
/// editor.
/// </summary>
[EditorIcon("route-2")]
public class Path2D : Node2D
{
    private readonly ResourceSubscription<Curve2D> _curve;

    public Path2D()
    {
        _curve = new ResourceSubscription<Curve2D>(RaiseCurveChanged);
    }

    /// <summary>The curve, in local space. Replacing it, or any change to it, raises <see cref="CurveChanged"/>.</summary>
    [Export]
    public Curve2D? Curve
    {
        get => _curve.Value;
        set
        {
            if (_curve.Set(value))
                RaiseCurveChanged();
        }
    }

    /// <summary>The colour the editor draws the path in (sRGB).</summary>
    [Export]
    public DrawingColor DebugColor { get; set; } = DrawingColor.FromArgb(255, 97, 166, 250);

    /// <summary>The curve was replaced or raised <see cref="Resource.Changed"/> (only while in the tree for the latter).</summary>
    [Signal] public event Action? CurveChanged;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _curve.Activate();
    }

    protected override void OnExitTree()
    {
        _curve.Deactivate();
        base.OnExitTree();
    }

    private void RaiseCurveChanged()
    {
        CurveChanged?.Invoke();
        for (var i = 0; i < ChildCount; i++)
            if (GetChild(i) is PathFollow2D follow)
                follow.UpdatePlacement();
    }
}
