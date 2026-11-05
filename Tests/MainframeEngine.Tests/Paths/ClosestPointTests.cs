using System.Numerics;

namespace MainframeEngine.Tests.Paths;

/// <summary>
/// G13.1's closest-point grid and hinted query: on seeded random curves the grid gives exactly what a full scan of the
/// baked segments gives (the scan below repeats <c>CurveBaker</c>'s arithmetic), and queries allocate nothing.
/// </summary>
public sealed class ClosestPointTests
{
    // The reference: every baked segment, scalar dot products in a fixed order, the first strictly closer one wins.
    private static (float Offset, Vector3 Point) Scan(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> distances, Vector3 p)
    {
        var best = float.PositiveInfinity;
        var offset = 0f;
        var point = points[0];
        for (var i = 0; i < points.Length - 1; i++)
        {
            var a = points[i];
            var d = points[i + 1] - a;
            var lengthSquared = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
            var ap = p - a;
            var t = lengthSquared > 0 ? Math.Clamp((ap.X * d.X + ap.Y * d.Y + ap.Z * d.Z) / lengthSquared, 0f, 1f) : 0f;
            var q = a + d * t;
            var e = q - p;
            var distance = e.X * e.X + e.Y * e.Y + e.Z * e.Z;
            if (distance < best)
            {
                best = distance;
                point = q;
                offset = distances[i] + (distances[i + 1] - distances[i]) * t;
            }
        }

        return (offset, point);
    }

    private static Curve3D RandomCurve(Random random)
    {
        var count = random.Next(3, 12);
        var points = new Vector3[count];
        var extent = 20f + (float)random.NextDouble() * 300f;
        var flat = random.Next(3) == 0; // some curves lie in a plane (one cell thick)
        for (var i = 0; i < count; i++)
            points[i] = new Vector3(Next(random, extent), flat ? 3f : Next(random, extent * 0.3f), Next(random, extent));
        var curve = new Curve3D { BakeInterval = 0.1f + (float)random.NextDouble() * 2f };
        curve.SetSmoothPoints(points, closed: random.Next(2) == 0);
        return curve;
    }

    private static float Next(Random random, float extent) => ((float)random.NextDouble() - 0.5f) * extent;

    [Fact]
    public void TheGridGivesTheFullScansAnswerOnRandomCurves()
    {
        var random = new Random(1301);
        var queries = 0;
        for (var c = 0; c < 1000; c++)
        {
            var curve = RandomCurve(random);
            var points = curve.GetBakedPoints().ToArray();
            var distances = curve.GetBakedDistances().ToArray();
            var extent = 400f;
            for (var q = 0; q < 8; q++)
            {
                // Near the curve, on it, and far outside its bounds.
                var p = q switch
                {
                    0 => points[random.Next(points.Length)],
                    < 5 => points[random.Next(points.Length)] + new Vector3(Next(random, 6), Next(random, 6), Next(random, 6)),
                    _ => new Vector3(Next(random, extent), Next(random, extent), Next(random, extent)),
                };
                var (offset, point) = Scan(points, distances, p);
                Assert.Equal(offset, curve.GetClosestOffset(p));
                Assert.Equal(point, curve.GetClosestPoint(p));
                queries++;
            }
        }

        Assert.Equal(8000, queries);
    }

    [Fact]
    public void TheGridGivesTheFullScansAnswerIn2D()
    {
        var random = new Random(77);
        for (var c = 0; c < 300; c++)
        {
            var count = random.Next(3, 10);
            var pts = new Vector2[count];
            for (var i = 0; i < count; i++)
                pts[i] = new Vector2(Next(random, 3000), Next(random, 2000));
            var curve = new Curve2D { BakeInterval = 1f + (float)random.NextDouble() * 10f };
            curve.SetSmoothPoints(pts, closed: random.Next(2) == 0);
            var baked = curve.GetBakedPoints().ToArray().Select(v => new Vector3(v, 0f)).ToArray();
            var distances = curve.GetBakedDistances().ToArray();
            for (var q = 0; q < 6; q++)
            {
                var p = new Vector2(Next(random, 4000), Next(random, 3000));
                Assert.Equal(Scan(baked, distances, new Vector3(p, 0f)).Offset, curve.GetClosestOffset(p));
            }
        }
    }

    [Fact]
    public void TheHintedQueryFindsTheSameOffsetInsideItsWindowAndWrapsOnClosedCurves()
    {
        var curve = new Curve3D { BakeInterval = 0.2f };
        curve.SetSmoothPoints([new(0, 0, 0), new(200, 5, 0), new(200, 0, 200), new(0, 5, 200)], closed: true);
        var length = curve.GetBakedLength();
        for (var d = 0f; d < length; d += 37.3f)
        {
            var p = curve.SampleBaked(d) + new Vector3(0, 1.5f, 0);
            Assert.Equal(curve.GetClosestOffset(p), curve.GetClosestOffset(p, d + 2f, 10f));
        }

        // Across the seam: a point just after the start, hinted from just before the end.
        var start = curve.SampleBaked(3f) + new Vector3(0, 1, 0);
        Assert.Equal(curve.GetClosestOffset(start), curve.GetClosestOffset(start, length - 2f, 8f), 3);
        // A window wider than the curve is a full search.
        Assert.Equal(curve.GetClosestOffset(start), curve.GetClosestOffset(start, 0f, length));
    }

    [Fact]
    public void ClosestQueriesAllocateNothing()
    {
        var curve = new Curve3D { BakeInterval = 0.2f };
        curve.SetSmoothPoints([new(0, 0, 0), new(500, 5, 0), new(500, 0, 500), new(0, 5, 500)], closed: true);
        var curve2 = new Curve2D();
        curve2.SetSmoothPoints([new(0, 0), new(3000, 50), new(3000, 3000), new(0, 2500)], closed: true);
        curve.GetClosestOffset(Vector3.Zero);
        curve2.GetClosestOffset(Vector2.Zero);
        var sum = 0f;
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var p = new Vector3(i * 2.5f, 1, i * 1.5f);
                sum += curve.GetClosestOffset(p) + curve.GetClosestOffset(p, i * 3f, 20f);
                sum += curve2.GetClosestOffset(new Vector2(i * 15f, i * 9f));
            }
        });
        Assert.Equal(0, allocated);
        Assert.True(float.IsFinite(sum));
    }
}
