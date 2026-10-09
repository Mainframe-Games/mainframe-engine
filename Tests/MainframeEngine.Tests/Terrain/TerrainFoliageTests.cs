using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Terrain;

/// <summary>Terrain foliage scatter (ADR 0157): deterministic placement, density, exclusions, rebuilds and thinning.</summary>
public sealed class TerrainFoliageTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private static TerrainData Data(int size = 32, float spacing = 0.5f, int chunk = 16, Func<float, float, float>? heights = null)
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, size, spacing, chunk);
        data.CollisionMode = TerrainCollisionMode.None;
        data.SetHeightsFrom(heights ?? ((x, z) => 0.05f * x + 0.03f * z));
        return data;
    }

    internal static FoliageType Grass(float density = 4f, int subdivisions = 1) => new()
    {
        Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.4f, 0.1f) },
        Density = density,
        Subdivisions = subdivisions,
        CullDistance = 40f,
        ThinBand = 20f,
    };

    private Terrain3D AddTerrain(TerrainData data, Vector3 position = default)
    {
        var terrain = _h.Add(new Terrain3D { Name = "Terrain", Data = data, Position = position });
        terrain.Owner = _h.Scene;
        return terrain;
    }

    // Every instance's terrain-local position, from every tile of type 0.
    private static List<Vector3> Positions(Terrain3D terrain, int type = 0)
    {
        var foliage = terrain.Foliage!;
        var n = foliage.GetTilesPerSide(type);
        var result = new List<Vector3>();
        for (var tz = 0; tz < n; tz++)
            for (var tx = 0; tx < n; tx++)
                if (foliage.GetTileNode(type, tx, tz) is { } node)
                    foreach (var t in node.Multimesh!.Transforms)
                        result.Add(t.Origin + node.Position);
        return result;
    }

    [Fact]
    public void PlacementIsDeterministicAndIndependentOfTheTiling()
    {
        var a = Data();
        a.FoliageTypes = [Grass()];
        var b = Data();
        b.FoliageTypes = [Grass()];
        var c = Data();
        c.FoliageTypes = [Grass(subdivisions: 4)];
        var first = AddTerrain(a);
        var second = AddTerrain(b, new Vector3(100, 0, 0));
        var tiled = AddTerrain(c);

        var p1 = Positions(first);
        var p2 = Positions(second);
        Assert.True(p1.Count > 3000, $"only {p1.Count} instances");
        Assert.Equal(p1, p2); // bit-identical, wherever the terrain is

        // The same points whatever the tile size: each grid point (one per 0.5 m cell at full jitter) is in exactly one tile.
        static (int, int) Cell(Vector3 v) => ((int)MathF.Floor(v.X / 0.5f), (int)MathF.Floor(v.Z / 0.5f));
        var byCell = p1.ToDictionary(Cell);
        var p3 = Positions(tiled);
        Assert.Equal(p1.Count, p3.Count);
        foreach (var p in p3)
            Assert.True(Vector3.Distance(byCell[Cell(p)], p) < 1e-4f, $"{p} differs from {byCell[Cell(p)]}");

        // A different seed moves them.
        a.FoliageTypes[0].Seed = 7;
        _h.Run(1);
        Assert.NotEqual(p1, Positions(first));
    }

    [Fact]
    public void InstancesStandOnTheGroundInsideTheMap()
    {
        var data = Data(heights: TerrainDataTests.Hills);
        var type = Grass();
        type.SinkMeters = 0f;
        type.AlignToNormal = 0.5f;
        data.FoliageTypes = [type];
        var terrain = AddTerrain(data, new Vector3(-16, 2, -16));
        var positions = Positions(terrain);
        Assert.NotEmpty(positions);
        foreach (var p in positions)
        {
            Assert.InRange(p.X, 0f, 32f);
            Assert.InRange(p.Z, 0f, 32f);
            var world = p + terrain.GlobalPosition;
            Assert.Equal(terrain.HeightAt(world.X, world.Z), world.Y, 3);
        }
    }

    [Fact]
    public void DensityFollowsTheMaskedSplatWeights()
    {
        var data = Data();
        // Layer 1 at half weight everywhere on the left half, layer 2 alone on the right half.
        data.SetWeightsFrom((x, _, w) =>
        {
            if (x < 16f)
            {
                w[0] = 1f;
                w[1] = 1f;
            }
            else
            {
                w[2] = 1f;
            }
        });
        var everywhere = Grass();
        var onLayer1 = Grass();
        onLayer1.LayerMask = 1u << 1;
        var onLayers1And2 = Grass();
        onLayers1And2.LayerMask = (1u << 1) | (1u << 2);
        data.FoliageTypes = [everywhere, onLayer1, onLayers1And2];
        var terrain = AddTerrain(data);

        var all = Positions(terrain, 0);
        Assert.Equal(64 * 64, all.Count); // 4 per m² over 32 × 32 m, every point grows

        var layer1 = Positions(terrain, 1);
        Assert.All(layer1, p => Assert.True(p.X < 16.5f)); // jitter stays inside its 0.5 m grid cell
        Assert.InRange(layer1.Count, 2048 * 0.5f * 0.94f, 2048 * 0.5f * 1.06f); // half weight over half the map

        var both = Positions(terrain, 2);
        Assert.InRange(both.Count(p => p.X >= 16f), 2048 * 0.97f, 2048 * 1.03f);
        Assert.InRange(both.Count(p => p.X < 16f), 1024 * 0.9f, 1024 * 1.1f);
    }

    [Fact]
    public void NothingGrowsInWaterOnSteepSlopesOrOutsideTheHeightLimits()
    {
        var data = Data(heights: (x, _) => x < 16 ? 0f : (x - 16) * 2f); // flat, then a 63° ramp
        data.SetWaterDepthFrom((_, z) => z < 8 ? 0.5f : 0f);
        data.FoliageTypes = [Grass()];
        var terrain = AddTerrain(data);
        var positions = Positions(terrain);
        Assert.NotEmpty(positions);
        foreach (var p in positions)
        {
            Assert.True(p.Z > 7.5f, $"an instance at z = {p.Z} stands in water");
            Assert.True(p.X < 16.6f, $"an instance at x = {p.X} stands on the ramp");
        }

        var limited = Grass();
        limited.HeightMin = -1f;
        limited.HeightMax = 0.5f;
        limited.SlopeMaxDegrees = 90f;
        data.FoliageTypes = [limited];
        _h.Run(1);
        Assert.All(Positions(terrain), p => Assert.InRange(p.Y + limited.SinkMeters * 1.2f, -1f, 0.52f));
    }

    [Fact]
    public void EditsRebuildOnlyTheTilesTheyTouch()
    {
        var data = Data(size: 64, chunk: 16); // 4 × 4 chunks
        data.FoliageTypes = [Grass()];
        var terrain = AddTerrain(data);
        var foliage = terrain.Foliage!;
        Assert.Equal(4, foliage.GetTilesPerSide(0));
        int Builds(int x, int z) => foliage.GetTileBuilds(0, x, z);
        Assert.Equal(1, Builds(0, 0));

        // Raise a few vertices around (40, 40) m: chunk (2, 2), well inside it.
        terrain.SetHeights(new Rect2I(78, 78, 4, 4), Enumerable.Repeat(3f, 16).ToArray());
        Assert.Equal(2, Builds(2, 2));
        Assert.Equal(1, Builds(0, 0));
        Assert.Equal(1, Builds(3, 0));
        var raised = Positions(terrain).Where(p => p.X is > 39.6f and < 40.4f && p.Z is > 39.6f and < 40.4f).ToList();
        Assert.NotEmpty(raised);
        Assert.All(raised, p => Assert.True(p.Y > 2.5f));

        // Painting near a tile edge also rebuilds the neighbour that can own points there (half a tile of fuzz).
        terrain.SetWeights(new Rect2I(62, 10, 2, 2), new float[2 * 2 * 8]);
        Assert.Equal(2, Builds(1, 0));
        Assert.Equal(2, Builds(2, 0));
        Assert.Equal(1, Builds(0, 3));

        // User channels do not affect foliage.
        terrain.SetCells(TerrainLayers.User, 0, new Rect2I(0, 0, 2, 2), new uint[4]);
        Assert.Equal(1, Builds(0, 0));

        // Changing the type rebuilds every tile of it.
        data.FoliageTypes[0].ScaleMin = 2f;
        data.FoliageTypes[0].ScaleMax = 2f;
        _h.Run(1);
        for (var tz = 0; tz < 4; tz++)
            for (var tx = 0; tx < 4; tx++)
                Assert.All(foliage.GetTileNode(0, tx, tz)!.Multimesh!.Transforms, t => Assert.Equal(2f, t.Basis.Y.Length(), 4));
    }

    [Fact]
    public void TilesThinByKeyPrefixWithDistanceAndHideBeyondTheCullDistance()
    {
        var data = Data(size: 64, chunk: 16);
        data.FoliageTypes = [Grass()]; // full density to 20 m, none from 40 m
        var terrain = AddTerrain(data);
        var foliage = terrain.Foliage!;
        var node = foliage.GetTileNode(0, 0, 0)!;
        var count = foliage.GetTileInstanceCount(0, 0, 0);
        var multimesh = node.Multimesh!;

        foliage.UpdateDistances(new Vector3(8, 0, 8)); // at the tile centre
        Assert.True(node.Visible);
        Assert.Equal(count, multimesh.DrawnInstanceCount);

        var previous = count;
        for (var d = 21f; d < 40f; d += 2f)
        {
            foliage.UpdateDistances(new Vector3(8 + d, 0, 8));
            var drawn = multimesh.DrawnInstanceCount;
            Assert.True(drawn <= previous, $"more instances drawn further away ({drawn} > {previous} at {d} m)");
            Assert.InRange(drawn, (40f - d) / 20f * count - 0.08f * count, (40f - d) / 20f * count + 0.08f * count);
            previous = drawn;
        }

        foliage.UpdateDistances(new Vector3(8 + 41f, 0, 8));
        Assert.False(node.Visible);
        Assert.Equal(40f, node.VisibilityRangeEnd);
        Assert.Null(node.Owner);

        // The drawn prefix is an even sample: its keys are exactly those below the factor.
        foliage.UpdateDistances(new Vector3(8 + 30f, 0, 8));
        Assert.InRange(multimesh.DrawnInstanceCount, 0.4f * count, 0.6f * count);
    }

    [Fact]
    public void FoliageIsNotSavedWithTheScene()
    {
        var data = Data();
        data.FoliageTypes = [Grass()];
        AddTerrain(data);
        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(_h.Scene));
        Assert.DoesNotContain("TerrainFoliage3D", json, StringComparison.Ordinal);
        Assert.DoesNotContain("MultiMeshInstance3D", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacingTheTypeListRebuildsAndAnEmptyListDrawsNothing()
    {
        var data = Data();
        data.FoliageTypes = [Grass()];
        var terrain = AddTerrain(data);
        Assert.True(terrain.Foliage!.TotalInstances > 0);
        data.FoliageTypes = [];
        _h.Run(1);
        Assert.Equal(0, terrain.Foliage.TotalInstances);
        Assert.Empty(terrain.Foliage.Children);
        data.FoliageTypes = [Grass(density: 1f), null!];
        _h.Run(1);
        Assert.Equal(32 * 32, terrain.Foliage.TotalInstances);
    }

    [Fact]
    public void VisibleCountChangesDoNotTouchTheInstanceContent()
    {
        var multimesh = new MultiMesh { Mesh = new BoxMesh(), InstanceCount = 4 };
        var content = multimesh.ContentVersion;
        var version = multimesh.Version;
        multimesh.VisibleInstanceCount = 2;
        Assert.Equal(content, multimesh.ContentVersion);
        Assert.NotEqual(version, multimesh.Version);
        multimesh.SetInstanceTransform(0, Transform3D.Identity);
        Assert.NotEqual(content, multimesh.ContentVersion);

        var custom = new Aabb(new Vector3(-5), new Vector3(5));
        multimesh.CustomAabb = custom;
        Assert.Equal(custom, multimesh.GetAabb());
        multimesh.CustomAabb = Aabb.Empty;
        Assert.Equal(new Aabb(new Vector3(-0.5f), new Vector3(0.5f)), multimesh.GetAabb());
    }
}

