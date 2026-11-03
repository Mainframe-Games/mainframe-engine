using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// An immediate-mode list of editor gizmo shapes in world space: lines with a width in pixels (anti-aliased, round caps),
/// solid shaded meshes (cones, boxes, discs) and textured icons from a <see cref="GizmoIconAtlas"/>. A view draws
/// <see cref="SceneViewport.Gizmos"/> depth-tested and <see cref="SceneViewport.OverlayGizmos"/> on top, each in
/// submission order (draw far shapes first), after its debug lines; then clears them. Grows on demand, then never
/// allocates.
/// </summary>
/// <remarks>
/// Lines become screen-space quads in the vertex shader (<c>Shaders/Gizmos/Gizmo</c>), so their width stays in pixels at
/// any distance. Solid meshes are shaded and back-face culled here on the CPU with the camera given to
/// <see cref="SetCamera"/>: a key light from the viewer's upper left, so a cone reads as a cone. They have no depth
/// buffer of their own, so only convex shapes (whose front faces never overlap) draw correctly. 2D views (a canvas)
/// draw the lines and triangles in the z = 0 plane; icons are 3D only. Colours are sRGB-authored with straight alpha.
/// </remarks>
public sealed class GizmoDrawList
{
    /// <summary>Vertices kept per frame; more shapes are dropped.</summary>
    public const int MaxVertices = 1 << 20;

    // Pixels: the anti-aliased edge drawn along a solid shape's silhouette.
    private const float SilhouetteWidth = 1f;

    private GizmoVertex[] _vertices = new GizmoVertex[1024];
    private int _count;
    private Vector3 _eye;
    private Vector3 _forward = -Vector3.UnitZ;
    private Vector3 _toLight = Vector3.Normalize(new Vector3(-0.4f, 0.7f, 1f));
    private bool _orthographic;

    /// <summary>The atlas <see cref="Icon"/> reads from (null: icons draw as solid quads).</summary>
    public GizmoIconAtlas? Atlas { get; set; }

    /// <summary>Vertices in the list (6 per line segment, 3 per triangle).</summary>
    public int VertexCount => _count;

    /// <summary>Vertices dropped since the last <see cref="Clear"/> because the list was full.</summary>
    public int DroppedVertices { get; private set; }

    internal ReadOnlySpan<GizmoVertex> Vertices => new(_vertices, 0, _count);

    /// <summary>Removes every shape (the render server does this after drawing).</summary>
    public void Clear()
    {
        _count = 0;
        DroppedVertices = 0;
    }

    /// <summary>
    /// The camera the solid shapes are shaded and culled for: its world position, forward and up, and whether it is
    /// orthographic (then every face is seen along <paramref name="forward"/>).
    /// </summary>
    public void SetCamera(Vector3 position, Vector3 forward, Vector3 up, bool orthographic)
    {
        _eye = position;
        _forward = Vector3.Normalize(forward);
        _orthographic = orthographic;
        var right = Vector3.Normalize(Vector3.Cross(_forward, up));
        var trueUp = Vector3.Cross(right, _forward);
        _toLight = Vector3.Normalize(-_forward * 1f + trueUp * 0.75f - right * 0.45f);
    }

    /// <summary>A segment <paramref name="widthPixels"/> wide (framebuffer pixels) with round caps.</summary>
    public void Line(Vector3 a, Vector3 b, Vector4 color, float widthPixels)
    {
        if (!Reserve(6))
            return;
        var white = WhiteUv;
        var w = MathF.Max(widthPixels, 0.5f);
        // Each end carries the other end and a side (the shader expands both ends in one frame for the segment).
        var a0 = new GizmoVertex(a, b, color, white, new Vector2(1f, w));
        var a1 = new GizmoVertex(a, b, color, white, new Vector2(-1f, w));
        var b0 = new GizmoVertex(b, a, color, white, new Vector2(1f, w));
        var b1 = new GizmoVertex(b, a, color, white, new Vector2(-1f, w));
        _vertices[_count++] = a0;
        _vertices[_count++] = a1;
        _vertices[_count++] = b1;
        _vertices[_count++] = a0;
        _vertices[_count++] = b1;
        _vertices[_count++] = b0;
    }

    /// <summary>Consecutive segments through <paramref name="points"/> (and back to the first when closed).</summary>
    public void Polyline(ReadOnlySpan<Vector3> points, Vector4 color, float widthPixels, bool closed = false)
    {
        for (var i = 0; i + 1 < points.Length; i++)
            Line(points[i], points[i + 1], color, widthPixels);
        if (closed && points.Length > 2)
            Line(points[^1], points[0], color, widthPixels);
    }

