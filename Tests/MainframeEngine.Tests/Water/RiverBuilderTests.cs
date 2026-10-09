using System.Numerics;

namespace MainframeEngine.Tests.Water;

public sealed class RiverBuilderTests
{
    /// <summary>A straight river flowing along −Z from the origin, dropping <paramref name="drop"/> metres.</summary>
    internal static Curve3D StraightCurve(float length = 20f, float drop = 0f, float width = 4f, float depth = 0.8f)
    {
        var curve = new Curve3D();
        var end = new Vector3(0, -drop, -length);
        curve.AddPoint(Vector3.Zero, @out: end / 3);
        curve.AddPoint(end, @in: -end / 3);
        for (var i = 0; i < 2; i++)
        {
            curve.SetPointWidth(i, width);
            curve.SetPointDepth(i, depth);
        }

        return curve;
    }

    private static RiverBuilder Build(Curve3D curve, RiverSettings? settings = null)
    {
        var builder = new RiverBuilder();
        builder.Build(curve, settings ?? RiverSettings.Default);
        return builder;
    }

    [Fact]
    public void SectionsAreOneMetreApartWithFiveVerticesEach()
    {
        var builder = Build(StraightCurve());
        Assert.Equal(21, builder.SectionCount);
        Assert.Equal(21 * 5, builder.Mesh.VertexCount);
        Assert.Equal(20 * 4 * 6, builder.Mesh.IndexCount);
        Assert.Equal(20f, builder.Length, 3);
        Assert.Equal(builder.Mesh.VertexCount, builder.Mesh.Custom0.Length);
        Assert.Equal(builder.Mesh.VertexCount, builder.Mesh.UVs.Length);
    }

    [Fact]
    public void EmptyCurvesBuildAnEmptyRiver()
    {
        Assert.Equal(0, Build(new Curve3D()).Mesh.VertexCount);
        var builder = new RiverBuilder();
        builder.Build(null, RiverSettings.Default);
        Assert.Equal(0, builder.SectionCount);
        Assert.False(builder.TrySample(Vector3.Zero, out _, out _));
    }

    [Fact]
    public void UvsRunAcrossAndAlongTheArcLength()
    {
        var settings = RiverSettings.Default;
        settings.UvLength = 4f;
        var mesh = Build(StraightCurve(), settings).Mesh;
        for (var k = 0; k < 21; k++)
            for (var j = 0; j < 5; j++)
            {
                var uv = mesh.UVs[k * 5 + j];
                Assert.Equal(j / 4f, uv.X, 5);
                Assert.Equal(k / 4f, uv.Y, 3);
            }
    }

    [Fact]
    public void NormalsPointUpAndTrianglesFaceUp()
    {
        var curve = new Curve3D();
        curve.AddPoint(Vector3.Zero, @out: new Vector3(0, 0, -6));
        curve.AddPoint(new Vector3(10, -1, -10), new Vector3(-6, 0, 0)); // a bend
        var mesh = Build(curve).Mesh;
        Assert.All(mesh.Normals, n => Assert.Equal(Vector3.UnitY, n));
        for (var i = 0; i < mesh.IndexCount; i += 3)
        {
            var a = mesh.Positions[mesh.Indices[i]];
            var b = mesh.Positions[mesh.Indices[i + 1]];
            var c = mesh.Positions[mesh.Indices[i + 2]];
            Assert.True(Vector3.Cross(b - a, c - a).Y > 0, $"triangle {i / 3} faces down");
        }
    }

    [Fact]
    public void LeftBankIsUZeroFacingDownstream()
    {
        var mesh = Build(StraightCurve(width: 4f)).Mesh;
        // Flowing along −Z, the left bank is −X.
        Assert.Equal(-2f, mesh.Positions[0].X, 4);
        Assert.Equal(2f, mesh.Positions[4].X, 4);
        Assert.Equal(0f, mesh.Positions[2].X, 4);
    }

