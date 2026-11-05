using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// Draws its parent <see cref="Path2D"/>'s baked curve as a road (paths themselves draw only in the editor), and
/// redraws when the curve changes.
/// </summary>
public sealed class PathTrack2D : Node2D
{
    private Path2D? _path;

    /// <summary>The road's width in pixels.</summary>
    [Export] public float Width { get; set; } = 18f;

    [Export] public Vector4 Color { get; set; } = new(0.2f, 0.24f, 0.33f, 1);

    /// <summary>The centre line's colour (transparent: none).</summary>
    [Export] public Vector4 LineColor { get; set; } = new(0.55f, 0.62f, 0.78f, 0.6f);

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _path = Parent as Path2D;
        if (_path is not null)
            _path.CurveChanged += QueueRedraw;
    }

    protected override void OnExitTree()
    {
        if (_path is not null)
            _path.CurveChanged -= QueueRedraw;
        _path = null;
        base.OnExitTree();
    }

    protected override void OnDraw()
    {
        if (_path?.Curve is not { } curve)
            return;
        var points = curve.GetBakedPoints();
        if (points.Length < 2)
            return;
        DrawPolyline(points, Color, Width, antialiased: true);
        if (LineColor.W > 0)
            DrawPolyline(points, LineColor, 2f, antialiased: true);
    }
}
