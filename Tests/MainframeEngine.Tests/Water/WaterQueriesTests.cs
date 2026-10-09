using System.Numerics;

namespace MainframeEngine.Tests.Water;

public sealed class WaterQueriesTests : IDisposable
{
    private readonly SceneTree _tree = new(new ServerRegistry());
    private readonly Node3D _scene = new() { Name = "Scene" };

    public WaterQueriesTests()
    {
        _tree.ChangeScene(_scene);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _tree.Servers.Dispose();
    }

    private WaterQueries Water => _tree.Root.World3D.Water;

    private River3D AddRiver(Curve3D curve, Vector3 position = default, float yawDegrees = 0)
    {
        var river = new River3D { Name = "River", Curve = curve, Position = position, RotationDegrees = new Vector3(0, yawDegrees, 0) };
        _scene.AddChild(river);
        return river;
    }

    [Fact]
    public void RiverRegistersAndBuildsAnUnownedRibbon()
    {
        var regenerated = 0;
        var river = new River3D { Name = "River", Curve = RiverBuilderTests.StraightCurve() };
        river.Regenerated += () => regenerated++;
        _scene.AddChild(river);

        Assert.Contains(river, Water.Bodies);
        var ribbon = Assert.IsType<MeshInstance3D>(river.Ribbon);
        Assert.Same(river, ribbon.Parent);
        Assert.Null(ribbon.Owner);
        Assert.Equal(river.MeshData.VertexCount, ribbon.Mesh!.VertexCount);
        Assert.Equal(AlphaMode.Blend, Assert.IsType<StandardMaterial3D>(ribbon.MaterialOverride).Transparency);
        Assert.Equal(1, regenerated);

        // A curve change regenerates at once.
        river.Curve!.SetPointPosition(1, new Vector3(0, 0, -30));
        Assert.Equal(2, regenerated);
        Assert.Equal(30f, river.Length, 2);
        Assert.Equal(river.MeshData.VertexCount, ribbon.Mesh!.VertexCount);

        _scene.RemoveChild(river);
        Assert.DoesNotContain(river, Water.Bodies);
        river.Free();
    }