    [Fact]
    public void SurfaceNeverRisesDownstreamWhenEnforced()
    {
        var curve = new Curve3D();
        curve.AddPoint(Vector3.Zero, @out: new Vector3(0, 0, -3));
        curve.AddPoint(new Vector3(0, -1, -10), new Vector3(0, 0, 3), new Vector3(0, 0, -3));
        curve.AddPoint(new Vector3(0, 0.5f, -20), new Vector3(0, 0, 3)); // rises 1.5 m
        var clamped = Build(curve);
        var centres = clamped.SectionCentres;
        for (var k = 1; k < centres.Length; k++)
            Assert.True(centres[k].Y <= centres[k - 1].Y, $"section {k} rises");
        Assert.Equal(-1f, centres[^1].Y, 2);
        Assert.Equal(0.5f, curve.GetPointPosition(2).Y); // the curve itself is unchanged

        var settings = RiverSettings.Default;
        settings.EnforceDownhill = false;
        Assert.Equal(0.5f, Build(curve, settings).SectionCentres[^1].Y, 3);
    }

    [Theory]
    [InlineData(0.00f, 0.3f)]   // flat: MinSpeed
    [InlineData(0.01f, 0.9f)]   // 1 %: 0.3 + 6 × 0.1
    [InlineData(0.10f, 2.19f)]  // 10 %: 0.3 + 6 × 0.316 (the drop is measured per metre of arc, not of run)
    [InlineData(1.00f, 4.0f)]   // steep: capped at MaxSpeed
    public void SpeedFollowsTheSquareRootOfTheSlope(float slope, float expected)
    {
        var builder = Build(StraightCurve(length: 100f, drop: 100f * slope));
        var middle = builder.SectionSpeeds[builder.SectionCount / 2];
        Assert.Equal(expected, middle, 1);
        var perArc = slope / MathF.Sqrt(1 + slope * slope);
        Assert.Equal(Math.Min(4f, 0.3f + 6f * MathF.Sqrt(perArc)), middle, 3);
    }

    [Fact]
    public void Custom0CarriesColumnDepthFlowAndFoam()
    {
        var builder = Build(StraightCurve(length: 100f, drop: 10f, depth: 0.8f));
        var mesh = builder.Mesh;
        var k = builder.SectionCount / 2;
        var speed = builder.SectionSpeeds[k];
        var centre = mesh.Custom0[k * 5 + 2];
        var bank = mesh.Custom0[k * 5];
        var between = mesh.Custom0[k * 5 + 1]; // x = −0.5
        Assert.Equal(0.8f, centre.X, 4);
        Assert.Equal(0f, bank.X, 4);
        Assert.Equal(0.8f * 0.75f, between.X, 4);
        // Flow along the tangent (−Z), 0.4 × speed at the banks.
        Assert.Equal(0f, centre.Y, 4);
        Assert.Equal(-speed, centre.Z, 4);
        Assert.Equal(-speed * 0.4f, bank.Z, 4);
        Assert.Equal(-speed * (1f - 0.6f * 0.25f), between.Z, 4);
        // 10 % slope: ≈ 2.2 m/s, just into rapids, so a little foam.
        Assert.InRange(centre.W, 0.01f, 0.2f);
        Assert.Equal(0f, Build(StraightCurve()).Mesh.Custom0[2].W);
    }

    [Fact]
    public void ArraysAreReusedWhileTheSectionCountHolds()
    {
        var curve = StraightCurve();
        var builder = Build(curve);
        var positions = builder.Mesh.Positions;
        curve.SetPointWidth(1, 6f);
        builder.Build(curve, RiverSettings.Default);
        Assert.Same(positions, builder.Mesh.Positions);
        curve.SetPointPosition(1, new Vector3(0, 0, -30));
        builder.Build(curve, RiverSettings.Default);
        Assert.NotSame(positions, builder.Mesh.Positions);
    }

    [Fact]
    public void SamplesInsideFollowTheLateralProfile()
    {
        var builder = Build(StraightCurve(length: 20f, drop: 2f, width: 4f, depth: 1f));
        Assert.True(builder.TrySample(new Vector3(0, 0, -10), out var centre, out var offset));
        Assert.Equal(builder.Length / 2, offset, 2); // arc length: the 2 m drop makes it 20.1 m
        Assert.Equal(-1f, centre.SurfaceHeight, 2);
        Assert.Equal(1f, centre.ColumnDepth, 3);
        Assert.True(centre.Flow.Z < 0);
        Assert.True(builder.TrySample(new Vector3(1.9f, 0, -10), out var edge, out _));
        Assert.InRange(edge.ColumnDepth, 0f, 0.1f);
        Assert.False(builder.TrySample(new Vector3(2.1f, 0, -10), out _, out _));
        Assert.False(builder.TrySample(new Vector3(0, 0, 0.5f), out _, out _));
        Assert.False(builder.TrySample(new Vector3(0, 0, -20.5f), out _, out _));
    }
}
