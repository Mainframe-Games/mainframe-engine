using System.Numerics;
using MainframeEngine.Tests.Terrain;

namespace MainframeEngine.Tests.Water;

/// <summary><see cref="River3D.Carve"/>, <see cref="River3D.Recarve"/>, <see cref="River3D.Uncarve"/> and <see cref="River3D.FitToTerrain"/>.</summary>
public sealed class RiverCarveTests : IDisposable
{
    private readonly SceneTree _tree = new(new ServerRegistry());
    private readonly Node3D _scene = new() { Name = "Scene" };

    public RiverCarveTests()
    {
        _tree.ChangeScene(_scene);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _tree.Servers.Dispose();
    }

    private Terrain3D AddTerrain(Func<float, float, float> height)
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 0.5f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        data.SetHeightsFrom(height);
        var terrain = new Terrain3D { Name = "Terrain", Data = data };
        _scene.AddChild(terrain);
        return terrain;
    }

    // A straight river along −Z at x = 16 from z = 28 to z = 4, surface at y = 1.8: 4 m wide, 0.6 m deep.
    private River3D AddStraightRiver(float surface = 1.8f)
    {
        var river = new River3D
        {
            Name = "River",
            Curve = RiverBuilderTests.StraightCurve(length: 24f, width: 4f, depth: 0.6f),
            Position = new Vector3(16, surface, 28),
        };
        _scene.AddChild(river);
        return river;
    }

    private static float Stored(TerrainData data, float x, float z)
    {
        var i = (int)MathF.Round(x / data.VertexSpacing);
        var j = (int)MathF.Round(z / data.VertexSpacing);
        return data.Heights[j * data.VerticesPerSide + i];
    }

    [Fact]
    public void TheChannelIsAParabolaWithBlendedBanks()
    {
        var terrain = AddTerrain((_, _) => 2f);
        var data = terrain.Data!;
        var river = AddStraightRiver();

        Assert.True(river.Carve());
        Assert.NotNull(river.LastCarveEdit);
        Assert.NotEmpty(river.CarveId);
        var step = data.HeightStep;

        // Channel: lip = surface + ShoreLift = 1.85; h = lip − (D + lift)(1 − x²).
        Assert.Equal(1.85f - 0.65f, Stored(data, 16f, 16f), step);
        Assert.Equal(1.85f - 0.65f * 0.75f, Stored(data, 17f, 16f), step);  // x = 0.5
        Assert.Equal(1.85f - 0.65f * 0.75f, Stored(data, 15f, 16f), step);  // symmetric
        Assert.Equal(1.85f, Stored(data, 18f, 16f), step);                  // the ribbon's edge
        // Bank: smoothstep from the lip to the ground over BankWidth (2 m).
        Assert.Equal(1.925f, Stored(data, 19f, 16f), step);                 // halfway
        Assert.Equal(2f, Stored(data, 20f, 16f), step);
        Assert.Equal(2f, Stored(data, 25f, 16f), step);
        // Past the downstream end (z = 4) the channel closes: 1 m beyond is halfway up the bank, 2 m beyond is ground.
        Assert.Equal(1.925f, Stored(data, 16f, 3f), step);
        Assert.Equal(2f, Stored(data, 16f, 2f), step);

        // The bed meets the surface just inside the ribbon (x² = D / (D + lift) ≈ 0.96²) and is under water before that.
        var water = _tree.Root.World3D.Water;
        Assert.True(terrain.HeightAt(17.8f, 16f) < river.SurfaceHeightAtOffset(12f) + river.Position.Y);
        Assert.True(water.WaterDepthAt(new Vector3(16f, 1.5f, 16f)) > 0.5f);
        Assert.True(terrain.HeightAt(16f, 16f) < water.SurfaceHeightAt(new Vector3(16f, 0f, 16f)) - 0.5f);
    }

    [Fact]
    public void UncarveAndUndoRestoreTheHeightsBitForBit()
    {
        var terrain = AddTerrain(TerrainDataTests.Hills);
        var data = terrain.Data!;
        var before = data.Heights.ToArray();
        var river = AddStraightRiver(surface: 1f);
        river.Curve!.SetPointPosition(1, new Vector3(-6, -0.5f, -22)); // a bend and a drop

        Assert.True(river.Carve());
        Assert.NotEqual(before, data.Heights.ToArray());
        var edit = river.LastCarveEdit!;
        edit.Undo();
        Assert.Equal(before, data.Heights.ToArray());
        edit.Redo();
        Assert.NotEqual(before, data.Heights.ToArray());

        var id = river.CarveId;
        Assert.True(river.Uncarve());
        Assert.Equal(before, data.Heights.ToArray());
        Assert.Equal("", river.CarveId);
        Assert.Null(data.GetCarveRecord(id));
        Assert.False(river.Uncarve());
        Assert.False(river.Recarve());
    }

    [Fact]
    public void CarvingAgainStartsFromTheUncarvedGround()
    {
        var terrain = AddTerrain(TerrainDataTests.Hills);
        var data = terrain.Data!;
        var original = data.Heights.ToArray();
        var river = AddStraightRiver(surface: 1f);

        river.Carve();
        var once = data.Heights.ToArray();
        Assert.True(river.Recarve());
        Assert.True(river.Carve()); // with a record, Carve re-carves too
        Assert.Equal(once, data.Heights.ToArray());

        // Moving the river and re-carving equals carving the moved river into the original ground.
        river.Position += new Vector3(3, 0, 0);
        Assert.True(river.Recarve());
        var moved = data.Heights.ToArray();
        Assert.True(river.Uncarve());
        Assert.Equal(original, data.Heights.ToArray());
        Assert.True(river.Carve());
        Assert.Equal(moved, data.Heights.ToArray());
    }

    [Fact]
    public void ARiverFindsTheTerrainUnderItOrUsesItsPath()
    {
        var terrain = AddTerrain((_, _) => 2f);
        var river = AddStraightRiver();
        river.TerrainPath = "../Missing";
        Assert.False(river.Carve());
        river.TerrainPath = "../Terrain";
        Assert.True(river.Carve());
        Assert.True(river.Uncarve());

        river.TerrainPath = "";
        river.Position = new Vector3(100, 2, 100); // off the map
        Assert.False(river.Carve());
        river.Terrain = terrain;                   // explicit, but the river lies off the map
        Assert.False(river.Carve());
    }

    [Fact]
    public void FitToTerrainPutsThePointsUnderTheGround()
    {
        var terrain = AddTerrain((_, z) => 7f + 0.1f * z); // the river runs from z = 28 down to z = 4
        var river = AddStraightRiver(surface: 5f);
        var regenerated = 0;
        river.Regenerated += () => regenerated++;

        Assert.True(river.FitToTerrain(0.3f));
        Assert.Equal(1, regenerated); // one curve change
        for (var i = 0; i < river.Curve!.PointCount; i++)
        {
            var world = river.GlobalTransform.TransformPoint(river.Curve.GetPointPosition(i));
            Assert.Equal(terrain.HeightAt(world.X, world.Z) - 0.3f, world.Y, 1e-3f);
        }

        // Carved, the surface runs 0.3 m under the old ground with banks 0.05 m above it.
        Assert.True(river.Carve());
        Assert.Equal(7f + 0.1f * 16f - 0.25f, terrain.HeightAt(18f, 16f), 0.02f);
    }

    [Fact]
    public void CarveRecordsRoundTripThroughTheLayerFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mf-carve", Guid.NewGuid().ToString("N"));
        try
        {
            var data = TerrainData.Create(TerrainProfile.Realistic, 32, 0.5f, 16);
            data.SetHeightsFrom(TerrainDataTests.Hills);
            var heights = new float[12 * 7];
            var rect = new Rect2I(5, 9, 12, 7);
            data.GetHeights(rect, heights);
            data.SetCarveRecord("abc", new TerrainCarveRecord(rect, heights));
            data.SaveLayers(folder);
            Assert.True(File.Exists(Path.Combine(folder, "carve_abc.json")));

            var loaded = new TerrainData
            {
                SizeMeters = 32,
                VertexSpacing = 0.5f,
                ChunkMeters = 16,
                LayerFolder = folder,
            };
            var record = loaded.GetCarveRecord("abc")!;
            Assert.Equal(rect, record.Rect);
            Assert.Equal(heights, record.Heights);

            // Saving loaded data into another folder carries records it never read along.
            var other = Path.Combine(folder, "moved");
            var reloaded = new TerrainData { SizeMeters = 32, VertexSpacing = 0.5f, ChunkMeters = 16, LayerFolder = folder };
            reloaded.EnsureLoaded();
            reloaded.SaveLayers(other);
            Assert.True(File.Exists(Path.Combine(other, "carve_abc.json")));

            data.SetCarveRecord("abc", null);
            data.SaveLayers(folder);
            Assert.False(File.Exists(Path.Combine(folder, "carve_abc.json")));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
