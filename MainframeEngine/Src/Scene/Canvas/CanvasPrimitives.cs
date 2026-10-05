using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The canvas draw primitives, tessellated exactly as Godot 4.7's <c>RendererCanvasCull::canvas_item_add_*</c> and
/// <c>CanvasItem::draw_*</c> do (same segment counts, strip layouts, feathering and triangulation), into a
/// <see cref="CanvasDrawList"/>. Thin lines (width &lt; 0) are line primitives, one pixel wide whatever the scale.
/// </summary>
public static class CanvasPrimitives
{
    /// <summary>Godot's <c>FEATHER_SIZE</c>: the antialiasing fringe width in pixels.</summary>
    public const float FeatherSize = 1.25f;

    /// <summary>Godot's ellipse/circle tessellation (<c>ellipse_segments</c>).</summary>
    public const int EllipseSegments = 64;

    private const float CmpEpsilon = 0.00001f;

    private static readonly Vector4 White = Vector4.One;

    // ── Rects and textures ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Godot's <c>CanvasItem::draw_rect</c>.</summary>
    public static void Rect(CanvasDrawList list, Rect2 rect, Vector4 color, bool filled, float width, bool antialiased)
    {
        rect = rect.Abs();
        if (filled)
        {
            AddRect(list, rect, color, antialiased);
        }
        else if (width >= rect.Size.X || width >= rect.Size.Y)
        {
            AddRect(list, rect.Grow(0.5f * width), color, antialiased);
        }
        else
        {
            Span<Vector2> points =
            [
                rect.Position,
                rect.Position + new Vector2(rect.Size.X, 0),
                rect.Position + rect.Size,
                rect.Position + new Vector2(0, rect.Size.Y),
                rect.Position,
            ];
            Polyline(list, points, [color], width, antialiased);
        }
    }

    /// <summary>Godot's <c>canvas_item_add_rect</c>: an untextured rect command (+ feathers when antialiased).</summary>
    public static void AddRect(CanvasDrawList list, Rect2 rect, Vector4 color, bool antialiased)
    {
        var adjusted = antialiased ? rect.Grow(-FeatherSize * 0.25f) : rect;
        AddRectCommand(list, null, adjusted, new Rect2(0, 0, 1, 1), color, flipH: false, flipV: false, transpose: false, tile: false);
        if (!antialiased)
            return;

        var borderSize = FeatherSize;
        var size = MathF.Min(adjusted.Size.X, adjusted.Size.Y);
        if (size >= 0f && size < 1f)
            borderSize *= size;
        var down = new Vector2(0f, adjusted.Size.Y);
        var right = new Vector2(adjusted.Size.X, 0f);
        var beginLeft = adjusted.Position;
        var beginRight = adjusted.Position + down;
        var endLeft = adjusted.Position + right;
        var endRight = adjusted.Position + adjusted.Size;
        var dir = new Vector2(0f, -1f);
        var dir2 = new Vector2(-1f, 0f);
        Feathers(list, beginLeft, beginRight, endLeft, endRight, dir * borderSize, dir2 * borderSize, color);
    }

    /// <summary>Godot's <c>Texture2D::draw_rect</c> → <c>canvas_item_add_texture_rect</c>.</summary>
    public static void TextureRect(CanvasDrawList list, Texture2D texture, Rect2 rect, Vector4 modulate, bool tile, bool transpose = false)
    {
        var flipH = rect.Size.X < 0;
        var flipV = rect.Size.Y < 0;
        if (flipH)
            rect.Size.X = -rect.Size.X;
        if (flipV)
            rect.Size.Y = -rect.Size.Y;
        if (transpose)
            rect.Size = new Vector2(rect.Size.Y, rect.Size.X);
        var source = tile
            ? new Rect2(0, 0, MathF.Abs(rect.Size.X) / Math.Max(1, texture.Width), MathF.Abs(rect.Size.Y) / Math.Max(1, texture.Height))
            : new Rect2(0, 0, 1, 1);
        AddRectCommand(list, texture, rect, source, modulate, flipH, flipV, transpose, tile);
    }

