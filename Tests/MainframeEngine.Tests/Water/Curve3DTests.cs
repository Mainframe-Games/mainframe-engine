using System.Numerics;

namespace MainframeEngine.Tests.Water;

public sealed class Curve3DTests
{
    private static Curve3D Line(float length = 10f, float interval = 0.2f)
    {
        var curve = new Curve3D { BakeInterval = interval };
        curve.AddPoint(Vector3.Zero, @out: new Vector3(length / 3, 0, 0));
        curve.AddPoint(new Vector3(length, 0, 0), @in: new Vector3(-length / 3, 0, 0));
        return curve;
    }

    /// <summary>A quarter circle of radius r in XZ from (r, 0, 0) to (0, 0, r) (the standard 0.5523 handle).</summary>
    private static Curve3D QuarterCircle(float r)
    {
        const float k = 0.5522847f;
        var curve = new Curve3D { BakeInterval = 0.1f };
        curve.AddPoint(new Vector3(r, 0, 0), @out: new Vector3(0, 0, k * r));
        curve.AddPoint(new Vector3(0, 0, r), @in: new Vector3(k * r, 0, 0));
        return curve;
    }

    [Fact]
    public void BezierMatchesHandComputedValues()
    {
        var curve = new Curve3D();
        curve.AddPoint(Vector3.Zero, @out: new Vector3(0, 3, 0));
        curve.AddPoint(new Vector3(3, 0, 0), @in: new Vector3(0, 3, 0));
        // 0.125 p0 + 0.375 c0 + 0.375 c1 + 0.125 p1 with c0 = (0, 3, 0), c1 = (3, 3, 0).
        Assert.Equal(new Vector3(1.5f, 2.25f, 0), curve.Sample(0, 0.5f));
        Assert.Equal(Vector3.Zero, curve.Sample(0, 0f));
        Assert.Equal(new Vector3(3, 0, 0), curve.Sample(0, 1f));
        // t = 0.25: 27/64 p0 + 27/64 c0 + 9/64 c1 + 1/64 p1.
        var quarter = curve.Sample(0, 0.25f);
        Assert.Equal(30f / 64f, quarter.X, 5);
        Assert.Equal(108f / 64f, quarter.Y, 5);
        // Out-of-range segment indices clamp to the end points (Godot).
        Assert.Equal(Vector3.Zero, curve.Sample(-1, 0.5f));
        Assert.Equal(new Vector3(3, 0, 0), curve.Sample(5, 0.5f));
    }

    [Fact]
    public void QuarterCircleBakedLengthIsWithinATenthOfAPercent()
    {
        var curve = QuarterCircle(10f);
        var expected = MathF.PI * 10f / 2f;
        Assert.InRange(curve.GetBakedLength(), expected * 0.999f, expected * 1.001f);
        // Baked points are equally spaced and on the circle.
        var points = curve.GetBakedPoints();
        var distances = curve.GetBakedDistances();
        Assert.True(points.Length > 100);
        Assert.Equal(0f, distances[0]);
        Assert.Equal(curve.GetBakedLength(), distances[^1]);
        foreach (var p in points)
            Assert.InRange(new Vector2(p.X, p.Z).Length(), 9.99f, 10.01f);
        var step = curve.GetBakedLength() / (points.Length - 1);
        Assert.True(step <= 0.1f);
        for (var i = 1; i < points.Length; i++)
            Assert.InRange(Vector3.Distance(points[i], points[i - 1]), step * 0.99f, step * 1.0001f);
    }

    [Fact]
    public void SampleBakedClampsAtTheEnds()
    {
        var curve = Line();
        Assert.Equal(10f, curve.GetBakedLength(), 4);
        Assert.Equal(Vector3.Zero, curve.SampleBaked(0));
        Assert.Equal(Vector3.Zero, curve.SampleBaked(-5));
        Assert.Equal(new Vector3(10, 0, 0), curve.SampleBaked(curve.GetBakedLength()));
        Assert.Equal(new Vector3(10, 0, 0), curve.SampleBaked(100));
        Assert.Equal(4.3f, curve.SampleBaked(4.3f).X, 3);
        Assert.Equal(4.3f, curve.SampleBaked(4.3f, cubic: true).X, 3);
    }

