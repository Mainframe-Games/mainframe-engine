using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// An immediate-mode batch of coloured world-space line segments (physics shapes, gizmos). Producers add lines during
/// the frame; the <see cref="RenderServer"/> draws a viewport's batch after its visuals with a line-list pipeline and
/// clears it. Adding is allocation-free once the buffer has grown to the frame's size.
/// </summary>
/// <remarks>
/// 2D producers (the 2D physics debug draw) add their lines in the z = 0 plane, in pixels, which is where
/// <see cref="Camera2D"/> looks. A batch holds at most <see cref="MaxLines"/>; further lines are dropped (counted in
/// <see cref="DroppedLines"/>) so a batch nobody draws (minimised window) cannot grow without bound.
/// </remarks>
public sealed class DebugLines
{
    /// <summary>Lines kept per frame; more are dropped.</summary>
    public const int MaxLines = 1 << 19;

    private const int CircleSegments = 24;

    private DebugLineVertex[] _vertices = new DebugLineVertex[256];
    private int _count;

    /// <summary>Line segments in the batch.</summary>
    public int LineCount => _count / 2;

    /// <summary>Lines dropped since the last <see cref="Clear"/> because the batch was full.</summary>
    public int DroppedLines { get; private set; }

    internal ReadOnlySpan<DebugLineVertex> Vertices => new(_vertices, 0, _count);

    /// <summary>Removes every line (the render server does this after drawing).</summary>
    public void Clear()
    {
        _count = 0;
        DroppedLines = 0;
    }

    /// <summary>Adds a segment from <paramref name="a"/> to <paramref name="b"/> (world space).</summary>
    public void AddLine(Vector3 a, Vector3 b, Vector4 color)
    {
        if (_count >= MaxLines * 2)
        {
            DroppedLines++;
            return;
        }

        if (_count + 2 > _vertices.Length)
            Array.Resize(ref _vertices, Math.Min(_vertices.Length * 2, MaxLines * 2));
        _vertices[_count++] = new DebugLineVertex(a, color);
        _vertices[_count++] = new DebugLineVertex(b, color);
    }

    /// <summary>Adds the segment <paramref name="a"/>–<paramref name="b"/> transformed by <paramref name="transform"/>.</summary>
    public void AddLine(in Transform3D transform, Vector3 a, Vector3 b, Vector4 color) =>
        AddLine(transform.TransformPoint(a), transform.TransformPoint(b), color);