    /// <summary>Godot's <c>Texture2D::draw_rect_region</c> → <c>canvas_item_add_texture_rect_region</c> (source in texture pixels).</summary>
    public static void TextureRectRegion(CanvasDrawList list, Texture2D texture, Rect2 rect, Rect2 sourceRect, Vector4 modulate, bool transpose)
    {
        var flipH = false;
        var flipV = false;
        if (rect.Size.X < 0)
        {
            flipH = true;
            rect.Size.X = -rect.Size.X;
        }

        if (sourceRect.Size.X < 0)
        {
            flipH = !flipH;
            sourceRect.Size.X = -sourceRect.Size.X;
        }

        if (rect.Size.Y < 0)
        {
            flipV = true;
            rect.Size.Y = -rect.Size.Y;
        }

        if (sourceRect.Size.Y < 0)
        {
            flipV = !flipV;
            sourceRect.Size.Y = -sourceRect.Size.Y;
        }

        if (transpose)
            rect.Size = new Vector2(rect.Size.Y, rect.Size.X);
        var texel = new Vector2(1f / Math.Max(1, texture.Width), 1f / Math.Max(1, texture.Height));
        var source = new Rect2(sourceRect.Position * texel, sourceRect.Size * texel);
        AddRectCommand(list, texture, rect, source, modulate, flipH, flipV, transpose, tile: false);
    }

    // The renderer's TYPE_RECT: corners (0,0) (0,1) (1,1) (1,0) of dst, triangles 0-1-2 and 0-2-3; a flip mirrors the
    // positions (Godot negates the source size and the vertex shader mirrors the quad), a transpose swaps the UV axes.
    private static void AddRectCommand(CanvasDrawList list, Texture2D? texture, Rect2 dst, Rect2 src, Vector4 color, bool flipH, bool flipV, bool transpose, bool tile)
    {
        if (dst.Size.X < 0)
        {
            dst.Position.X += dst.Size.X;
            dst.Size.X = -dst.Size.X;
        }

        if (dst.Size.Y < 0)
        {
            dst.Position.Y += dst.Size.Y;
            dst.Size.Y = -dst.Size.Y;
        }

        Span<Vector2> bases = [new(0, 0), new(0, 1), new(1, 1), new(1, 0)];
        Span<CanvasVertex> v = stackalloc CanvasVertex[4];
        for (var i = 0; i < 4; i++)
        {
            var b = bases[i];
            var uvBase = transpose ? new Vector2(b.Y, b.X) : b;
            var uv = src.Position + src.Size * uvBase;
            var corner = new Vector2(flipH ? 1f - b.X : b.X, flipV ? 1f - b.Y : b.Y);
            v[i] = new CanvasVertex(dst.Position + dst.Size * corner, uv, color);
        }

        var first = list.AddVertices(v);
        list.AddIndices(texture, CanvasPrimitive.Triangles, first, [0, 1, 2, 0, 2, 3], tile);
    }

    // ── Lines ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Godot's <c>canvas_item_get_compensated_antialiasing_width</c>.</summary>
    public static float CompensatedAntialiasingWidth(float width)
    {
        if (width > 0f)
        {
            if (width <= FeatherSize * 2f + CmpEpsilon)
                return width * 0.5f;
            if (width <= FeatherSize * 4f + CmpEpsilon)
                return Remap(width, FeatherSize * 2f, FeatherSize * 4f, width * 0.5f, width - FeatherSize * 0.5f);
            return width - FeatherSize * 0.5f;
        }

        return width;
    }

