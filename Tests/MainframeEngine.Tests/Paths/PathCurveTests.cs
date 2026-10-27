using System.Numerics;

namespace MainframeEngine.Tests.Paths;

/// <summary><see cref="Curve3D"/>'s path additions (ADR 0215): closed curves, tangents, up vectors, helpers.</summary>
public sealed class PathCurveTests
{
    private static readonly Vector3[] Square = [new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(0, 0, 10)];

    private static Curve3D Polygon(ReadOnlySpan<Vector3> corners, bool closed)
    {
        var curve = new Curve3D { BakeInterval = 0.1f };
        foreach (var corner in corners)
            curve.AddPoint(corner);
        curve.Closed = closed;
        return curve;
    }

    [Fact]
    public void AClosedCurveAddsTheSeamSegment()
    {
        var open = Polygon(Square, closed: false);
        var closed = Polygon(Square, closed: true);
        Assert.Equal(30f, open.GetBakedLength(), 3);
        Assert.Equal(40f, closed.GetBakedLength(), 3);
        Assert.Equal(3, open.SegmentCount);
        Assert.Equal(4, closed.SegmentCount);
        Assert.Equal(new Vector3(0, 0, 5), closed.Sample(3, 0.5f));
        Assert.Equal(closed.GetBakedPoints()[0], closed.GetBakedPoints()[^1]);
    }

    [Fact]
    public void WrapOffsetWrapsClosedAndClampsOpenCurves()
    {
        var open = Polygon(Square, closed: false);
        var closed = Polygon(Square, closed: true);
        Assert.Equal(30f, open.WrapOffset(45f), 3);
        Assert.Equal(0f, open.WrapOffset(-5f));
        Assert.Equal(5f, closed.WrapOffset(45f), 3);
        Assert.Equal(35f, closed.WrapOffset(-5f), 3);
        Assert.Equal(0f, new Curve3D().WrapOffset(3f));
    }

    [Fact]
    public void SmoothPointsPassThroughEveryPointAndTangentsAreContinuousAcrossTheSeam()
    {
        var curve = new Curve3D { BakeInterval = 0.05f };
        curve.SetSmoothPoints(Square, closed: true);
        Assert.True(curve.Closed);
        for (var i = 0; i < Square.Length; i++)
            Assert.Equal(Square[i], curve.Sample(i, 0f));

        var length = curve.GetBakedLength();
        var previous = curve.SampleBakedTangent(0);
        var worst = 0f;
        for (var d = 0.05f; d <= length; d += 0.05f)
        {
            var tangent = curve.SampleBakedTangent(d);
            worst = MathF.Max(worst, MathF.Acos(Math.Clamp(Vector3.Dot(previous, tangent), -1f, 1f)));
            previous = tangent;
        }

        Assert.True(worst < 0.05f, $"tangent jumps by {worst} rad");
        var start = curve.SampleBakedTangent(0);
        var end = curve.SampleBakedTangent(length);
        Assert.True(Vector3.Dot(start, end) > 0.9999f);
    }

    [Fact]
    public void RotationNoLongerStepsBetweenBakedPoints()
    {
        var curve = new Curve3D { BakeInterval = 2f };
        curve.SetSmoothPoints([new(0, 0, 0), new(20, 0, 0), new(20, 0, 20)], closed: false);
        var a = -curve.SampleBakedWithRotation(9.9f).Basis.Z;
        var b = -curve.SampleBakedWithRotation(10.1f).Basis.Z;
        Assert.True(Vector3.Dot(a, b) > 0.999f);
    }

    [Fact]
    public void UpVectorsStayPerpendicularAndNeverFlipOnAVerticalLoop()
    {
        // A loop-the-loop in the XY plane: world up is parallel to the tangent at the sides.
        var loop = new Curve3D { BakeInterval = 0.1f, UpVectorEnabled = true };
        const int n = 16;
        var points = new Vector3[n];
        for (var i = 0; i < n; i++)
        {
            var a = i * MathF.Tau / n;
            points[i] = new Vector3(10 * MathF.Sin(a), 10 - 10 * MathF.Cos(a), i * 0.2f);
        }

        loop.SetSmoothPoints(points, closed: false);
        var length = loop.GetBakedLength();
        var previousUp = loop.SampleBakedUpVector(0);
        for (var d = 0f; d <= length; d += 0.1f)
        {
            var up = loop.SampleBakedUpVector(d);
            Assert.Equal(0f, Vector3.Dot(up, loop.SampleBakedTangent(d)), 3);
            Assert.Equal(1f, up.Length(), 3);
            Assert.True(Vector3.Dot(up, previousUp) > 0.99f, $"up flips at {d}");
            previousUp = up;
        }
    }

    [Fact]
    public void ClosedUpVectorsMatchAtTheSeam()
    {
        // A non-planar closed loop (a saddle), whose rotation-minimising frame does not close by itself.
        var curve = new Curve3D { BakeInterval = 0.1f, UpVectorEnabled = true };
        curve.SetSmoothPoints([new(0, 0, 0), new(10, 4, 0), new(10, 0, 10), new(0, 4, 10)], closed: true);
        var start = curve.SampleBakedUpVector(0);
        var end = curve.SampleBakedUpVector(curve.GetBakedLength());
        Assert.True(Vector3.Dot(start, end) > 0.999f);
    }

    [Fact]
    public void ClosedAndUpVectorRoundTripThroughAScene()
    {
        var curve = Polygon(Square, closed: true);
        curve.UpVectorEnabled = true;
        var root = new Node3D { Name = "Root" };
        var path = new Path3D { Name = "Path", Curve = curve };
        root.AddChild(path);
        path.Owner = root;
        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetNode<Path3D>("Path").Curve!;
        Assert.True(copy.Closed);
        Assert.True(copy.UpVectorEnabled);
        Assert.Equal(curve.Points, copy.Points);
        Assert.Equal(40f, copy.GetBakedLength(), 3);
    }

    [Fact]
    public void PathSamplingAllocatesNothing()
    {
        var curve = new Curve3D { UpVectorEnabled = true };
        curve.SetSmoothPoints(Square, closed: true);
        curve.GetBakedLength();
        var sum = 0f;
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var offset = curve.WrapOffset(i * 0.07f);
                sum += curve.SampleBakedTangent(offset).X + curve.SampleBakedUpVector(offset, applyTilt: true).Y;
                sum += curve.SampleBakedWithRotation(offset, cubic: true).Origin.Z;
            }
        });
        Assert.Equal(0, allocated);
        Assert.True(float.IsFinite(sum));
    }
}
