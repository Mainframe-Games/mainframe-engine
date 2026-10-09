using System.Numerics;

namespace MainframeEngine.Tests.Water;

/// <summary>Ponds from the terrain's water layer: the per-chunk surfaces and the terrain as a water body.</summary>
public sealed class TerrainWaterTests : IDisposable
{
    private readonly SceneTree _tree = new(new ServerRegistry());
    private readonly Node3D _scene = new() { Name = "Scene" };

    public TerrainWaterTests()
    {
        _tree.ChangeScene(_scene);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _tree.Servers.Dispose();
    }

    private WaterQueries Water => _tree.Root.World3D.Water;

    // 32 m at 1 m, 16 m chunks; a 1.2 m deep pond (full water layer) for x, z in 4 … 10.
    private Terrain3D AddPondTerrain(Vector3 position = default)
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 1f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        data.SetHeightsFrom((_, _) => 3f);
        var terrain = new Terrain3D { Name = "Terrain", Data = data, Position = position };
        _scene.AddChild(terrain);
        terrain.SetWaterDepthFrom((x, z) => x is >= 4 and <= 10 && z is >= 4 and <= 10 ? 1.2f : 0f);
        return terrain;
    }

    [Fact]
    public void WetChunksGetAnUnsavedWaterSurfaceAtTheStoredHeight()
    {
        var terrain = AddPondTerrain();
        var water = Assert.IsType<MeshInstance3D>(terrain.GetChunkWater(0, 0));
        Assert.Null(terrain.GetChunkWater(1, 0));
        Assert.Null(terrain.GetChunkWater(1, 1));
        Assert.Null(water.Owner);
        Assert.Same(Terrain3D.DefaultWaterMaterial, water.MaterialOverride);
        Assert.False(water.CastShadows);

        var surface = water.Mesh!.GetSurface(0);
        var level = terrain.Data!.QuantizeHeight(3f) - TerrainWaterMesh.ShoreDrop;
        Assert.All(surface.Positions, p => Assert.Equal(level, p.Y, 1e-5f));
        // Wet vertices carry their column depth; the ring of dry shore vertices carries 0.
        Assert.Contains(surface.Custom0, c => MathF.Abs(c.X - 1.2f) < 1e-3f);
        Assert.Contains(surface.Custom0, c => c.X == 0f);
        // Triangles with any wet vertex: the wet 6 × 6 quads plus the ring around them; never a fully dry one.
        var indices = surface.Indices;
        for (var t = 0; t < indices.Length; t += 3)
            Assert.True(surface.Custom0[indices[t]].X > 0 || surface.Custom0[indices[t + 1]].X > 0 || surface.Custom0[indices[t + 2]].X > 0);
        Assert.InRange(indices.Length / 3, 6 * 6 * 2 + 1, 8 * 8 * 2);

        // Drying the layer frees the node.
        terrain.SetWaterDepthFrom((_, _) => 0f);
        Assert.Null(terrain.GetChunkWater(0, 0));
        Assert.DoesNotContain(terrain.Children, c => c.Name.StartsWith("Water", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTerrainAnswersWaterQueriesForItsPonds()
    {
        var terrain = AddPondTerrain(new Vector3(-16, 1, -16));
        Assert.Contains(terrain, Water.Bodies);

        var inside = new Vector3(-9, 0, -9); // terrain-local (7, 7)
        Assert.Equal(1.2f, Water.WaterDepthAt(inside), 1e-3f);
        Assert.Equal(1f + terrain.Data!.QuantizeHeight(3f), Water.SurfaceHeightAt(inside), 1e-4f); // origin + stored height
        Assert.Equal(Vector3.Zero, Water.FlowAt(inside));
        Assert.True(Water.IsUnderwater(new Vector3(-9, 3, -9)));
        Assert.True(float.IsNaN(Water.SurfaceHeightAt(new Vector3(10, 0, 10))));
        Assert.True(float.IsNaN(Water.SurfaceHeightAt(new Vector3(100, 0, 100))));
        var bounds = terrain.WaterBounds;
        Assert.InRange(bounds.Min.X, -13.01f, -11.99f);
        Assert.InRange(bounds.Max.X, -6.01f, -4.99f);

        _scene.RemoveChild(terrain);
        Assert.DoesNotContain(terrain, Water.Bodies);
        terrain.Free();
    }

    [Fact]
    public void ADryTerrainHasNoWaterBoundsOrSurfaces()
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 1f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        var terrain = new Terrain3D { Name = "Terrain", Data = data };
        _scene.AddChild(terrain);
        Assert.True(terrain.WaterBounds.IsEmpty);
        Assert.False(terrain.TrySample(new Vector3(5, 0, 5), out _));
        Assert.DoesNotContain(terrain.Children, c => c is MeshInstance3D);

        var custom = new WaterMaterial3D();
        terrain.WaterMaterial = custom;
        terrain.SetWaterDepthFrom((x, _) => x < 3 ? 0.5f : 0f);
        Assert.Same(custom, terrain.GetChunkWater(0, 0)!.MaterialOverride);
    }
}