    /// <summary>Godot's <c>canvas_item_add_line</c>.</summary>
    public static void Line(CanvasDrawList list, Vector2 from, Vector2 to, Vector4 color, float width, bool antialiased)
    {
        var diff = from - to;
        var dir = Normalized(Orthogonal(diff));
        if (antialiased)
            width = CompensatedAntialiasingWidth(width);
        var t = dir * width * 0.5f;

        Vector2 beginLeft, beginRight, endLeft, endRight;
        if (width >= 0f)
        {
            beginLeft = from + t;
            beginRight = from - t;
            endLeft = to + t;
            endRight = to - t;
            Primitive(list, [beginLeft, beginRight, endRight, endLeft], [color, color, color, color]);
        }
        else
        {
            beginLeft = beginRight = from;
            endLeft = endRight = to;
            Span<CanvasVertex> v = [new(from, Vector2.Zero, color), new(to, Vector2.Zero, color)];
            var first = list.AddVertices(v);
            list.AddIndices(null, CanvasPrimitive.Lines, first, [0, 1]);
        }

        if (!antialiased)
            return;

        var borderSize = FeatherSize;
        if (width >= 0f && width < 1f)
            borderSize *= width;
        var dir2 = Normalized(diff);
        Feathers(list, beginLeft, beginRight, endLeft, endRight, dir * borderSize, dir2 * borderSize, color);
    }

    // The eight feather quads of canvas_item_add_line / canvas_item_add_rect (left, right, top, bottom, four corners).
    private static void Feathers(CanvasDrawList list, Vector2 beginLeft, Vector2 beginRight, Vector2 endLeft, Vector2 endRight, Vector2 border, Vector2 border2, Vector4 color)
    {
        var clear = color with { W = 0f };
        Primitive(list, [beginLeft, beginLeft + border, endLeft + border, endLeft], [color, clear, clear, color]);
        Primitive(list, [beginRight, beginRight - border, endRight - border, endRight], [color, clear, clear, color]);
        Primitive(list, [beginLeft, beginLeft + border2, beginRight + border2, beginRight], [color, clear, clear, color]);
        Primitive(list, [endLeft, endLeft - border2, endRight - border2, endRight], [color, clear, clear, color]);
        Primitive(list, [beginLeft, beginLeft + border2, beginLeft + border + border2, beginLeft + border], [color, clear, clear, clear]);
        Primitive(list, [beginRight, beginRight + border2, beginRight - border + border2, beginRight - border], [color, clear, clear, clear]);
        Primitive(list, [endLeft, endLeft - border2, endLeft + border - border2, endLeft + border], [color, clear, clear, clear]);
        Primitive(list, [endRight, endRight - border2, endRight - border - border2, endRight - border], [color, clear, clear, clear]);
    }

    // A CommandPrimitive: 3 points = one triangle, 4 = triangles 0-1-2 and 0-2-3.
    private static void Primitive(CanvasDrawList list, ReadOnlySpan<Vector2> points, ReadOnlySpan<Vector4> colors)
    {
        Span<CanvasVertex> v = stackalloc CanvasVertex[points.Length];
        for (var i = 0; i < points.Length; i++)
            v[i] = new CanvasVertex(points[i], Vector2.Zero, colors[i]);
        var first = list.AddVertices(v);
        if (points.Length == 4)
            list.AddIndices(null, CanvasPrimitive.Triangles, first, [0, 1, 2, 0, 2, 3]);
        else
            list.AddIndices(null, CanvasPrimitive.Triangles, first, [0, 1, 2]);
    }