    [Fact]
    public void SavingTheSceneSkipsTheRibbon()
    {
        var river = AddRiver(RiverBuilderTests.StraightCurve());
        river.Owner = _scene;
        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(_scene));
        Assert.DoesNotContain("Ribbon", json, StringComparison.Ordinal);
        Assert.Contains("River3D", json, StringComparison.Ordinal);
    }

    [Fact]
    public void QueriesInsideAStream()
    {
        AddRiver(RiverBuilderTests.StraightCurve(length: 20f, drop: 2f, width: 4f, depth: 1f));
        var p = new Vector3(0, -1.5f, -10);
        Assert.True(Water.TrySample(p, out var sample));
        Assert.Equal(-1f, sample.SurfaceHeight, 2);
        Assert.Equal(1f, Water.WaterDepthAt(p), 3);
        Assert.Equal(-1f, Water.SurfaceHeightAt(p), 2);
        Assert.True(Water.FlowAt(p).Z < -0.3f);
        Assert.Equal(0.5f, Water.ImmersionAt(p), 2);
        Assert.True(Water.IsUnderwater(p));
        Assert.False(Water.IsUnderwater(p + new Vector3(0, 0.6f, 0)));
        Assert.True(Water.ImmersionAt(p + new Vector3(0, 2, 0)) < 0);
    }

    [Fact]
    public void QueriesOutsideAreDry()
    {
        AddRiver(RiverBuilderTests.StraightCurve(width: 4f));
        var dry = new Vector3(5, 0, -10);
        Assert.False(Water.TrySample(dry, out _));
        Assert.Equal(0f, Water.WaterDepthAt(dry));
        Assert.True(float.IsNaN(Water.SurfaceHeightAt(dry)));
        Assert.Equal(Vector3.Zero, Water.FlowAt(dry));
        Assert.Equal(0f, Water.ImmersionAt(dry));
        Assert.False(Water.IsUnderwater(dry));
        // The edge: just inside and just outside the half width.
        Assert.True(Water.TrySample(new Vector3(1.95f, 0, -10), out _));
        Assert.False(Water.TrySample(new Vector3(2.05f, 0, -10), out _));
        Assert.False(Water.TrySample(new Vector3(500, 0, 500), out _));
    }

    [Fact]
    public void QueriesFollowTheRiversTransform()
    {
        // Turned 90° about Y: flowing along −X; moved to (100, 2, 0).
        AddRiver(RiverBuilderTests.StraightCurve(length: 20f, width: 4f, depth: 0.5f), new Vector3(100, 2, 0), 90f);
        Assert.True(Water.TrySample(new Vector3(90, 0, 0), out var sample));
        Assert.Equal(2f, sample.SurfaceHeight, 3);
        Assert.Equal(0.5f, sample.ColumnDepth, 3);
        Assert.True(sample.Flow.X < 0);
        Assert.Equal(0f, sample.Flow.Z, 3);
        Assert.False(Water.TrySample(new Vector3(0, 0, -10), out _));
        Assert.InRange(Water.Bodies[0].WaterBounds.Min.X, 79f, 81f);
    }

    [Fact]
    public void BendsHaveNoGapsOnTheOuterBank()
    {
        var curve = new Curve3D();
        curve.AddPoint(Vector3.Zero, @out: new Vector3(0, 0, -5.5f));
        curve.AddPoint(new Vector3(10, 0, -10), new Vector3(-5.5f, 0, 0));
        curve.SetPointWidth(0, 3f);
        curve.SetPointWidth(1, 3f);
        curve.SetPointDepth(0, 0.5f);
        curve.SetPointDepth(1, 0.5f);
        var river = AddRiver(curve);
        // Points 1.4 m outside the centreline all along the bend (the outer bank is +X then... the arc's outside).
        var length = river.Length;
        for (var s = 0.25f; s < length - 0.25f; s += 0.25f)
        {
            var t = curve.SampleBakedWithRotation(s);
            var right = t.Basis.X;
            var centre = t.Origin;
            Assert.True(Water.TrySample(centre + right * 1.4f, out _), $"gap right of {s:0.00} m");
            Assert.True(Water.TrySample(centre - right * 1.4f, out _), $"gap left of {s:0.00} m");
            Assert.False(Water.TrySample(centre + right * 1.7f, out _), $"water right of the bank at {s:0.00} m");
        }
    }

    [Fact]
    public void TheHighestSurfaceWinsOnOverlap()
    {
        var queries = new WaterQueries();
        queries.Register(new FlatWater(1f, 2f));
        queries.Register(new FlatWater(3f, 0.5f));
        queries.Register(new FlatWater(2f, 1f));
        Assert.Equal(3f, queries.SurfaceHeightAt(Vector3.Zero));
        Assert.Equal(0.5f, queries.WaterDepthAt(Vector3.Zero));
        Assert.Equal(3, queries.Bodies.Count);
        queries.Register(queries.Bodies[0]);
        Assert.Equal(3, queries.Bodies.Count);
    }

    [Theory]
    [InlineData(-1f, 1f)]
    [InlineData(0f, 1f)]
    [InlineData(0.3f, 1f)]
    [InlineData(0.75f, 0.7f)]
    [InlineData(1.2f, 0.4f)]
    [InlineData(3f, 0.4f)]
    public void WadeSpeedScaleEasesFromOneToMinScale(float immersion, float expected) =>
        Assert.Equal(expected, WaterQueries.WadeSpeedScale(immersion), 4);

    [Fact]
    public void QueriesAllocateNothing()
    {
        AddRiver(RiverBuilderTests.StraightCurve(length: 200f, drop: 4f));
        var queries = Water;
        var sum = 0f;
        queries.TrySample(Vector3.Zero, out _); // warm up
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                var p = new Vector3(i % 7 - 3, 0, -i * 0.1f);
                sum += queries.WaterDepthAt(p) + queries.ImmersionAt(p) + queries.FlowAt(p).Z;
                sum += queries.IsUnderwater(p) ? 1 : 0;
                sum += WaterQueries.WadeSpeedScale(queries.ImmersionAt(p));
            }
        });
        Assert.Equal(0, allocated);
        Assert.True(float.IsFinite(sum));
    }

    private sealed class FlatWater(float surface, float depth) : IWaterBody3D
    {
        public Aabb WaterBounds => new(new Vector3(-10, surface - depth, -10), new Vector3(10, surface, 10));

        public bool TrySample(Vector3 position, out WaterSample sample)
        {
            sample = new WaterSample(surface, depth, Vector3.Zero);
            return true;
        }
    }
}
