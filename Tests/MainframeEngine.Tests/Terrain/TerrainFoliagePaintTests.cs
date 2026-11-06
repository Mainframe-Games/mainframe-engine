using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Terrain;

/// <summary>G8a.8: painted foliage density (the Terrain dock's Foliage brush): growth, undo, save and load.</summary>
public sealed class TerrainFoliagePaintTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private static TerrainData Data()
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 0.5f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        return data;
    }

    private static FoliageType Grass() => TerrainFoliageTests.Grass();

    private Terrain3D AddTerrain(TerrainData data)
    {
        var terrain = _h.Add(new Terrain3D { Name = "Terrain", Data = data });
        terrain.Owner = _h.Scene;
        return terrain;
    }

    private static List<Vector3> Positions(Terrain3D terrain)
    {
        var foliage = terrain.Foliage!;
        var n = foliage.GetTilesPerSide(0);
        var result = new List<Vector3>();
        for (var tz = 0; tz < n; tz++)
            for (var tx = 0; tx < n; tx++)
                if (foliage.GetTileNode(0, tx, tz) is { } node)
                    foreach (var t in node.Multimesh!.Transforms)
                        result.Add(t.Origin + node.Position);
        return result;
    }

    [Fact]
    public void PaintedDensityGrowsATypeWithoutAutoGrowthAndUndoRemovesIt()
    {
        var data = Data();
        var grass = Grass();
        grass.AutoGrowth = false; // only where painted
        data.FoliageTypes = [grass];
        var terrain = AddTerrain(data);
        Assert.Empty(Positions(terrain));
        Assert.False(data.HasPaintedFoliage);

        // Type 0 at full density over cells 8..15 × 8..15 (4 × 4 m at 0.5 m cells).
        var cells = new uint[8 * 8];
        Array.Fill(cells, 0xFFu);
        terrain.Edit.Begin(TerrainLayers.Foliage);
        terrain.SetCells(TerrainLayers.Foliage, 0, new Rect2I(8, 8, 8, 8), cells);
        var record = terrain.Edit.End();
        Assert.True(data.HasPaintedFoliage);
        Assert.Equal(1f, data.GetFoliageDensity(0, 10, 10));
        Assert.Equal(0f, data.GetFoliageDensity(1, 10, 10));
        var grown = Positions(terrain);
        Assert.InRange(grown.Count, 40, 80); // 16 m² at 4 per m², a little lost or gained at the edges
        Assert.All(grown, p => Assert.True(p.X is >= 3.5f and <= 8.5f && p.Z is >= 3.5f and <= 8.5f, $"{p} is outside the painted square"));

        record.Undo();
        Assert.Equal(0f, data.GetFoliageDensity(0, 10, 10));
        Assert.Empty(Positions(terrain));
    }

    [Fact]
    public void PaintedFoliageSavesOnlyWhenPaintedAndLoadsBack()
    {
        var folder = Directory.CreateTempSubdirectory("mf-foliage").FullName;
        try
        {
            var data = Data();
            data.SaveLayers(folder);
            Assert.False(File.Exists(Path.Combine(folder, TerrainData.FoliageFile(0))));
            data.SetCells(TerrainLayers.Foliage, 1, new Rect2I(3, 4, 1, 1), [0x00_80_00_00u]); // type 6 (image 1, B) at 128
            data.SaveLayers(folder);
            Assert.False(File.Exists(Path.Combine(folder, TerrainData.FoliageFile(0))));
            Assert.True(File.Exists(Path.Combine(folder, TerrainData.FoliageFile(1))));

            var loaded = new TerrainData { SizeMeters = 32, VertexSpacing = 0.5f, ChunkMeters = 16, LayerFolder = folder };
            Assert.Equal(128 / 255f, loaded.GetFoliageDensity(6, 3, 4), 4);
            Assert.True(loaded.HasPaintedFoliage);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
