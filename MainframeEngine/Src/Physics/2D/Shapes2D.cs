using System.Numerics;
using Box2D.NET;
using static Box2D.NET.B2Distances;
using static Box2D.NET.B2Geometries;
using static Box2D.NET.B2Hulls;

namespace MainframeEngine;

/// <summary>Box2D geometry of one shape, in metres, in its body's frame.</summary>
internal struct ShapeGeometry2D
{
    public B2ShapeType Type;
    public B2Circle Circle;
    public B2Capsule Capsule;
    public B2Polygon Polygon;
    public B2Segment Segment;

    /// <summary>A distance/cast proxy of this geometry placed at <paramref name="position"/>/<paramref name="rotation"/> (metres).</summary>
    public readonly B2ShapeProxy MakeProxy(B2Vec2 position, B2Rot rotation)
    {
        switch (Type)
        {
            case B2ShapeType.b2_circleShape:
                {
                    Span<B2Vec2> p = [Circle.center];
                    return b2MakeOffsetProxy(p, 1, Circle.radius, position, rotation);
                }
            case B2ShapeType.b2_capsuleShape:
                {
                    Span<B2Vec2> p = [Capsule.center1, Capsule.center2];
                    return b2MakeOffsetProxy(p, 2, Capsule.radius, position, rotation);
                }
            case B2ShapeType.b2_segmentShape:
                {
                    Span<B2Vec2> p = [Segment.point1, Segment.point2];
                    return b2MakeOffsetProxy(p, 2, 0, position, rotation);
                }
            default:
                {
                    Span<B2Vec2> p = stackalloc B2Vec2[Polygon.count];
                    for (var i = 0; i < Polygon.count; i++)
                        p[i] = Polygon.vertices[i];
                    return b2MakeOffsetProxy(p, Polygon.count, Polygon.radius, position, rotation);
                }
        }
    }
}

/// <summary>
/// Base of 2D collision shape resources (Godot's <c>Shape2D</c>), in pixels. A <see cref="CollisionShape2D"/> child
/// gives a body its shape; resources are shareable and saved with scenes. The physics server converts to Box2D's metres
/// with <see cref="PhysicsSettings2D.PixelsPerMeter"/>.
/// </summary>
public abstract class Shape2D : Resource
{
    /// <summary>Segment-based shapes: static and kinematic bodies only (no area, so no mass).</summary>
    public virtual bool IsConcave => false;

    /// <summary>
    /// Appends the Box2D geometries of this shape placed by <paramref name="transform"/> (shape → body frame, pixels,
    /// scale included), converted to metres by dividing by <paramref name="pixelsPerMeter"/>.
    /// </summary>
    internal abstract void CreateGeometry(List<ShapeGeometry2D> output, in Transform2D transform, float pixelsPerMeter);

    /// <summary>Outline of the shape under <paramref name="transform"/> (world, pixels) in the z = 0 plane.</summary>
    internal abstract void DrawDebug(DebugLines lines, in Transform2D transform, Vector4 color);

    protected void OnShapeChanged() => EmitChanged();

    private protected static B2Vec2 ToMeters(in Transform2D transform, Vector2 point, float pixelsPerMeter)
    {
        var p = transform.TransformPoint(point) / pixelsPerMeter;
        return new B2Vec2(p.X, p.Y);
    }

    /// <summary>Largest axis scale of <paramref name="transform"/> (radii of round shapes under non-uniform scale).</summary>
    private protected static float MaxScale(in Transform2D transform) => MathF.Max(transform.X.Length(), transform.Y.Length());

    private protected static bool AddPolygon(List<ShapeGeometry2D> output, ReadOnlySpan<B2Vec2> points)
    {
        var hull = b2ComputeHull(points, points.Length);
        if (hull.count < 3)
            return false;
        output.Add(new ShapeGeometry2D { Type = B2ShapeType.b2_polygonShape, Polygon = b2MakePolygon(hull, 0f) });
        return true;
    }

    private protected static float Positive(float value, string name)
    {
        if (!(value > 0) || !float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, value, "Must be positive and finite.");
        return value;
    }
}