    /// <summary>Godot's <c>canvas_item_add_polyline</c> (thin line strip, or a triangle strip with optional feathers).</summary>
    public static void Polyline(CanvasDrawList list, ReadOnlySpan<Vector2> points, ReadOnlySpan<Vector4> colors, float width, bool antialiased)
    {
        var pointCount = points.Length;
        if (pointCount < 2)
            return;
        var color = White;
        if (antialiased)
            width = CompensatedAntialiasingWidth(width);

        if (width < 0)
        {
            // Line strip: the colours are per point when there is one each, else the first (or white) for all.
            Span<CanvasVertex> v = pointCount <= 256 ? stackalloc CanvasVertex[pointCount] : new CanvasVertex[pointCount];
            for (var i = 0; i < pointCount; i++)
            {
                if (colors.Length == pointCount)
                    color = colors[i];
                else if (colors.Length == 1)
                    color = colors[0];
                else if (i < colors.Length)
                    color = colors[i];
                v[i] = new CanvasVertex(points[i], Vector2.Zero, color);
            }

            var first = list.AddVertices(v);
            Span<int> idx = (pointCount - 1) * 2 <= 512 ? stackalloc int[(pointCount - 1) * 2] : new int[(pointCount - 1) * 2];
            for (var i = 0; i < pointCount - 1; i++)
            {
                idx[i * 2] = i;
                idx[i * 2 + 1] = i + 1;
            }

            list.AddIndices(null, CanvasPrimitive.Lines, first, idx);
            return;
        }

        var polylinePointCount = pointCount * 2;
        var loop = IsEqualApprox(points[0], points[pointCount - 1]);
        var firstSegmentDir = Vector2.Zero;
        var lastSegmentDir = Vector2.Zero;
        for (var i = 1; i < pointCount; i++)
        {
            firstSegmentDir = Normalized(points[i] - points[i - 1]);
            if (!IsZeroApprox(firstSegmentDir))
                break;
        }

        for (var i = pointCount - 1; i >= 1; i--)
        {
            lastSegmentDir = Normalized(points[i] - points[i - 1]);
            if (!IsZeroApprox(lastSegmentDir))
                break;
        }

        var stripCount = polylinePointCount + (antialiased && !loop ? 4 : 0);
        var strip = new Vector2[stripCount];
        var stripColors = new Vector4[stripCount];

        if (antialiased)
        {
            var borderSize = FeatherSize;
            if (width < 1f)
                borderSize *= width;
            var color2 = new Vector4(1, 1, 1, 0);
            var sideCount = polylinePointCount + (loop ? 0 : 5);
            var left = new Vector2[sideCount];
            var leftColors = new Vector4[sideCount];
            var right = new Vector2[sideCount];
            var rightColors = new Vector4[sideCount];

            var prevSegmentDir = Vector2.Zero;
            for (var i = 0; i < pointCount; i++)
            {
                var isFirst = i == 0;
                var isLast = i == pointCount - 1;
                var segmentDir = SegmentDir(points, i, prevSegmentDir);
                if (isFirst && loop)
                    prevSegmentDir = lastSegmentDir;
                else if (isLast && loop)
                    prevSegmentDir = firstSegmentDir;

                Vector2 baseEdgeOffset;
                if (isFirst && !loop)
                    baseEdgeOffset = Orthogonal(firstSegmentDir);
                else if (isLast && !loop)
                    baseEdgeOffset = Orthogonal(lastSegmentDir);
                else
                    baseEdgeOffset = EdgeOffsetClamped(segmentDir, prevSegmentDir);

                var edgeOffset = baseEdgeOffset * (width * 0.5f);
                var border = baseEdgeOffset * borderSize;
                var pos = points[i];
                var j = i * 2 + (loop ? 0 : 2);

                strip[j] = pos + edgeOffset;
                strip[j + 1] = pos - edgeOffset;
                left[j] = pos + edgeOffset;
                left[j + 1] = pos + edgeOffset + border;
                right[j] = pos - edgeOffset;
                right[j + 1] = pos - edgeOffset - border;

                if (i < colors.Length)
                {
                    color = colors[i];
                    color2 = color with { W = 0f };
                }

                stripColors[j] = stripColors[j + 1] = color;
                leftColors[j] = color;
                leftColors[j + 1] = color2;
                rightColors[j] = color;
                rightColors[j + 1] = color2;

                if (isFirst && !loop)
                {
                    var beginBorder = -segmentDir * borderSize;
                    strip[0] = pos + edgeOffset + beginBorder;
                    strip[1] = pos - edgeOffset + beginBorder;
                    stripColors[0] = stripColors[1] = color2;
                    left[0] = pos + edgeOffset + beginBorder;
                    left[1] = pos + edgeOffset + beginBorder + border;
                    leftColors[0] = leftColors[1] = color2;
                    right[0] = pos - edgeOffset + beginBorder;
                    right[1] = pos - edgeOffset + beginBorder - border;
                    rightColors[0] = rightColors[1] = color2;
                }

                if (isLast && !loop)
                {
                    var endBorder = prevSegmentDir * borderSize;
                    var end = polylinePointCount + 2;
                    strip[end] = pos + edgeOffset + endBorder;
                    strip[end + 1] = pos - edgeOffset + endBorder;
                    stripColors[end] = stripColors[end + 1] = color2;

                    left[end] = pos + edgeOffset;
                    left[end + 1] = pos + edgeOffset + endBorder + border;
                    left[end + 2] = pos + edgeOffset + endBorder;
                    leftColors[end] = color;
                    leftColors[end + 1] = color2;
                    leftColors[end + 2] = color2;

                    right[end] = pos - edgeOffset;
                    right[end + 1] = pos - edgeOffset + endBorder - border;
                    right[end + 2] = pos - edgeOffset + endBorder;
                    rightColors[end] = color;
                    rightColors[end + 1] = color2;
                    rightColors[end + 2] = color2;
                }

                prevSegmentDir = segmentDir;
            }

            // Godot allocates the main strip command first, then the left and right feathers.
            TriangleStrip(list, strip, stripColors);
            TriangleStrip(list, left, leftColors);
            TriangleStrip(list, right, rightColors);
            return;
        }

        {
            var prevSegmentDir = Vector2.Zero;
            for (var i = 0; i < pointCount; i++)
            {
                var isFirst = i == 0;
                var isLast = i == pointCount - 1;
                var segmentDir = SegmentDir(points, i, prevSegmentDir);
                if (isFirst && loop)
                    prevSegmentDir = lastSegmentDir;
                else if (isLast && loop)
                    prevSegmentDir = firstSegmentDir;

                Vector2 baseEdgeOffset;
                if (isFirst && !loop)
                    baseEdgeOffset = Orthogonal(firstSegmentDir);
                else if (isLast && !loop)
                    baseEdgeOffset = Orthogonal(lastSegmentDir);
                else
                    baseEdgeOffset = EdgeOffsetClamped(segmentDir, prevSegmentDir);

                var edgeOffset = baseEdgeOffset * (width * 0.5f);
                var pos = points[i];
                strip[i * 2] = pos + edgeOffset;
                strip[i * 2 + 1] = pos - edgeOffset;
                if (i < colors.Length)
                    color = colors[i];
                stripColors[i * 2] = stripColors[i * 2 + 1] = color;
                prevSegmentDir = segmentDir;
            }

            TriangleStrip(list, strip, stripColors);
        }
    }