    [Fact]
    public void EmptyAndSinglePointCurvesAreSafe()
    {
        var curve = new Curve3D();
        Assert.Equal(0f, curve.GetBakedLength());
        Assert.Equal(Vector3.Zero, curve.SampleBaked(1));
        Assert.Equal(0f, curve.GetClosestOffset(Vector3.One));
        curve.AddPoint(new Vector3(1, 2, 3));
        Assert.Equal(0f, curve.GetBakedLength());
        Assert.Equal(new Vector3(1, 2, 3), curve.SampleBaked(4));
        Assert.Equal(new Vector3(1, 2, 3), curve.GetClosestPoint(Vector3.Zero));
        Assert.Equal(1f, curve.SampleBakedWidth(0));
    }

    [Fact]
    public void ClosestOffsetAndPointProjectOntoTheCurve()
    {
        var curve = Line();
        Assert.Equal(4f, curve.GetClosestOffset(new Vector3(4, 3, -2)), 3);
        Assert.Equal(new Vector3(4, 0, 0), curve.GetClosestPoint(new Vector3(4, 3, 0)));
        Assert.Equal(0f, curve.GetClosestOffset(new Vector3(-5, 0, 0)));
        Assert.Equal(curve.GetBakedLength(), curve.GetClosestOffset(new Vector3(20, 0, 0)));

        var circle = QuarterCircle(10f);
        var mid = circle.GetClosestOffset(new Vector3(20, 0, 20)); // on the 45° diagonal
        Assert.InRange(mid, circle.GetBakedLength() * 0.49f, circle.GetBakedLength() * 0.51f);
    }

    [Fact]
    public void WidthDepthAndTiltInterpolateWithSmoothstepBetweenPoints()
    {
        var curve = Line();
        curve.SetPointWidth(0, 2f);
        curve.SetPointWidth(1, 4f);
        curve.SetPointDepth(1, 1f);
        curve.SetPointTilt(1, 0.5f);
        var length = curve.GetBakedLength();
        Assert.Equal(2f, curve.SampleBakedWidth(0), 4);
        Assert.Equal(4f, curve.SampleBakedWidth(length), 4);
        Assert.Equal(3f, curve.SampleBakedWidth(length / 2), 2);
        // Smoothstep: flat at the ends (a quarter of the way is ≈ 15.6 % of the change for an even parametrisation).
        Assert.InRange(curve.SampleBakedDepth(length / 4), 0.1f, 0.2f);
        Assert.Equal(0.25f, curve.SampleBakedTilt(length / 2), 2);
    }

    [Fact]
    public void SampleBakedWithRotationLooksAlongTheCurve()
    {
        var curve = Line();
        var t = curve.SampleBakedWithRotation(5f);
        Assert.Equal(5f, t.Origin.X, 3);
        var forward = -t.Basis.Z;
        Assert.Equal(1f, forward.X, 4);
        Assert.Equal(1f, t.Basis.Y.Y, 4);

        curve.SetPointTilt(0, MathF.PI / 2);
        curve.SetPointTilt(1, MathF.PI / 2);
        var rolled = curve.SampleBakedWithRotation(5f, applyTilt: true);
        Assert.Equal(0f, rolled.Basis.Y.Y, 4); // rolled 90° about the direction
    }

    [Fact]
    public void PointsRoundTripThroughAScene()
    {
        var curve = QuarterCircle(5f);
        curve.SetPointWidth(1, 3.5f);
        curve.SetPointDepth(0, 0.6f);
        curve.SetPointTilt(1, 0.25f);
        var root = new Node3D { Name = "Root" };
        var river = new River3D { Name = "River", Curve = curve };
        root.AddChild(river);
        river.Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetNode<River3D>("River").Curve!;
        Assert.Equal(curve.Points, copy.Points);
        Assert.Equal(curve.BakeInterval, copy.BakeInterval);
        Assert.Equal(3.5f, copy.GetPointWidth(1));
        Assert.Equal(curve.GetBakedLength(), copy.GetBakedLength());
    }