    /// <summary>A circle of <paramref name="radius"/> in the plane spanned by the unit axes <paramref name="u"/>, <paramref name="v"/>.</summary>
    public void AddCircle(in Transform3D transform, Vector3 center, Vector3 u, Vector3 v, float radius, Vector4 color)
    {
        var previous = transform.TransformPoint(center + u * radius);
        for (var i = 1; i <= CircleSegments; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.Tau / CircleSegments);
            var next = transform.TransformPoint(center + (u * cos + v * sin) * radius);
            AddLine(previous, next, color);
            previous = next;
        }
    }

    /// <summary>Half of a circle (from +u through +v to −u), for capsule caps.</summary>
    public void AddArc(in Transform3D transform, Vector3 center, Vector3 u, Vector3 v, float radius, Vector4 color)
    {
        var previous = transform.TransformPoint(center + u * radius);
        for (var i = 1; i <= CircleSegments / 2; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.PI / (CircleSegments / 2));
            var next = transform.TransformPoint(center + (u * cos + v * sin) * radius);
            AddLine(previous, next, color);
            previous = next;
        }
    }

    /// <summary>The 12 edges of a box centred on the origin with <paramref name="halfExtents"/>.</summary>
    public void AddBox(in Transform3D transform, Vector3 halfExtents, Vector4 color)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? -halfExtents.X : halfExtents.X,
                (i & 2) == 0 ? -halfExtents.Y : halfExtents.Y,
                (i & 4) == 0 ? -halfExtents.Z : halfExtents.Z);
            c[i] = transform.TransformPoint(corner);
        }

        for (var i = 0; i < 8; i++)
        {
            // Connect each corner to the neighbours that differ in exactly one higher bit.
            if ((i & 1) == 0) AddLine(c[i], c[i | 1], color);
            if ((i & 2) == 0) AddLine(c[i], c[i | 2], color);
            if ((i & 4) == 0) AddLine(c[i], c[i | 4], color);
        }
    }

    /// <summary>Three great circles of a sphere.</summary>
    public void AddSphere(in Transform3D transform, float radius, Vector4 color)
    {
        AddCircle(transform, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, radius, color);
        AddCircle(transform, Vector3.Zero, Vector3.UnitY, Vector3.UnitZ, radius, color);
        AddCircle(transform, Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, radius, color);
    }

    /// <summary>A capsule along local Y: <paramref name="height"/> is the total height including both caps.</summary>
    public void AddCapsule(in Transform3D transform, float radius, float height, Vector4 color)
    {
        var half = MathF.Max(0, height * 0.5f - radius);
        var top = new Vector3(0, half, 0);
        var bottom = -top;
        AddCircle(transform, top, Vector3.UnitX, Vector3.UnitZ, radius, color);
        AddCircle(transform, bottom, Vector3.UnitX, Vector3.UnitZ, radius, color);
        AddArc(transform, top, Vector3.UnitX, Vector3.UnitY, radius, color);
        AddArc(transform, top, Vector3.UnitZ, Vector3.UnitY, radius, color);
        AddArc(transform, bottom, -Vector3.UnitX, -Vector3.UnitY, radius, color);
        AddArc(transform, bottom, -Vector3.UnitZ, -Vector3.UnitY, radius, color);
        AddLine(transform, top + Vector3.UnitX * radius, bottom + Vector3.UnitX * radius, color);
        AddLine(transform, top - Vector3.UnitX * radius, bottom - Vector3.UnitX * radius, color);
        AddLine(transform, top + Vector3.UnitZ * radius, bottom + Vector3.UnitZ * radius, color);
        AddLine(transform, top - Vector3.UnitZ * radius, bottom - Vector3.UnitZ * radius, color);
    }

    /// <summary>A cylinder along local Y with total <paramref name="height"/>.</summary>
    public void AddCylinder(in Transform3D transform, float radius, float height, Vector4 color)
    {
        var top = new Vector3(0, height * 0.5f, 0);
        var bottom = -top;
        AddCircle(transform, top, Vector3.UnitX, Vector3.UnitZ, radius, color);
        AddCircle(transform, bottom, Vector3.UnitX, Vector3.UnitZ, radius, color);
        AddLine(transform, top + Vector3.UnitX * radius, bottom + Vector3.UnitX * radius, color);
        AddLine(transform, top - Vector3.UnitX * radius, bottom - Vector3.UnitX * radius, color);
        AddLine(transform, top + Vector3.UnitZ * radius, bottom + Vector3.UnitZ * radius, color);
        AddLine(transform, top - Vector3.UnitZ * radius, bottom - Vector3.UnitZ * radius, color);
    }

    /// <summary>The edges of each triangle in a triangle list (<paramref name="vertices"/>.Length is a multiple of 3).</summary>
    public void AddTriangles(in Transform3D transform, ReadOnlySpan<Vector3> vertices, Vector4 color)
    {
        for (var i = 0; i + 2 < vertices.Length; i += 3)
        {
            var a = transform.TransformPoint(vertices[i]);
            var b = transform.TransformPoint(vertices[i + 1]);
            var c = transform.TransformPoint(vertices[i + 2]);
            AddLine(a, b, color);
            AddLine(b, c, color);
            AddLine(c, a, color);
        }
    }

    // ---- 2D (z = 0 plane) -----------------------------------------------------------------------------------------

    /// <summary>A 2D segment in the z = 0 plane.</summary>
    public void AddLine2D(in Transform2D transform, Vector2 a, Vector2 b, Vector4 color) =>
        AddLine(new Vector3(transform.TransformPoint(a), 0), new Vector3(transform.TransformPoint(b), 0), color);

    /// <summary>A closed polygon outline in the z = 0 plane.</summary>
    public void AddPolygon2D(in Transform2D transform, ReadOnlySpan<Vector2> points, Vector4 color)
    {
        for (var i = 0; i < points.Length; i++)
            AddLine2D(transform, points[i], points[(i + 1) % points.Length], color);
    }

    /// <summary>A circle in the z = 0 plane, with a radius line showing rotation.</summary>
    public void AddCircle2D(in Transform2D transform, Vector2 center, float radius, Vector4 color)
    {
        var previous = center + new Vector2(radius, 0);
        for (var i = 1; i <= CircleSegments; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.Tau / CircleSegments);
            var next = center + new Vector2(cos, sin) * radius;
            AddLine2D(transform, previous, next, color);
            previous = next;
        }

        AddLine2D(transform, center, center + new Vector2(radius, 0), color);
    }

    /// <summary>A capsule along local Y in the z = 0 plane; <paramref name="height"/> includes both caps.</summary>
    public void AddCapsule2D(in Transform2D transform, float radius, float height, Vector4 color)
    {
        var half = MathF.Max(0, height * 0.5f - radius);
        var top = new Vector2(0, half);
        var bottom = -top;
        AddLine2D(transform, top + new Vector2(radius, 0), bottom + new Vector2(radius, 0), color);
        AddLine2D(transform, top - new Vector2(radius, 0), bottom - new Vector2(radius, 0), color);
        const int half360 = CircleSegments / 2;
        for (var cap = 0; cap < 2; cap++)
        {
            var center = cap == 0 ? top : bottom;
            var start = cap == 0 ? 0f : MathF.PI;
            var previous = center + new Vector2(MathF.Cos(start), MathF.Sin(start)) * radius;
            for (var i = 1; i <= half360; i++)
            {
                var (sin, cos) = MathF.SinCos(start + i * MathF.PI / half360);
                var next = center + new Vector2(cos, sin) * radius;
                AddLine2D(transform, previous, next, color);
                previous = next;
            }
        }
    }
}

/// <summary>Vertex layout shared with the scene grid shaders: position (vec3) + colour (vec4), 28 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct DebugLineVertex(Vector3 position, Vector4 color)
{
    public readonly Vector3 Position = position;
    public readonly Vector4 Color = color;
}