    private static Vector2 SegmentDir(ReadOnlySpan<Vector2> points, int index, Vector2 prevSegmentDir)
    {
        if (index == points.Length - 1)
            return prevSegmentDir;
        var dir = Normalized(points[index + 1] - points[index]);
        return IsZeroApprox(dir) ? prevSegmentDir : dir;
    }

    // Godot's compute_polyline_edge_offset_clamped.
    private static Vector2 EdgeOffsetClamped(Vector2 segmentDir, Vector2 prevSegmentDir)
    {
        var length = 1f;
        var bisector = Normalized(prevSegmentDir * segmentDir.Length() - segmentDir * prevSegmentDir.Length());
        var angle = MathF.Atan2(Cross(bisector, prevSegmentDir), Vector2.Dot(bisector, prevSegmentDir));
        var sinAngle = MathF.Sin(angle);
        if (!IsZeroApprox(sinAngle) && !IsEqualApprox(segmentDir, prevSegmentDir))
        {
            length = 1f / sinAngle;
            length = Math.Clamp(length, -3f, 3f);
        }
        else
        {
            bisector = Orthogonal(segmentDir);
        }

        if (IsZeroApprox(bisector))
            bisector = Orthogonal(segmentDir);
        return bisector * length;
    }

    private static void TriangleStrip(CanvasDrawList list, ReadOnlySpan<Vector2> points, ReadOnlySpan<Vector4> colors)
    {
        if (points.Length < 3)
            return;
        var v = new CanvasVertex[points.Length];
        for (var i = 0; i < points.Length; i++)
            v[i] = new CanvasVertex(points[i], Vector2.Zero, colors[i]);
        var first = list.AddVertices(v);
        var idx = new int[(points.Length - 2) * 3];
        for (var i = 0; i < points.Length - 2; i++)
        {
            // Strip winding alternates; winding is irrelevant to the canvas (no culling) but keep the strip's order.
            if ((i & 1) == 0)
                (idx[i * 3], idx[i * 3 + 1], idx[i * 3 + 2]) = (i, i + 1, i + 2);
            else
                (idx[i * 3], idx[i * 3 + 1], idx[i * 3 + 2]) = (i + 1, i, i + 2);
        }

        list.AddIndices(null, CanvasPrimitive.Triangles, first, idx);
    }