    /// <summary>A circle in the plane of the unit axes <paramref name="u"/>, <paramref name="v"/>.</summary>
    public void Circle(Vector3 center, Vector3 u, Vector3 v, float radius, Vector4 color, float widthPixels, int segments = 64)
    {
        var previous = center + u * radius;
        for (var i = 1; i <= segments; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.Tau / segments);
            var next = center + (u * cos + v * sin) * radius;
            Line(previous, next, color, widthPixels);
            previous = next;
        }
    }

    /// <summary>A triangle in its colour as given (no shading, both sides).</summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector4 color)
    {
        if (!Reserve(3))
            return;
        var white = WhiteUv;
        _vertices[_count++] = new GizmoVertex(a, a, color, white, default);
        _vertices[_count++] = new GizmoVertex(b, b, color, white, default);
        _vertices[_count++] = new GizmoVertex(c, c, color, white, default);
    }

    /// <summary>A flat filled disc (no shading) in the plane of the unit axes <paramref name="u"/>, <paramref name="v"/>.</summary>
    public void Disc(Vector3 center, Vector3 u, Vector3 v, float radius, Vector4 color, int segments = 32)
    {
        var previous = center + u * radius;
        for (var i = 1; i <= segments; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.Tau / segments);
            var next = center + (u * cos + v * sin) * radius;
            Triangle(center, previous, next, color);
            previous = next;
        }
    }

    /// <summary>
    /// A solid shaded cone: its tip, the unit <paramref name="axis"/> from the base towards the tip, height and base
    /// radius. Smooth-shaded sides and a flat base, faces turned away from the camera culled, and the silhouette edged
    /// with a thin anti-aliased line (the faces themselves are not anti-aliased).
    /// </summary>
    public void Cone(Vector3 tip, Vector3 axis, float height, float radius, Vector4 color, int segments = 20)
    {
        var (u, v) = Perpendiculars(axis);
        var baseCenter = tip - axis * height;
        var slope = radius / MathF.Max(height, 1e-6f);
        var baseVisible = FacesCamera(-axis, baseCenter);
        var baseColor = Shade(color, -axis);
        if (baseVisible)
        {
            var previous = baseCenter + u * radius;
            for (var i = 1; i <= segments; i++)
            {
                var (sin, cos) = MathF.SinCos(i * MathF.Tau / segments);
                var next = baseCenter + (u * cos + v * sin) * radius;
                Triangle(baseCenter, next, previous, baseColor);
                previous = next;
            }
        }

        // Pass 0: the visible side faces; pass 1: the silhouette over them (a generator where the facing flips, the rim
        // where only the side or only the base shows).
        var white = WhiteUv;
        for (var pass = 0; pass < 2; pass++)
        {
            var previousVisible = false;
            var previousColor = color;
            for (var i = 0; i <= segments; i++)
            {
                var (s0, c0) = MathF.SinCos(i * MathF.Tau / segments);
                var (s1, c1) = MathF.SinCos((i + 1) * MathF.Tau / segments);
                var (sm, cm) = MathF.SinCos((i + 0.5f) * MathF.Tau / segments);
                var r0 = u * c0 + v * s0;
                var r1 = u * c1 + v * s1;
                var faceNormal = Vector3.Normalize(u * cm + v * sm + axis * slope);
                var p0 = baseCenter + r0 * radius;
                var p1 = baseCenter + r1 * radius;
                var visible = FacesCamera(faceNormal, (p0 + p1 + tip) / 3f);
                var faceColor = Shade(color, faceNormal);
                if (pass == 1)
                {
                    if (i > 0 && visible != previousVisible)
                        Line(p0, tip, visible ? faceColor : previousColor, SilhouetteWidth);
                    if (i < segments && visible != baseVisible)
                        Line(p0, p1, visible ? faceColor : baseColor, SilhouetteWidth);
                }
                else if (i < segments && visible && Reserve(3))
                {
                    _vertices[_count++] = new GizmoVertex(p0, p0, Shade(color, Vector3.Normalize(r0 + axis * slope)), white, default);
                    _vertices[_count++] = new GizmoVertex(p1, p1, Shade(color, Vector3.Normalize(r1 + axis * slope)), white, default);
                    _vertices[_count++] = new GizmoVertex(tip, tip, faceColor, white, default);
                }

                previousVisible = visible;
                previousColor = faceColor;
            }
        }
    }

    /// <summary>
    /// A solid shaded box: centre and half-extent vectors along its three edges (any orientation); the silhouette edged
    /// with a thin anti-aliased line.
    /// </summary>
    public void Box(Vector3 center, Vector3 halfX, Vector3 halfY, Vector3 halfZ, Vector4 color)
    {
        ReadOnlySpan<Vector3> half = [halfX, halfY, halfZ];
        Span<bool> visible = stackalloc bool[6]; // face 2·axis + (0: +, 1: −)
        Span<Vector4> shaded = stackalloc Vector4[6];
        for (var f = 0; f < 6; f++)
        {
            var n = (f & 1) == 0 ? half[f >> 1] : -half[f >> 1];
            var normal = Vector3.Normalize(n);
            visible[f] = FacesCamera(normal, center + n);
            shaded[f] = Shade(color, normal);
            if (!visible[f])
                continue;
            var s = half[((f >> 1) + 1) % 3];
            var t = half[((f >> 1) + 2) % 3];
            var c = center + n;
            Triangle(c - s - t, c + s - t, c + s + t, shaded[f]);
            Triangle(c - s - t, c + s + t, c - s + t, shaded[f]);
        }

        // The 12 edges: along axis i, at the signs of the other two; a silhouette where exactly one of its faces shows.
        for (var i = 0; i < 3; i++)
        {
            var j = (i + 1) % 3;
            var k = (i + 2) % 3;
            for (var sj = 0; sj < 2; sj++)
                for (var sk = 0; sk < 2; sk++)
                {
                    var fj = 2 * j + sj;
                    var fk = 2 * k + sk;
                    if (visible[fj] == visible[fk])
                        continue;
                    var offset = center + (sj == 0 ? half[j] : -half[j]) + (sk == 0 ? half[k] : -half[k]);
                    Line(offset - half[i], offset + half[i], visible[fj] ? shaded[fj] : shaded[fk], SilhouetteWidth);
                }
        }
    }

    /// <summary>
    /// An icon from <see cref="Atlas"/>: a quad centred on <paramref name="center"/> spanning ±<paramref name="right"/>
    /// and ±<paramref name="up"/> (world vectors; a billboard passes the camera's), its texels times <paramref name="tint"/>.
    /// </summary>
    public void Icon(int icon, Vector3 center, Vector3 right, Vector3 up, Vector4 tint)
    {
        if (!Reserve(6))
            return;
        var (min, max) = Atlas is { } atlas ? atlas.UvRect(icon) : (Vector2.Zero, Vector2.Zero);
        var tl = new GizmoVertex(center - right + up, default, tint, min, default);
        var tr = new GizmoVertex(center + right + up, default, tint, new Vector2(max.X, min.Y), default);
        var br = new GizmoVertex(center + right - up, default, tint, max, default);
        var bl = new GizmoVertex(center - right - up, default, tint, new Vector2(min.X, max.Y), default);
        _vertices[_count++] = tl;
        _vertices[_count++] = bl;
        _vertices[_count++] = br;
        _vertices[_count++] = tl;
        _vertices[_count++] = br;
        _vertices[_count++] = tr;
    }

    private bool FacesCamera(Vector3 normal, Vector3 point) =>
        _orthographic ? Vector3.Dot(normal, _forward) < 0f : Vector3.Dot(normal, _eye - point) > 0f;

    // Half-Lambert-ish: never darker than 45 % of the colour, so a head in shadow still reads as its axis colour.
    private Vector4 Shade(Vector4 color, Vector3 normal)
    {
        var light = MathF.Max(0f, Vector3.Dot(normal, _toLight));
        var shade = 0.45f + 0.55f * light + 0.25f * MathF.Pow(light, 24f);
        return new Vector4(MathF.Min(color.X * shade, 1f), MathF.Min(color.Y * shade, 1f), MathF.Min(color.Z * shade, 1f), color.W);
    }

    private Vector2 WhiteUv => Atlas?.WhiteUv ?? Vector2.Zero;

    private bool Reserve(int more)
    {
        if (_count + more > MaxVertices)
        {
            DroppedVertices += more;
            return false;
        }

        if (_count + more > _vertices.Length)
            Array.Resize(ref _vertices, Math.Min(Math.Max(_vertices.Length * 2, _count + more), MaxVertices));
        return true;
    }

    /// <summary>Two unit vectors perpendicular to the unit <paramref name="axis"/> and to each other.</summary>
    public static (Vector3 U, Vector3 V) Perpendiculars(Vector3 axis)
    {
        var reference = MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, reference));
        var v = Vector3.Cross(axis, u);
        return (u, v);
    }
}

/// <summary>
/// A gizmo vertex: position, the other end of its line (lines only), sRGB colour with straight alpha, atlas UV and the
/// line parameters (x = side ±1, y = width in pixels; 0 for triangles). 56 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct GizmoVertex(Vector3 position, Vector3 other, Vector4 color, Vector2 uv, Vector2 line)
{
    public readonly Vector3 Position = position;
    public readonly Vector3 Other = other;
    public readonly Vector4 Color = color;
    public readonly Vector2 Uv = uv;
    public readonly Vector2 Line = line;
}
