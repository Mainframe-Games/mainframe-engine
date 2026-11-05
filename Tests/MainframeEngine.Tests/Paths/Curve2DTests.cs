using System.Numerics;

namespace MainframeEngine.Tests.Paths;

/// <summary><see cref="Curve2D"/> (G13): Godot's API, closed curves, tangents, rotation, helpers and the bake contract.</summary>
public sealed class Curve2DTests
{
    private static Curve2D Square(bool closed)
    {
        var curve = new Curve2D { BakeInterval = 1f, Closed = closed };
        curve.AddPoint(new Vector2(0, 0));
        curve.AddPoint(new Vector2(100, 0));
        curve.AddPoint(new Vector2(100, 100));
        curve.AddPoint(new Vector2(0, 100));
        return curve;
    }

    [Fact]
    public void PointsAddRemoveAndRead()
    {
        var curve = new Curve2D();
        curve.AddPoint(new Vector2(1, 2), new Vector2(-3, 0), new Vector2(3, 0));
        curve.AddPoint(new Vector2(10, 2));
        curve.AddPoint(new Vector2(5, 5), index: 1);
        Assert.Equal(3, curve.PointCount);
        Assert.Equal(new Vector2(5, 5), curve.GetPointPosition(1));
        Assert.Equal(new Vector2(-3, 0), curve.GetPointIn(0));
        Assert.Equal(new Vector2(3, 0), curve.GetPointOut(0));
        curve.SetPointOut(2, new Vector2(1, 1));
        Assert.Equal(new Vector2(1, 1), curve.GetPointOut(2));
        curve.RemovePoint(1);
        Assert.Equal(new Vector2(10, 2), curve.GetPointPosition(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.GetPointPosition(2));
        curve.ClearPoints();
        Assert.Equal(0, curve.PointCount);
        Assert.Equal(0f, curve.GetBakedLength());
        Assert.Throws<ArgumentException>(() => curve.Points = new float[5]);
    }

    [Fact]
    public void ChangesRaiseChangedAndRebake()
    {
        var curve = Square(closed: false);
        var changes = 0;
        curve.Changed += () => changes++;
        Assert.Equal(300f, curve.GetBakedLength(), 2);
        curve.SetPointPosition(3, new Vector2(0, 200));
        Assert.Equal(1, changes);
        Assert.Equal(200f + 100f * MathF.Sqrt(2), curve.GetBakedLength(), 1);
    }

    [Fact]
    public void SampleFollowsTheBezierAndClampsIndices()
    {
        var curve = new Curve2D();
        curve.AddPoint(Vector2.Zero, @out: new Vector2(0, 50));
        curve.AddPoint(new Vector2(100, 0), new Vector2(0, 50));
        var mid = curve.Sample(0, 0.5f);
        Assert.Equal(50f, mid.X, 3);
        Assert.Equal(37.5f, mid.Y, 3); // 3 × 0.125 × 50 + 3 × 0.125 × 50
        Assert.Equal(Vector2.Zero, curve.Sample(-1, 0.5f));
        Assert.Equal(new Vector2(100, 0), curve.Sample(1, 0.5f));
    }

    [Fact]
    public void AClosedCurveAddsTheSeamSegment()
    {
        var open = Square(closed: false);
        var closed = Square(closed: true);
        Assert.Equal(3, open.SegmentCount);
        Assert.Equal(4, closed.SegmentCount);
        Assert.Equal(400f, closed.GetBakedLength(), 2);
        Assert.Equal(new Vector2(0, 50), closed.Sample(3, 0.5f));
        Assert.Equal(new Vector2(0, 50), closed.SampleBaked(350f));
    }

    [Fact]
    public void WrapOffsetWrapsClosedAndClampsOpenCurves()
    {
        Assert.Equal(300f, Square(closed: false).WrapOffset(450f), 2);
        Assert.Equal(0f, Square(closed: false).WrapOffset(-5f));
        Assert.Equal(50f, Square(closed: true).WrapOffset(450f), 2);
        Assert.Equal(390f, Square(closed: true).WrapOffset(-10f), 2);
    }

    [Fact]
    public void TangentsAndRotationPointAlongTheCurve()
    {
        var curve = Square(closed: false);
        Assert.Equal(1f, curve.SampleBakedTangent(50f).X, 3);
        Assert.Equal(1f, curve.SampleBakedTangent(150f).Y, 3);
        var transform = curve.SampleBakedWithRotation(150f);
        Assert.Equal(new Vector2(100, 50), transform.Origin);
        Assert.Equal(MathF.PI / 2, transform.Rotation, 3);           // +X along the tangent (down)
        Assert.Equal(-1f, transform.Y.X, 3);                          // +Y 90° clockwise on screen
        Assert.Equal(Transform2D.Identity.X, new Curve2D().SampleBakedWithRotation(0).X);
    }

    [Fact]
    public void SmoothPointsPassThroughEveryPointAndTangentsAreContinuousAcrossTheSeam()
    {
        Vector2[] points = [new(0, 0), new(200, -50), new(300, 150), new(50, 200)];
        var curve = new Curve2D { BakeInterval = 0.5f };
        curve.SetSmoothPoints(points, closed: true);
        Assert.True(curve.Closed);
        foreach (var p in points)
            Assert.True(Vector2.Distance(curve.GetClosestPoint(p), p) < 0.05f, $"misses {p}");

        var length = curve.GetBakedLength();
        var previous = curve.SampleBakedTangent(length - 2f);
        for (var d = -1f; d <= 4f; d += 0.5f)
        {
            var tangent = curve.SampleBakedTangent(curve.WrapOffset(d));
            Assert.True(Vector2.Dot(tangent, previous) > 0.99f, $"tangent jumps at {d}");
            previous = tangent;
        }
    }

    [Fact]
    public void ReverseKeepsTheShapeAndTwiceIsTheIdentity()
    {
        var curve = new Curve2D { BakeInterval = 1f };
        curve.SetSmoothPoints([new(0, 0), new(100, 40), new(180, -20), new(260, 60)], closed: false);
        var before = (float[])curve.Points.Clone();
        var length = curve.GetBakedLength();
        var start = curve.SampleBaked(0);
        curve.Reverse();
        Assert.Equal(length, curve.GetBakedLength(), 2);
        Assert.True(Vector2.Distance(start, curve.SampleBaked(length)) < 1e-3f);
        Assert.True(Vector2.Distance(curve.SampleBaked(length * 0.3f), Clone(before).SampleBaked(length * 0.7f)) < 0.5f);
        curve.Reverse();
        Assert.Equal(before, curve.Points);
    }

    private static Curve2D Clone(float[] points) => new() { BakeInterval = 1f, Points = (float[])points.Clone() };

    [Fact]
    public void ClosestOffsetProjectsOnTheBakedCurve()
    {
        var curve = Square(closed: false);
        Assert.Equal(150f, curve.GetClosestOffset(new Vector2(130, 50)), 2);
        Assert.Equal(new Vector2(100, 50), curve.GetClosestPoint(new Vector2(130, 50)));
    }

    [Fact]
    public void RoundTripsThroughAScene()
    {
        var curve = Square(closed: true);
        curve.BakeInterval = 2.5f;
        var root = new Node2D { Name = "Root" };
        var path = new Path2D { Name = "Path", Curve = curve };
        root.AddChild(path);
        path.Owner = root;
        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetNode<Path2D>("Path").Curve!;
        Assert.True(copy.Closed);
        Assert.Equal(2.5f, copy.BakeInterval);
        Assert.Equal(curve.Points, copy.Points);
    }

    [Fact]
    public void SamplingAllocatesNothing()
    {
        var curve = new Curve2D();
        curve.SetSmoothPoints([new(0, 0), new(300, 50), new(300, 300), new(0, 250)], closed: true);
        curve.GetBakedLength();
        var sum = 0f;
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var offset = i * 7f;
                sum += curve.SampleBaked(offset).X + curve.SampleBaked(offset, cubic: true).Y;
                sum += curve.SampleBakedTangent(offset).X + curve.SampleBakedWithRotation(offset).Origin.Y;
                sum += curve.GetClosestOffset(new Vector2(offset, 20)) + curve.WrapOffset(offset);
            }
        });
        Assert.Equal(0, allocated);
        Assert.True(float.IsFinite(sum));
    }

    /// <summary>The baking contract (as <c>Curve3DTests.FixedCurveBakesToACommittedHash</c>): the same bits on every CPU.</summary>
    [Fact]
    public void FixedCurveBakesToACommittedHash()
    {
        var curve = new Curve2D { BakeInterval = 3.7f };
        curve.AddPoint(new Vector2(0, 40), @out: new Vector2(31, -2));
        curve.AddPoint(new Vector2(125, 32), new Vector2(-25, 3), new Vector2(25, -3));
        curve.AddPoint(new Vector2(180, 215), new Vector2(12, -41), new Vector2(-12, 41));
        curve.AddPoint(new Vector2(77.5f, 330), new Vector2(33, -6));

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
        }

        foreach (var d in curve.GetBakedDistances())
            Mix(d);
        Assert.Equal(ExpectedHash, hash);
    }

    private const ulong ExpectedHash = 9442394276991243827UL;
}