    // ── Ellipses and arcs ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Godot's <c>CanvasItem::draw_ellipse</c> (circles: <paramref name="major"/> = <paramref name="minor"/>).</summary>
    public static void Ellipse(CanvasDrawList list, Vector2 position, float major, float minor, Vector4 color, bool filled, float width, bool antialiased)
    {
        if (filled)
        {
            AddEllipse(list, position, major, minor, color, antialiased);
        }
        else if (width >= 2f * MathF.Max(major, minor))
        {
            AddEllipse(list, position, major + 0.5f * width, minor + 0.5f * width, color, antialiased);
        }
        else
        {
            Span<Vector2> points = stackalloc Vector2[EllipseSegments + 1];
            var step = MathF.Tau / EllipseSegments;
            for (var i = 0; i < EllipseSegments; i++)
            {
                var angle = i * step;
                points[i] = new Vector2(MathF.Cos(angle) * major, MathF.Sin(angle) * minor) + position;
            }

            points[EllipseSegments] = points[0];
            Polyline(list, points, [color], width, antialiased);
        }
    }

    /// <summary>Godot's <c>canvas_item_add_ellipse</c>: a 64-segment fan around the centre (+ a feather strip).</summary>
    public static void AddEllipse(CanvasDrawList list, Vector2 position, float major, float minor, Vector4 color, bool antialiased)
    {
        if (antialiased)
        {
            major = MathF.Max(0f, major - FeatherSize * 0.25f);
            minor = MathF.Max(0f, minor - FeatherSize * 0.25f);
        }

        var step = MathF.Tau / EllipseSegments;
        Span<CanvasVertex> v = stackalloc CanvasVertex[EllipseSegments + 2];
        v[EllipseSegments + 1] = new CanvasVertex(position, Vector2.Zero, color);
        for (var i = 0; i < EllipseSegments + 1; i++)
        {
            var angle = i * step;
            v[i] = new CanvasVertex(new Vector2(MathF.Cos(angle) * major, MathF.Sin(angle) * minor) + position, Vector2.Zero, color);
        }

        var first = list.AddVertices(v);
        Span<int> idx = stackalloc int[EllipseSegments * 3];
        for (var i = 0; i < EllipseSegments; i++)
        {
            idx[i * 3] = EllipseSegments + 1;
            idx[i * 3 + 1] = i;
            idx[i * 3 + 2] = i + 1;
        }

        list.AddIndices(null, CanvasPrimitive.Triangles, first, idx);

        if (!antialiased)
            return;
        var borderSize = FeatherSize;
        var maxAxis = MathF.Max(major, minor) * 2f;
        if (maxAxis >= 0f && maxAxis < 1f)
            borderSize *= maxAxis * 0.5f;
        var clear = color with { W = 0f };
        var points = new Vector2[2 * EllipseSegments + 2];
        var colors = new Vector4[2 * EllipseSegments + 2];
        for (var i = 0; i < EllipseSegments + 1; i++)
        {
            var angle = i * step;
            var c = MathF.Cos(angle);
            var s = MathF.Sin(angle);
            points[i * 2] = new Vector2(c * major, s * minor) + position;
            points[i * 2 + 1] = new Vector2(c * (major + borderSize), s * (minor + borderSize)) + position;
            colors[i * 2] = color;
            colors[i * 2 + 1] = clear;
        }

        TriangleStrip(list, points, colors);
    }

