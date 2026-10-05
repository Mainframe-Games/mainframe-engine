using System.Numerics;
using MainframeEngine;

namespace Demo;

public enum DemoShapeKind { Rect, Circle, Polygon }

/// <summary>A filled shape (optional vertical gradient and outline) drawn with the canvas draw API.</summary>
public sealed class DemoShape2D : Node2D
{
    private DemoShapeKind _kind;
    private Vector2 _size = new(64, 64);
    private float _radius = 32f;
    private Vector2[] _points = [];
    private Vector2[] _closedPoints = [];
    private Vector4 _color = Vector4.One;
    private Vector4 _colorBottom;
    private Vector4 _outline;
    private float _outlineWidth;
    private readonly Vector4[] _quadColors = new Vector4[4];
    private readonly Vector2[] _quad = new Vector2[4];

    [Export] public DemoShapeKind Kind { get => _kind; set { _kind = value; QueueRedraw(); } }

    [Export] public Vector2 Size { get => _size; set { _size = value; QueueRedraw(); } }

    [Export] public float Radius { get => _radius; set { _radius = value; QueueRedraw(); } }

    [Export] public Vector2[] Points { get => _points; set { _points = value ?? []; _closedPoints = CloseLoop(_points); QueueRedraw(); } }

    [Export] public Vector4 Color { get => _color; set { _color = value; QueueRedraw(); } }

    /// <summary>
    /// Bottom edge colour of a rectangle (Y is down). <see cref="Vector4.Zero"/> (the default) means no gradient: the
    /// rectangle is a flat fill of <see cref="Color"/>. Any other value draws a vertical gradient from <see cref="Color"/>.
    /// </summary>
    [Export] public Vector4 ColorBottom { get => _colorBottom; set { _colorBottom = value; QueueRedraw(); } }

    [Export] public Vector4 Outline { get => _outline; set { _outline = value; QueueRedraw(); } }

    [Export] public float OutlineWidth { get => _outlineWidth; set { _outlineWidth = value; QueueRedraw(); } }

    protected override void OnDraw()
    {
        switch (_kind)
        {
            case DemoShapeKind.Rect:
                var half = _size * 0.5f;
                _quad[0] = -half;
                _quad[1] = new Vector2(half.X, -half.Y);
                _quad[2] = half;
                _quad[3] = new Vector2(-half.X, half.Y);
                RectVertexColors(_color, _colorBottom, _quadColors);
                DrawPolygon(_quad, _quadColors);
                if (_outlineWidth > 0f)
                    DrawRect(new Rect2(-half, _size), _outline, filled: false, width: _outlineWidth, antialiased: true);
                break;
            case DemoShapeKind.Circle:
                DrawCircle(Vector2.Zero, _radius, _color, filled: true, width: -1f, antialiased: true);
                if (_outlineWidth > 0f)
                    DrawCircle(Vector2.Zero, _radius, _outline, filled: false, width: _outlineWidth, antialiased: true);
                break;
            case DemoShapeKind.Polygon when _points.Length >= 3:
                DrawColoredPolygon(_points, _color);
                if (_outlineWidth > 0f)
                    DrawPolyline(_closedPoints, _outline, _outlineWidth, antialiased: true);
                break;
        }
    }

    /// <summary>The four rectangle vertex colours (top-left, top-right, bottom-right, bottom-left): flat when <paramref name="bottom"/> is zero.</summary>
    public static void RectVertexColors(Vector4 top, Vector4 bottom, Span<Vector4> colors)
    {
        var low = bottom == Vector4.Zero ? top : bottom;
        colors[0] = colors[1] = top;
        colors[2] = colors[3] = low;
    }

    // The outline polyline over the points plus the first point again, so the last edge is drawn without allocating per draw.
    private static Vector2[] CloseLoop(Vector2[] points)
    {
        if (points.Length < 3)
            return [];
        var closed = new Vector2[points.Length + 1];
        points.CopyTo(closed, 0);
        closed[^1] = points[0];
        return closed;
    }
}