    [Fact]
    public void EverySetterRaisesChanged()
    {
        var curve = Line();
        var changes = 0;
        curve.Changed += () => changes++;
        curve.AddPoint(new Vector3(20, 0, 0));
        curve.SetPointPosition(2, new Vector3(21, 0, 0));
        curve.SetPointIn(2, Vector3.One);
        curve.SetPointOut(2, Vector3.One);
        curve.SetPointTilt(2, 1f);
        curve.SetPointWidth(2, 2f);
        curve.SetPointDepth(2, 0.5f);
        curve.BakeInterval = 0.5f;
        curve.RemovePoint(2);
        curve.ClearPoints();
        Assert.Equal(10, changes);
        Assert.Equal(0, curve.PointCount);
    }

    [Fact]
    public void AddPointInsertsAtAnIndexWithDefaults()
    {
        var curve = Line();
        curve.AddPoint(new Vector3(5, 1, 0), index: 1);
        Assert.Equal(3, curve.PointCount);
        Assert.Equal(new Vector3(5, 1, 0), curve.GetPointPosition(1));
        Assert.Equal(new Vector3(10, 0, 0), curve.GetPointPosition(2));
        Assert.Equal(1f, curve.GetPointWidth(1));
        Assert.Equal(0f, curve.GetPointDepth(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.GetPointPosition(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.BakeInterval = 0);
    }

    [Fact]
    public void SamplingAllocatesNothing()
    {
        var curve = QuarterCircle(10f);
        curve.GetBakedLength(); // bake
        var sum = 0f;
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var offset = i * 0.02f;
                sum += curve.SampleBaked(offset).X + curve.SampleBaked(offset, cubic: true).Z;
                sum += curve.SampleBakedWidth(offset) + curve.SampleBakedDepth(offset);
                sum += curve.GetClosestOffset(new Vector3(offset, 0, 5));
                sum += curve.SampleBakedWithRotation(offset).Origin.Y;
            }
        });
        Assert.Equal(0, allocated);
        Assert.True(float.IsFinite(sum));
    }

    /// <summary>
    /// The baking contract: a fixed curve bakes to the same bits on every OS and CPU (x64 and arm64). If this fails on
    /// one platform only, baking used an operation that rounds differently there.
    /// </summary>
    [Fact]
    public void FixedCurveBakesToACommittedHash()
    {
        var curve = new Curve3D { BakeInterval = 0.37f };
        curve.AddPoint(new Vector3(0, 4, 0), @out: new Vector3(3.1f, -0.2f, 1.7f));
        curve.AddPoint(new Vector3(12.5f, 3.2f, 6.25f), new Vector3(-2.5f, 0.3f, -3f), new Vector3(2.5f, -0.3f, 3f));
        curve.AddPoint(new Vector3(18, 1.5f, 21.75f), new Vector3(1.2f, 0.4f, -4.1f), new Vector3(-1.2f, -0.4f, 4.1f));
        curve.AddPoint(new Vector3(7.75f, 0.25f, 33), new Vector3(3.3f, 0.1f, -0.6f));
        curve.SetPointWidth(1, 3.3f);
        curve.SetPointDepth(2, 0.75f);
        curve.SetPointTilt(3, -0.4f);

        var hash = 14695981039346656037UL;
        void Mix(float value)
        {
            hash ^= (uint)BitConverter.SingleToInt32Bits(value);
            hash *= 1099511628211UL;
        }

        foreach (var p in curve.GetBakedPoints())
        {
            Mix(p.X);
            Mix(p.Y);
            Mix(p.Z);
        }

        foreach (var d in curve.GetBakedDistances())
            Mix(d);
        Mix(curve.SampleBakedWidth(10f));
        Mix(curve.SampleBakedDepth(20f));
        Mix(curve.SampleBakedTilt(30f));
        Assert.Equal(curve.GetBakedPoints().Length, curve.GetBakedDistances().Length);
        Assert.Equal(ExpectedHash, hash);
    }

    private const ulong ExpectedHash = 13233855097908824429UL;
}
