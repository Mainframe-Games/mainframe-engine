using System.Numerics;

namespace MainframeEngine.Tests.Terrain;

/// <summary>The procedural ground-cover meshes (ADR 0157): streams, winding, determinism.</summary>
public sealed class GrassMeshTests
{
    public static TheoryData<string> Plants => ["clump", "fern"];

    private static ArrayMesh Make(string kind, int seed = 3) => kind switch
    {
        "clump" => GrassMesh.Clump(blades: 12, height: 0.5f, width: 0.04f, bend: 0.4f, seed: seed),
        _ => GrassMesh.Fern(fronds: 6, length: 0.6f, width: 0.15f, leaflets: 8, seed: seed),
    };

    [Theory]
    [MemberData(nameof(Plants))]
    public void PlantsCarryTheFoliageStreams(string kind)
    {
        var surface = Make(kind).GetSurface(0);
        surface.Validate();
        Assert.True(surface.HasVertexStreams);
        Assert.Equal(surface.VertexCount, surface.Custom0.Length);
        Assert.Equal(surface.VertexCount, surface.Colors.Length);

        var top = surface.Positions.Max(p => p.Y);
        for (var i = 0; i < surface.VertexCount; i++)
        {
            var c = surface.Custom0[i];
            Assert.InRange(c.X, 0f, 1f);  // wind weight
            Assert.Equal(1f, c.Y);        // leaf flutter on
            Assert.InRange(c.Z, 0f, 1f);  // phase
            Assert.InRange(c.W, 0.3f, 1f); // AO
            Assert.InRange(surface.UVs[i].Y, 0f, 1f);
            Assert.InRange(surface.Colors[i].W, 1f, 1f);
            Assert.True(surface.Normals[i].Y > 0.5f, "plant normals lean towards up");
        }

        // Roots: no wind, darker; tips: full wind, UV v = 0.
        var roots = Enumerable.Range(0, surface.VertexCount).Where(i => surface.Custom0[i].X == 0f).ToList();
        var tips = Enumerable.Range(0, surface.VertexCount).Where(i => surface.Custom0[i].X >= 0.999f).ToList();
        Assert.NotEmpty(roots);
        Assert.NotEmpty(tips);
        Assert.All(roots, i => Assert.True(surface.Positions[i].Y < 0.01f));
        Assert.True(roots.Average(i => surface.Custom0[i].W) < tips.Average(i => surface.Custom0[i].W));
        Assert.All(tips, i => Assert.Equal(0f, surface.UVs[i].Y, 4));
        if (kind == "clump")
            Assert.All(tips, i => Assert.True(surface.Positions[i].Y > 0.4f * top));
    }

    [Theory]
    [MemberData(nameof(Plants))]
    public void TrianglesFaceTheirNormals(string kind)
    {
        var surface = Make(kind).GetSurface(0);
        var p = surface.Positions;
        var n = surface.Normals;
        var idx = surface.Indices;
        for (var k = 0; k < idx.Length; k += 3)
        {
            var face = Vector3.Cross(p[idx[k + 1]] - p[idx[k]], p[idx[k + 2]] - p[idx[k]]);
            Assert.True(Vector3.Dot(face, n[idx[k]] + n[idx[k + 1]] + n[idx[k + 2]]) >= 0f);
        }
    }

    [Fact]
    public void GeneratorsAreDeterministicPerSeed()
    {
        Assert.Equal(Make("clump").GetSurface(0).Positions, Make("clump").GetSurface(0).Positions);
        Assert.NotEqual(Make("clump").GetSurface(0).Positions, Make("clump", seed: 4).GetSurface(0).Positions);
        Assert.Equal(Make("fern").GetSurface(0).Custom0, Make("fern").GetSurface(0).Custom0);
        Assert.Equal(GrassMesh.Rock(seed: 2).GetSurface(0).Positions, GrassMesh.Rock(seed: 2).GetSurface(0).Positions);
        Assert.Equal(12 * 9, GrassMesh.Clump(blades: 12).GetSurface(0).VertexCount); // 4 segments: 8 side vertices + a tip
    }

    [Fact]
    public void RocksAreClosedOutwardFacingAndSitInTheGround()
    {
        var surface = GrassMesh.Rock(radius: 0.3f, seed: 5).GetSurface(0);
        surface.Validate();
        Assert.Empty(surface.Custom0);
        Assert.Equal(surface.VertexCount, surface.Colors.Length);
        // Closed and wound outwards: a positive signed volume, and nearly every face points away from the centre.
        var centre = surface.Bounds.Center;
        var p = surface.Positions;
        var idx = surface.Indices;
        var volume = 0f;
        var outward = 0;
        for (var k = 0; k < idx.Length; k += 3)
        {
            Vector3 a = p[idx[k]], b = p[idx[k + 1]], c = p[idx[k + 2]];
            volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - centre) > 0f)
                outward++;
        }

        Assert.True(volume > 0f);
        Assert.True(outward > 0.95f * idx.Length / 3, $"{outward} of {idx.Length / 3} faces point outwards");
        Assert.True(surface.Bounds.Min.Y < 0f);
        Assert.True(surface.Bounds.Max.Y > 0f);
        Assert.InRange(surface.Bounds.Size.X, 0.4f, 1.0f);
    }

    [Fact]
    public void TheGrassMaterialKeepsNormalsAndDoesNotCutOut()
    {
        var material = GrassMesh.CreateMaterial();
        Assert.False(material.AlphaCutout);
        Assert.Equal(FoliageBackFace.Keep, material.BackFace);
        Assert.Equal(ShadingMode.Pbr, material.ShadingMode);
    }
}
