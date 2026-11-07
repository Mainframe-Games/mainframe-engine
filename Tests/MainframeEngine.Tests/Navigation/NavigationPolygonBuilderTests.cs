using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.9: outlines and obstructions → convex polygons (Clipper2 offsets and booleans, ear clipping, Hertel–Mehlhorn).</summary>
public sealed class NavigationPolygonBuilderTests
{
    internal static Vector2[] Box(float x0, float y0, float x1, float y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    private static (Vector2[] Vertices, int[][] Polygons) Build(Vector2[][] outlines, Vector2[][]? obstructions = null, float radius = 0f,
        Rect2 rect = default) =>
        NavigationPolygonBuilder.Build(outlines, obstructions ?? [], radius, 1f, rect);

    internal static float Area(Vector2[] vertices, int[] polygon)
    {
        var sum = 0f;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = vertices[polygon[i]];
            var b = vertices[polygon[(i + 1) % polygon.Length]];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return sum * 0.5f;
    }

    private static float TotalArea((Vector2[] Vertices, int[][] Polygons) mesh) => mesh.Polygons.Sum(p => Area(mesh.Vertices, p));

    private static bool InsideAny((Vector2[] Vertices, int[][] Polygons) mesh, Vector2 point)
    {
        foreach (var polygon in mesh.Polygons)
        {
            var inside = true;
            for (var i = 0; i < polygon.Length && inside; i++)
            {
                var a = mesh.Vertices[polygon[i]];
                var b = mesh.Vertices[polygon[(i + 1) % polygon.Length]];
                inside = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X) > 0f;
            }

            if (inside)
                return true;
        }

        return false;
    }

    // Every polygon convex, counter-clockwise (maths sense) and with positive area; vertices unique.
    internal static void AssertValid((Vector2[] Vertices, int[][] Polygons) mesh)
    {
        Assert.Equal(mesh.Vertices.Length, mesh.Vertices.Distinct().Count());
        foreach (var polygon in mesh.Polygons)
        {
            Assert.True(polygon.Length >= 3);
            Assert.True(Area(mesh.Vertices, polygon) > 0f);
            for (var i = 0; i < polygon.Length; i++)
            {
                var a = mesh.Vertices[polygon[i]];
                var b = mesh.Vertices[polygon[(i + 1) % polygon.Length]];
                var c = mesh.Vertices[polygon[(i + 2) % polygon.Length]];
                Assert.True((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) >= 0f, "a reflex corner");
            }
        }
    }

    [Fact]
    public void ASquareIsOneConvexPolygon()
    {
        var mesh = Build([Box(0, 0, 100, 100)]);
        AssertValid(mesh);
        var polygon = Assert.Single(mesh.Polygons);
        Assert.Equal(4, polygon.Length);
        Assert.Equal(10_000f, TotalArea(mesh));
    }

    [Fact]
    public void AnLShapeSplitsIntoConvexPolygonsAndKeepsItsArea()
    {
        Vector2[] l = [new(0, 0), new(200, 0), new(200, 100), new(100, 100), new(100, 300), new(0, 300)];
        var mesh = Build([l]);
        AssertValid(mesh);
        Assert.Equal(2, mesh.Polygons.Length);
        Assert.Equal(200 * 100 + 100 * 200, TotalArea(mesh));
    }

    [Fact]
    public void AnOutlineInsideAnotherIsAHole()
    {
        var mesh = Build([Box(0, 0, 300, 300), Box(100, 100, 200, 200)]);
        AssertValid(mesh);
        Assert.Equal(300 * 300 - 100 * 100, TotalArea(mesh));
        Assert.False(InsideAny(mesh, new Vector2(150, 150)));
        Assert.True(InsideAny(mesh, new Vector2(50, 150)));
        Assert.True(mesh.Polygons.Length <= 8);
    }

    [Fact]
    public void ObstructionsAreCarvedAndGrownByTheAgentRadius()
    {
        var mesh = Build([Box(0, 0, 400, 400)], [Box(150, 150, 250, 250)], radius: 10f);
        AssertValid(mesh);
        // The walkable square shrinks by 10 on each side, the box grows by 10 (miter joins keep square corners).
        Assert.Equal(380f * 380f - 120f * 120f, TotalArea(mesh));
        Assert.False(InsideAny(mesh, new Vector2(145, 200)));
        Assert.True(InsideAny(mesh, new Vector2(135, 200)));
        Assert.False(InsideAny(mesh, new Vector2(5, 200)));
    }

