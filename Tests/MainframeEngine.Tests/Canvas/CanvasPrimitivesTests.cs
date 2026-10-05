using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>The draw primitives tessellate exactly like Godot 4.7 (counts, layouts, triangulation, sprite rects).</summary>
public sealed class CanvasPrimitivesTests
{
    private static readonly Vector4 White = Vector4.One;

    [Fact]
    public void AFilledCircleIsA64SegmentFanAroundItsCentre()
    {
        var list = new CanvasDrawList();
        list.Clear();
        CanvasPrimitives.Ellipse(list, new Vector2(5, 5), 10, 10, White, filled: true, width: -1, antialiased: false);
        Assert.Equal(66, list.VertexCount);         // 65 rim points (the first repeated) + the centre
        Assert.Equal(64 * 3, list.IndexCount);
        Assert.Equal(new Vector2(5, 5), list.Vertices[65].Position);
        Assert.Equal(new Rect2(-5, -5, 20, 20), list.Bounds);
    }

    [Fact]
    public void ThinLinesAreLinePrimitivesWideOnesQuads()
    {
        var list = new CanvasDrawList();
        list.Clear();
        CanvasPrimitives.Line(list, Vector2.Zero, new Vector2(10, 0), White, -1, false);
        CanvasPrimitives.Line(list, Vector2.Zero, new Vector2(10, 0), White, 4, false);
        Assert.Equal(CanvasPrimitive.Lines, list.Commands[0].Primitive);
        Assert.Equal(CanvasPrimitive.Triangles, list.Commands[1].Primitive);
        // Godot's quad: from + t, from − t, to − t, to + t with t = orthogonal(from − to).normalized × width/2 = (0, 2).
        var quad = list.Vertices.Slice(2, 4).ToArray().Select(v => v.Position);
        Assert.Equal([new Vector2(0, 2), new Vector2(0, -2), new Vector2(10, -2), new Vector2(10, 2)], quad);
    }

    [Fact]
    public void AnOutlinedRectIsAClosedPolylineAndATooWideOneAGrownRect()
    {
        var list = new CanvasDrawList();
        list.Clear();
        CanvasPrimitives.Rect(list, new Rect2(0, 0, 10, 10), White, filled: false, width: 2, antialiased: false);
        Assert.Equal(10, list.VertexCount); // 5 points × 2 (triangle strip)
        list.Clear();
        CanvasPrimitives.Rect(list, new Rect2(0, 0, 10, 10), White, filled: false, width: 20, antialiased: false);
        Assert.Equal(new Rect2(-10, -10, 30, 30), list.Bounds);
    }

    [Fact]
    public void ConcavePolygonsTriangulateLikeGodot()
    {
        // An arrow head (concave at index 2); Godot's Triangulate (CCW reorder, ear clipping) gives these indices.
        Vector2[] points = [new(0, 0), new(10, 5), new(4, 5), new(10, 10), new(0, 10)];
        var indices = CanvasPrimitives.Triangulate(points);
        Assert.NotNull(indices);
        Assert.Equal(9, indices!.Length);
        // Every triangle is non-degenerate and uses valid points; area adds up to the polygon's.
        var area = 0f;
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = points[indices[i]];
            var b = points[indices[i + 1]];
            var c = points[indices[i + 2]];
            area += MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2f;
        }

        Assert.Equal(60f, area, 3); // shoelace area of the arrow head
    }

    [Fact]
    public void SpriteRectsFollowGodotGetRects()
    {
        var texture = Texture2D.FromPixels(8, 4, new byte[8 * 4 * 4]);
        var sprite = new Sprite2D { Texture = texture };
        Assert.Equal(new Rect2(-4, -2, 8, 4), sprite.GetRect());
        sprite.Centered = false;
        sprite.Offset = new Vector2(1, 1);
        sprite.Hframes = 2;
        Assert.Equal(new Rect2(1, 1, 4, 4), sprite.GetRect());

        // A flipped region: same rect on screen, mirrored UVs.
        var list = new CanvasDrawList();
        list.Clear();
        CanvasPrimitives.TextureRectRegion(list, texture, new Rect2(0, 0, -8, 4), new Rect2(0, 0, 8, 4), White, transpose: false);
        Assert.Equal(new Rect2(0, 0, 8, 4), list.Bounds);
        Assert.Equal(new Vector2(0, 0), list.Vertices[0].Uv);
        Assert.Equal(new Vector2(8, 0), list.Vertices[0].Position); // corner (0,0) of the quad mirrored to x = 8
    }

    [Fact]
    public void DrawSetTransformAppliesToLaterCommands()
    {
        var list = new CanvasDrawList();
        list.Clear();
        list.DrawTransform = Transform2D.FromTrs(new Vector2(100, 0), 0, new Vector2(2));
        CanvasPrimitives.AddRect(list, new Rect2(0, 0, 1, 1), White, false);
        Assert.Equal(new Rect2(100, 0, 2, 2), list.Bounds);
    }
}
