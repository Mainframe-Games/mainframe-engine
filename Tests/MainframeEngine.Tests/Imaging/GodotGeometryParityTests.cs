using System.Numerics;
using System.Text.Json;

namespace MainframeEngine.Tests.Imaging;

/// <summary>
/// #112: <see cref="BitMask"/> and <see cref="Geometry2D"/> against Godot 4.7.2's <c>BitMap</c> and <c>Geometry2D</c> on the
/// same inputs (<c>Fixtures/godot-geometry-reference.gd</c> builds them and wrote the JSON).
/// </summary>
public sealed class GodotGeometryParityTests
{
    private static readonly JsonElement Reference = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Imaging", "Fixtures", "godot-geometry-reference.json"))).RootElement;

    private static BitMask Disc()
    {
        var mask = new BitMask(new Vector2I(64, 64));
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                mask.SetBit(x, y, (x - 32) * (x - 32) + (y - 32) * (y - 32) <= 400);
        return mask;
    }

    // Two diamonds touching corner to corner, a ring with a hole, and a lone rectangle.
    private static BitMask Blobs()
    {
        var mask = new BitMask(new Vector2I(64, 40));
        for (var y = 0; y < 40; y++)
            for (var x = 0; x < 64; x++)
            {
                var d1 = Math.Abs(x - 8) + Math.Abs(y - 8) <= 6;
                var d2 = Math.Abs(x - 21) + Math.Abs(y - 21) <= 6;
                var r2 = (x - 44) * (x - 44) + (y - 14) * (y - 14);
                var ring = r2 is <= 100 and >= 25;
                var rect = x is >= 2 and < 12 && y is >= 28 and < 36;
                mask.SetBit(x, y, d1 || d2 || ring || rect);
            }

        return mask;
    }

    private static Vector2[][] Expected(string key) =>
        [.. Reference.GetProperty(key).EnumerateArray().Select(p => p.EnumerateArray()
            .Select(v => new Vector2(v[0].GetSingle(), v[1].GetSingle())).ToArray())];

    private static void AssertPaths(string key, IReadOnlyList<Vector2[]> actual, float tolerance = 1e-3f)
    {
        var expected = Expected(key);
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Length, actual[i].Length);
            for (var j = 0; j < expected[i].Length; j++)
                Assert.True(Vector2.Distance(expected[i][j], actual[i][j]) <= tolerance, $"{key}[{i}][{j}]: {actual[i][j]} vs Godot's {expected[i][j]}");
        }
    }

    private static Rect2I All(BitMask mask) => new(Vector2I.Zero, mask.GetSize());

    [Theory]
    [InlineData("disc2", 2f)]
    [InlineData("disc05", 0.5f)]
    public void OpaqueToPolygonsMatchesGodotOnADisc(string key, float epsilon)
    {
        var disc = Disc();
        AssertPaths(key, disc.OpaqueToPolygons(All(disc), epsilon), 0f);
    }

    [Fact]
    public void OpaqueToPolygonsMatchesGodotOnTouchingAndHollowShapes()
    {
        var blobs = Blobs();
        AssertPaths("blobs2", blobs.OpaqueToPolygons(All(blobs)), 0f);
    }

    [Fact]
    public void GrowShrinkAndResizeMatchGodot()
    {
        Assert.Equal(Reference.GetProperty("count").GetInt32(), Blobs().GetTrueBitCount());

        var grown = Blobs();
        grown.GrowMask(3, new Rect2I(0, 0, 64, 40));
        Assert.Equal(Reference.GetProperty("grow3").GetInt32(), grown.GetTrueBitCount());

        var shrunk = Blobs();
        shrunk.ShrinkMask(2, new Rect2I(0, 0, 64, 40));
        Assert.Equal(Reference.GetProperty("shrink2").GetInt32(), shrunk.GetTrueBitCount());

        var partial = Blobs();
        partial.GrowMask(2, new Rect2I(0, 0, 30, 30));
        Assert.Equal(Reference.GetProperty("grow2rect").GetInt32(), partial.GetTrueBitCount());

        var resized = Disc();
        resized.Resize(new Vector2I(40, 24));
        Assert.Equal(Reference.GetProperty("resize").GetInt32(), resized.GetTrueBitCount());
    }

    private static readonly Vector2[] A = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
    private static readonly Vector2[] B = [new(5, 5), new(15, 5), new(15, 15), new(5, 15)];

    [Fact]
    public void PolygonBooleansMatchGodot()
    {
        AssertPaths("merge", Geometry2D.MergePolygons(A, B));
        AssertPaths("clip", Geometry2D.ClipPolygons(A, B));
        AssertPaths("intersect", Geometry2D.IntersectPolygons(A, B));
        AssertPaths("exclude", Geometry2D.ExcludePolygons(A, B));
        AssertPaths("hole", Geometry2D.ClipPolygons(A, [new(2, 2), new(8, 2), new(8, 8), new(2, 8)]));
        Vector2[] line = [new(-5, 5), new(20, 5)];
        AssertPaths("clip_line", Geometry2D.ClipPolylineWithPolygon(line, A));
        AssertPaths("intersect_line", Geometry2D.IntersectPolylineWithPolygon(line, A));
    }

    [Fact]
    public void OffsetsMatchGodot()
    {
        AssertPaths("offset_sq", Geometry2D.OffsetPolygon(A, 2.5f));
        AssertPaths("offset_round", Geometry2D.OffsetPolygon(A, 2.5f, Geometry2D.PolyJoinType.Round));
        AssertPaths("offset_miter", Geometry2D.OffsetPolygon(A, 2.5f, Geometry2D.PolyJoinType.Miter));
        AssertPaths("offset_in", Geometry2D.OffsetPolygon(A, -2f));
        AssertPaths("offset_line", Geometry2D.OffsetPolyline([new(0, 0), new(20, 0), new(20, 10)], 1.5f));
        Assert.Throws<ArgumentException>(() => Geometry2D.OffsetPolyline(A, 1f, endType: Geometry2D.PolyEndType.Polygon));
    }

    [Fact]
    public void TriangulationAndWindingMatchGodot()
    {
        Vector2[] concave = [new(0, 0), new(10, 0), new(10, 10), new(5, 4), new(0, 10)];
        Assert.Equal(Reference.GetProperty("triangulate").EnumerateArray().Select(i => i.GetInt32()), Geometry2D.TriangulatePolygon(concave));
        var clockwise = Reference.GetProperty("clockwise");
        Assert.Equal(clockwise[0].GetBoolean(), Geometry2D.IsPolygonClockwise(A));
        Assert.Equal(clockwise[1].GetBoolean(), Geometry2D.IsPolygonClockwise([new(0, 0), new(0, 10), new(10, 10), new(10, 0)]));
        Assert.Empty(Geometry2D.TriangulatePolygon([new(0, 0), new(1, 1)]));
    }

    [Fact]
    public void AlphaThresholdAndImageRoundTrip()
    {
        var image = new Bitmap(4, 1);
        byte[] alphas = [0, 25, 26, 255]; // Godot's test is alpha / 255 > threshold (0.1)
        for (var x = 0; x < 4; x++)
            image.Data[x * 4 + 3] = alphas[x];
        var mask = new BitMask();
        mask.CreateFromImageAlpha(image);

        Assert.Equal([false, false, true, true], Enumerable.Range(0, 4).Select(x => mask.GetBit(x, 0)));
        var back = mask.ConvertToImage();
        Assert.Equal(255, back.Data[2 * 4]);
        Assert.Equal(0, back.Data[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.GetBit(4, 0));
    }
}