    [Fact]
    public void ManyHolesBridgeIntoOneRing()
    {
        var pillars = new List<Vector2[]>();
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 5; x++)
                pillars.Add(Box(40 + x * 80, 40 + y * 80, 60 + x * 80 + y, 60 + y * 80));
        var mesh = Build([Box(0, 0, 400, 400)], [.. pillars]);
        AssertValid(mesh);
        var expected = 400f * 400f - pillars.Sum(p => (p[1].X - p[0].X) * (p[2].Y - p[1].Y));
        Assert.Equal(expected, TotalArea(mesh), 1f);
        foreach (var p in pillars)
            Assert.False(InsideAny(mesh, (p[0] + p[2]) * 0.5f));
    }

    [Fact]
    public void TouchingAndOverlappingObstructionsMerge()
    {
        var mesh = Build([Box(0, 0, 300, 100)], [Box(50, 20, 100, 80), Box(100, 20, 150, 80), Box(140, 40, 200, 90)]);
        AssertValid(mesh);
        var carved = 100f * 60f + 50f * 50f + 10f * 10f; // two boxes side by side, then the third's part outside them
        Assert.Equal(300f * 100f - carved, TotalArea(mesh), 1f);
    }

    [Fact]
    public void AnObstructionSplittingTheAreaLeavesTwoIslands()
    {
        var mesh = Build([Box(0, 0, 300, 100)], [Box(140, -10, 160, 110)]);
        AssertValid(mesh);
        Assert.Equal(2, mesh.Polygons.Length);
        Assert.Equal(300f * 100f - 20f * 100f, TotalArea(mesh));
    }

    [Fact]
    public void AnIslandInsideAHoleIsKept()
    {
        var mesh = Build([Box(0, 0, 300, 300), Box(50, 50, 250, 250), Box(100, 100, 200, 200)]);
        AssertValid(mesh);
        Assert.Equal(300f * 300f - 200f * 200f + 100f * 100f, TotalArea(mesh));
        Assert.True(InsideAny(mesh, new Vector2(150, 150)));
        Assert.False(InsideAny(mesh, new Vector2(75, 150)));
    }

    [Fact]
    public void TheBakingRectangleAloneIsTheTraversableArea()
    {
        var mesh = Build([], [Box(40, 40, 60, 60)], rect: new Rect2(0, 0, 100, 100));
        AssertValid(mesh);
        Assert.Equal(100f * 100f - 20f * 20f, TotalArea(mesh));

        var clipped = Build([Box(0, 0, 200, 200)], rect: new Rect2(50, 50, 100, 20));
        Assert.Equal(100f * 20f, TotalArea(clipped));
    }

    [Fact]
    public void DegenerateInputGivesAnEmptyMesh()
    {
        Assert.Empty(Build([]).Polygons);
        Assert.Empty(Build([[new(0, 0), new(10, 0)]]).Polygons);
        Assert.Empty(Build([[new(0, 0), new(10, 0), new(20, 0)]]).Polygons);
        Assert.Empty(Build([Box(0, 0, 10, 10)], radius: 6f).Polygons); // the radius eats it
        Assert.Empty(Build([Box(0, 0, 10, 10)], [Box(-5, -5, 15, 15)]).Polygons);
    }

    [Fact]
    public void EitherWindingWorks()
    {
        var clockwise = Box(0, 0, 100, 50).Reverse().ToArray();
        var mesh = Build([clockwise], [Box(40, 10, 60, 40).Reverse().ToArray()]);
        AssertValid(mesh);
        Assert.Equal(100f * 50f - 20f * 30f, TotalArea(mesh));
    }

    [Fact]
    public void AStarWithHolesIsConvexCoveredAndDeterministic()
    {
        var star = new Vector2[18];
        for (var i = 0; i < star.Length; i++)
        {
            var angle = i * MathF.Tau / star.Length;
            var r = i % 2 == 0 ? 300f : 140f;
            star[i] = new Vector2(400 + MathF.Cos(angle) * r, 400 + MathF.Sin(angle) * r);
        }

        Vector2[][] holes = [Box(380, 380, 420, 420), Box(500, 390, 520, 410), Box(300, 380, 320, 430)];
        var a = Build([star], holes, radius: 4f);
        var b = Build([star], holes, radius: 4f);
        AssertValid(a);
        Assert.Equal(a.Vertices, b.Vertices);
        Assert.Equal(a.Polygons.Length, b.Polygons.Length);
        for (var i = 0; i < a.Polygons.Length; i++)
            Assert.Equal(a.Polygons[i], b.Polygons[i]);

        // The pieces cover the offset star minus the grown holes: compare with Clipper's own area.
        var expected = ClipperArea([star], holes, 4f);
        Assert.Equal(expected, TotalArea(a), expected * 1e-4);
        foreach (var h in holes)
            Assert.False(InsideAny(a, (h[0] + h[2]) * 0.5f));
    }

    private static double ClipperArea(Vector2[][] outlines, Vector2[][] holes, float radius)
    {
        static Clipper2Lib.Paths64 Paths(Vector2[][] rings) =>
            [.. rings.Select(r => new Clipper2Lib.Path64(r.Select(p => new Clipper2Lib.Point64((long)Math.Round(p.X, MidpointRounding.AwayFromZero), (long)Math.Round(p.Y, MidpointRounding.AwayFromZero)))))];
        var walk = Clipper2Lib.Clipper.InflatePaths(Clipper2Lib.Clipper.Union(Paths(outlines), Clipper2Lib.FillRule.EvenOdd), -radius,
            Clipper2Lib.JoinType.Miter, Clipper2Lib.EndType.Polygon, 2.0);
        var clip = Clipper2Lib.Clipper.InflatePaths(Clipper2Lib.Clipper.Union(Paths(holes), Clipper2Lib.FillRule.NonZero), radius,
            Clipper2Lib.JoinType.Miter, Clipper2Lib.EndType.Polygon, 2.0);
        return Clipper2Lib.Clipper.Area(Clipper2Lib.Clipper.Difference(walk, clip, Clipper2Lib.FillRule.NonZero));
    }

    [Fact]
    public void MakePolygonsFromOutlinesFillsTheResource()
    {
        var polygon = new NavigationPolygon();
        polygon.AddOutline(Box(0, 0, 100, 100));
        polygon.AddOutline(Box(40, 40, 60, 60));
        var version = polygon.Version;
        polygon.MakePolygonsFromOutlines();
        Assert.True(polygon.Version > version);
        AssertValid((polygon.Vertices, polygon.Polygons));
        Assert.Equal(100f * 100f - 20f * 20f, polygon.Polygons.Sum(p => Area(polygon.Vertices, p)));
    }
}