/// <summary>Foliage distance updates in steady frames allocate nothing (ADR 0157).</summary>
[Collection(nameof(SerialAllocationGates))]
public sealed class TerrainFoliageAllocationTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void DistanceUpdatesAndSteadyFramesAllocateNothing()
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 64, 0.5f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        data.SetHeightsFrom(TerrainDataTests.Hills);
        data.FoliageTypes = [TerrainFoliageTests.Grass(subdivisions: 2), TerrainFoliageTests.Grass(density: 1f)];
        var terrain = _h.Add(new Terrain3D { Name = "Terrain", Data = data });
        var camera = _h.Add(new Camera3D { Position = new Vector3(10, 2, 10) });
        _h.Run(2);
        var foliage = terrain.Foliage!;

        var frame = 0;
        void Window()
        {
            for (var i = 0; i < 200; i++, frame++)
            {
                camera.Position = new Vector3(32 + 45 * MathF.Sin(frame * 0.05f), 3, 32 + 45 * MathF.Cos(frame * 0.05f));
                _h.Run(1);
                foliage.UpdateDistances(new Vector3(frame % 90, 1, 20));
            }
        }

        Window();
        Assert.Equal(0, AllocationGate.SmallestWindow(Window));
        Assert.True(foliage.DrawnInstances > 0);
    }
}