    /// <summary>Godot's <c>draw_ellipse_arc</c>/<c>draw_arc</c>: a polyline through <paramref name="pointCount"/> points.</summary>
    public static void Arc(CanvasDrawList list, Vector2 center, float major, float minor, float startAngle, float endAngle, int pointCount, Vector4 color, float width, bool antialiased)
    {
        if (pointCount < 2)
            return;
        var points = new Vector2[pointCount];
        var delta = Math.Clamp(endAngle - startAngle, -MathF.Tau, MathF.Tau);
        for (var i = 0; i < pointCount; i++)
        {
            var theta = i / (pointCount - 1f) * delta + startAngle;
            points[i] = center + new Vector2(major * MathF.Cos(theta), minor * MathF.Sin(theta));
        }

        Polyline(list, points, [color], width, antialiased);
    }

    // ── Polygons ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Godot's <c>canvas_item_add_polygon</c>: ear-clipped with Godot's <c>Triangulate</c>.</summary>
    public static void Polygon(CanvasDrawList list, ReadOnlySpan<Vector2> points, ReadOnlySpan<Vector4> colors, ReadOnlySpan<Vector2> uvs, Texture2D? texture)
    {
        var indices = Triangulate(points);
        if (indices is null)
        {
            Log.Error("[Canvas] Invalid polygon data, triangulation failed.");
            return;
        }

        Triangles(list, points, indices, colors, uvs, texture);
    }

    /// <summary>Indexed triangles (Godot's <c>canvas_item_add_triangle_array</c>).</summary>
    public static void Triangles(CanvasDrawList list, ReadOnlySpan<Vector2> points, ReadOnlySpan<int> indices, ReadOnlySpan<Vector4> colors, ReadOnlySpan<Vector2> uvs, Texture2D? texture)
    {
        var v = new CanvasVertex[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var color = colors.Length == points.Length ? colors[i] : colors.Length > 0 ? colors[0] : White;
            v[i] = new CanvasVertex(points[i], uvs.Length == points.Length ? uvs[i] : Vector2.Zero, color);
        }

        var first = list.AddVertices(v);
        list.AddIndices(texture, CanvasPrimitive.Triangles, first, indices);
    }