/// <summary>An axis-aligned rectangle (Godot's <c>RectangleShape2D</c>); <see cref="Size"/> is the full extent in pixels.</summary>
public sealed class RectangleShape2D : Shape2D
{
    [Export]
    public Vector2 Size
    {
        get;
        set
        {
            Positive(value.X, nameof(Size));
            Positive(value.Y, nameof(Size));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = new(20, 20);

    internal override void CreateGeometry(List<ShapeGeometry2D> output, in Transform2D transform, float pixelsPerMeter)
    {
        var h = Size * 0.5f;
        Span<B2Vec2> points =
        [
            ToMeters(transform, new Vector2(-h.X, -h.Y), pixelsPerMeter),
            ToMeters(transform, new Vector2(h.X, -h.Y), pixelsPerMeter),
            ToMeters(transform, new Vector2(h.X, h.Y), pixelsPerMeter),
            ToMeters(transform, new Vector2(-h.X, h.Y), pixelsPerMeter),
        ];
        if (!AddPolygon(output, points))
            Log.Error($"[Physics2D] RectangleShape2D {Size} is too small for Box2D at this scale; ignored.");
    }

    internal override void DrawDebug(DebugLines lines, in Transform2D transform, Vector4 color)
    {
        var h = Size * 0.5f;
        Span<Vector2> points = [new(-h.X, -h.Y), new(h.X, -h.Y), new(h.X, h.Y), new(-h.X, h.Y)];
        lines.AddPolygon2D(transform, points, color);
    }
}

/// <summary>A circle (Godot's <c>CircleShape2D</c>), radius in pixels.</summary>
public sealed class CircleShape2D : Shape2D
{
    [Export]
    public float Radius
    {
        get;
        set
        {
            Positive(value, nameof(Radius));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = 10f;

    internal override void CreateGeometry(List<ShapeGeometry2D> output, in Transform2D transform, float pixelsPerMeter) =>
        output.Add(new ShapeGeometry2D
        {
            Type = B2ShapeType.b2_circleShape,
            Circle = new B2Circle(ToMeters(transform, Vector2.Zero, pixelsPerMeter), Radius * MaxScale(transform) / pixelsPerMeter),
        });

    internal override void DrawDebug(DebugLines lines, in Transform2D transform, Vector4 color) =>
        lines.AddCircle2D(transform, Vector2.Zero, Radius, color);
}

/// <summary>A capsule along local Y (Godot's <c>CapsuleShape2D</c>); <see cref="Height"/> includes both caps (pixels).</summary>
public sealed class CapsuleShape2D : Shape2D
{
    [Export]
    public float Radius
    {
        get;
        set
        {
            Positive(value, nameof(Radius));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = 10f;

    /// <summary>Total height, caps included (at least 2 × <see cref="Radius"/>; smaller values are clamped).</summary>
    [Export]
    public float Height
    {
        get;
        set
        {
            Positive(value, nameof(Height));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = 30f;

    internal override void CreateGeometry(List<ShapeGeometry2D> output, in Transform2D transform, float pixelsPerMeter)
    {
        var half = MathF.Max(Height * 0.5f - Radius, 0.01f); // Box2D needs distinct centres
        var radius = Radius * MathF.Abs(transform.X.Length()) / pixelsPerMeter;
        output.Add(new ShapeGeometry2D
        {
            Type = B2ShapeType.b2_capsuleShape,
            Capsule = new B2Capsule(ToMeters(transform, new Vector2(0, -half), pixelsPerMeter), ToMeters(transform, new Vector2(0, half), pixelsPerMeter), radius),
        });
    }

    internal override void DrawDebug(DebugLines lines, in Transform2D transform, Vector4 color) =>
        lines.AddCapsule2D(transform, Radius, MathF.Max(Height, 2 * Radius), color);
}

/// <summary>A convex polygon (Godot's <c>ConvexPolygonShape2D</c>): up to 8 points (Box2D's limit), hull computed from them.</summary>
public sealed class ConvexPolygonShape2D : Shape2D
{
    /// <summary>Box2D's maximum polygon vertex count.</summary>
    public const int MaxPoints = 8;

    [Export]
    public Vector2[] Points
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > MaxPoints)
                throw new ArgumentException($"Box2D polygons have at most {MaxPoints} points.", nameof(value));
            field = value;
            OnShapeChanged();
        }
    } = [];

    internal override void CreateGeometry(List<ShapeGeometry2D> output, in Transform2D transform, float pixelsPerMeter)
    {
        Span<B2Vec2> points = stackalloc B2Vec2[Points.Length];
        for (var i = 0; i < Points.Length; i++)
            points[i] = ToMeters(transform, Points[i], pixelsPerMeter);
        if (!AddPolygon(output, points))
            Log.Error("[Physics2D] ConvexPolygonShape2D needs 3+ non-collinear points of a usable size; ignored.");
    }

    internal override void DrawDebug(DebugLines lines, in Transform2D transform, Vector4 color) =>
        lines.AddPolygon2D(transform, Points, color);
}

/// <summary>A line segment from <see cref="A"/> to <see cref="B"/> (Godot's <c>SegmentShape2D</c>); static/kinematic bodies.</summary>
public sealed class SegmentShape2D : Shape2D
{
    [Export]
    public Vector2 A
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    }

    [Export]
    public Vector2 B
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = new(100, 0);

    public override bool IsConcave => true;

    internal override void CreateGeometry(List<ShapeGeometry2D> output, in Transform2D transform, float pixelsPerMeter) =>
        output.Add(new ShapeGeometry2D
        {
            Type = B2ShapeType.b2_segmentShape,
            Segment = new B2Segment(ToMeters(transform, A, pixelsPerMeter), ToMeters(transform, B, pixelsPerMeter)),
        });

    internal override void DrawDebug(DebugLines lines, in Transform2D transform, Vector4 color) =>
        lines.AddLine2D(transform, A, B, color);
}

/// <summary>
/// Independent segments (Godot's <c>ConcavePolygonShape2D</c>): every 2 entries of <see cref="Segments"/> form one —
/// level outlines and terrain. Static and kinematic bodies only.
/// </summary>
public sealed class ConcavePolygonShape2D : Shape2D
{
    [Export]
    public Vector2[] Segments
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length % 2 != 0)
                throw new ArgumentException("Segments must hold 2 points per segment.", nameof(value));
            field = value;
            OnShapeChanged();
        }
    } = [];

    public override bool IsConcave => true;

    internal override void CreateGeometry(List<ShapeGeometry2D> output, in Transform2D transform, float pixelsPerMeter)
    {
        for (var i = 0; i + 1 < Segments.Length; i += 2)
        {
            if (Segments[i] == Segments[i + 1])
                continue;
            output.Add(new ShapeGeometry2D
            {
                Type = B2ShapeType.b2_segmentShape,
                Segment = new B2Segment(ToMeters(transform, Segments[i], pixelsPerMeter), ToMeters(transform, Segments[i + 1], pixelsPerMeter)),
            });
        }
    }

    internal override void DrawDebug(DebugLines lines, in Transform2D transform, Vector4 color)
    {
        for (var i = 0; i + 1 < Segments.Length; i += 2)
            lines.AddLine2D(transform, Segments[i], Segments[i + 1], color);
    }
}
