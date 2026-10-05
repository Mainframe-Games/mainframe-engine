using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>A screen-gizmo vertex: framebuffer-pixel position (top-left origin) and straight-alpha sRGB colour.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ScreenGizmoVertex(Vector2 position, Vector4 color)
{
    public readonly Vector2 Position = position;
    public readonly Vector4 Color = color;
}

/// <summary>
/// Immediate-mode screen-space shapes (pixels, +Y down) tessellated into triangles on the CPU, drawn after the tonemap
/// by <see cref="ScreenGizmosRenderer"/>. Lines get a 1 px feathered edge for anti-aliasing. Grows on demand, then
/// never allocates.
/// </summary>
public sealed class ScreenGizmoBatch
{
    public const float Feather = 1f;

    private ScreenGizmoVertex[] _vertices = new ScreenGizmoVertex[4096];
    private int _count;

    public int VertexCount => _count;

    internal ReadOnlySpan<ScreenGizmoVertex> Vertices => _vertices.AsSpan(0, _count);

    public void Clear() => _count = 0;

    public void Line(Vector2 a, Vector2 b, Vector4 color, float thickness = 1.5f)
    {
        var delta = b - a;
        var length = delta.Length();
        if (length < 1e-4f)
            return;
        var normal = new Vector2(-delta.Y, delta.X) / length;
        var half = normal * (thickness * 0.5f);
        var outer = normal * (thickness * 0.5f + Feather);
        var clear = color with { W = 0f };
        Quad(a + half, b + half, b - half, a - half, color, color, color, color);
        Quad(a + outer, b + outer, b + half, a + half, clear, clear, color, color);
        Quad(a - half, b - half, b - outer, a - outer, color, color, clear, clear);
    }

    public void Polyline(ReadOnlySpan<Vector2> points, Vector4 color, float thickness = 1.5f, bool closed = false)
    {
        for (var i = 0; i + 1 < points.Length; i++)
            Line(points[i], points[i + 1], color, thickness);
        if (closed && points.Length > 2)
            Line(points[^1], points[0], color, thickness);
    }

    public void Circle(Vector2 center, float radius, Vector4 color, float thickness = 1.5f, int segments = 32)
    {
        var previous = center + new Vector2(radius, 0);
        for (var i = 1; i <= segments; i++)
        {
            var angle = i * MathF.Tau / segments;
            var next = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            Line(previous, next, color, thickness);
            previous = next;
        }
    }

    public void FilledCircle(Vector2 center, float radius, Vector4 color, int segments = 24)
    {
        var previous = center + new Vector2(radius, 0);
        for (var i = 1; i <= segments; i++)
        {
            var angle = i * MathF.Tau / segments;
            var next = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            Triangle(center, previous, next, color);
            previous = next;
        }
    }

    public void FilledRect(Vector2 min, Vector2 max, Vector4 color) =>
        Quad(min, new Vector2(max.X, min.Y), max, new Vector2(min.X, max.Y), color, color, color, color);

    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Vector4 color)
    {
        Ensure(3);
        _vertices[_count++] = new ScreenGizmoVertex(a, color);
        _vertices[_count++] = new ScreenGizmoVertex(b, color);
        _vertices[_count++] = new ScreenGizmoVertex(c, color);
    }

    public void Arrow(Vector2 from, Vector2 to, Vector4 color, float thickness = 2f, float head = 10f)
    {
        Line(from, to, color, thickness);
        var delta = to - from;
        var length = delta.Length();
        if (length < 1e-4f)
            return;
        var dir = delta / length;
        var perp = new Vector2(-dir.Y, dir.X);
        Line(to, to - dir * head + perp * (head * 0.5f), color, thickness);
        Line(to, to - dir * head - perp * (head * 0.5f), color, thickness);
    }

    /// <summary>Stroke glyphs for axis labels (no font): 'X', 'Y', 'Z' in a <paramref name="size"/>-pixel box.</summary>
    public void Glyph(char c, Vector2 topLeft, float size, Vector4 color, float thickness = 1.5f)
    {
        var tl = topLeft;
        var tr = topLeft + new Vector2(size * 0.7f, 0);
        var bl = topLeft + new Vector2(0, size);
        var br = topLeft + new Vector2(size * 0.7f, size);
        var mid = topLeft + new Vector2(size * 0.35f, size * 0.5f);
        switch (char.ToUpperInvariant(c))
        {
            case 'X':
                Line(tl, br, color, thickness);
                Line(tr, bl, color, thickness);
                break;
            case 'Y':
                Line(tl, mid, color, thickness);
                Line(tr, mid, color, thickness);
                Line(mid, topLeft + new Vector2(size * 0.35f, size), color, thickness);
                break;
            case 'Z':
                Line(tl, tr, color, thickness);
                Line(tr, bl, color, thickness);
                Line(bl, br, color, thickness);
                break;
        }
    }

    private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 ca, Vector4 cb, Vector4 cc, Vector4 cd)
    {
        Ensure(6);
        _vertices[_count++] = new ScreenGizmoVertex(a, ca);
        _vertices[_count++] = new ScreenGizmoVertex(b, cb);
        _vertices[_count++] = new ScreenGizmoVertex(c, cc);
        _vertices[_count++] = new ScreenGizmoVertex(a, ca);
        _vertices[_count++] = new ScreenGizmoVertex(c, cc);
        _vertices[_count++] = new ScreenGizmoVertex(d, cd);
    }

    private void Ensure(int more)
    {
        if (_count + more > _vertices.Length)
            Array.Resize(ref _vertices, Math.Max(_vertices.Length * 2, _count + more));
    }
}