    /// <summary>Godot's <c>Triangulate::triangulate</c> (John Ratcliff's ear clipping); null when it fails.</summary>
    public static int[]? Triangulate(ReadOnlySpan<Vector2> contour)
    {
        var n = contour.Length;
        if (n < 3)
            return null;
        var v = new int[n];
        if (Area(contour) > 0f)
            for (var i = 0; i < n; i++)
                v[i] = i;
        else
            for (var i = 0; i < n; i++)
                v[i] = n - 1 - i;

        var result = new List<int>((n - 2) * 3);
        var relaxed = false;
        var nv = n;
        var count = 2 * nv;
        for (var m = nv - 1; nv > 2;)
        {
            if (0 >= count--)
            {
                if (relaxed)
                    return null;
                count = 2 * nv;
                relaxed = true;
            }

            var u = m;
            if (nv <= u)
                u = 0;
            m = u + 1;
            if (nv <= m)
                m = 0;
            var w = m + 1;
            if (nv <= w)
                w = 0;

            if (Snip(contour, u, m, w, nv, v, relaxed))
            {
                result.Add(v[u]);
                result.Add(v[m]);
                result.Add(v[w]);
                for (int s = m, t = m + 1; t < nv; s++, t++)
                    v[s] = v[t];
                nv--;
                count = 2 * nv;
            }
        }

        return result.ToArray();
    }

    private static float Area(ReadOnlySpan<Vector2> c)
    {
        var a = 0f;
        for (int p = c.Length - 1, q = 0; q < c.Length; p = q++)
            a += Cross(c[p], c[q]);
        return a * 0.5f;
    }

    private static bool Snip(ReadOnlySpan<Vector2> contour, int u, int v, int w, int n, int[] vv, bool relaxed)
    {
        var a = contour[vv[u]];
        var b = contour[vv[v]];
        var c = contour[vv[w]];
        var threshold = relaxed ? -CmpEpsilon : CmpEpsilon;
        if (threshold > (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X))
            return false;
        for (var p = 0; p < n; p++)
        {
            if (p == u || p == v || p == w)
                continue;
            if (IsInsideTriangle(a, b, c, contour[vv[p]], relaxed))
                return false;
        }

        return true;
    }

    private static bool IsInsideTriangle(Vector2 a, Vector2 b, Vector2 c, Vector2 p, bool includeEdges)
    {
        float ax = c.X - b.X, ay = c.Y - b.Y;
        float bx = a.X - c.X, by = a.Y - c.Y;
        float cx = b.X - a.X, cy = b.Y - a.Y;
        float apx = p.X - a.X, apy = p.Y - a.Y;
        float bpx = p.X - b.X, bpy = p.Y - b.Y;
        float cpx = p.X - c.X, cpy = p.Y - c.Y;
        var aCrossBp = ax * bpy - ay * bpx;
        var cCrossAp = cx * apy - cy * apx;
        var bCrossCp = bx * cpy - by * cpx;
        return includeEdges
            ? aCrossBp > 0f && bCrossCp > 0f && cCrossAp > 0f
            : aCrossBp >= 0f && bCrossCp >= 0f && cCrossAp >= 0f;
    }

    // ── Godot vector helpers (same formulas as core/math/vector2) ────────────────────────────────────────────

    private static Vector2 Orthogonal(Vector2 v) => new(v.Y, -v.X);

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static Vector2 Normalized(Vector2 v)
    {
        var l = v.X * v.X + v.Y * v.Y;
        if (l == 0f)
            return v;
        l = MathF.Sqrt(l);
        return new Vector2(v.X / l, v.Y / l);
    }

    private static bool IsZeroApprox(float f) => MathF.Abs(f) < CmpEpsilon;

    private static bool IsZeroApprox(Vector2 v) => IsZeroApprox(v.X) && IsZeroApprox(v.Y);

    private static bool IsEqualApprox(float a, float b)
    {
        if (a == b)
            return true;
        var tolerance = CmpEpsilon * MathF.Abs(a);
        if (tolerance < CmpEpsilon)
            tolerance = CmpEpsilon;
        return MathF.Abs(a - b) < tolerance;
    }

    private static bool IsEqualApprox(Vector2 a, Vector2 b) => IsEqualApprox(a.X, b.X) && IsEqualApprox(a.Y, b.Y);

    private static float Remap(float value, float istart, float istop, float ostart, float ostop) =>
        ostart + (ostop - ostart) * ((value - istart) / (istop - istart));
}
