using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>
/// G3.6: <see cref="NavigationGrid2D"/> with <see cref="NavigationGridSource.TileMapLayer"/> — the layer's layout, solid
/// cells from tile collision and the "solid" custom data, weights from float custom data, live re-reads on
/// <see cref="TileMapLayer.Changed"/> that make agents repath, the errors that leave the grid walkable, and no allocation.
/// </summary>
[Collection(nameof(SerialBox2D))]
public sealed class TileMapNavigationTests
{
    // 16 px tiles: (0,0) wall (collision on physics layer 0), (1,0) fence (collision on layer 1), (2,0) rock ("solid" true),
    // (3,0) mud ("weight" 3), (4,0) floor (nothing).
    private static readonly Vector2I Wall = new(0, 0), Fence = new(1, 0), Rock = new(2, 0), Mud = new(3, 0), Floor = new(4, 0);

    private static TileSet MakeTileSet(CustomDataType solidType = CustomDataType.Bool)
    {
        var source = new TileSetAtlasSource { Texture = Texture2D.FromPixels(80, 16, new byte[80 * 16 * 4]) };
        for (var x = 0; x < 5; x++)
            source.CreateTile(new Vector2I(x, 0));
        var set = new TileSet();
        set.AddSource(source);
        set.AddPhysicsLayer();
        set.AddPhysicsLayer();
        var solid = set.AddCustomDataLayer("solid", solidType);
        var weight = set.AddCustomDataLayer("weight", CustomDataType.Float);
        source.GetOrCreateTileData(Wall).Collision.Add(TileCollisionPolygon.FullTile(set.TileSize));
        source.GetOrCreateTileData(Fence).Collision.Add(TileCollisionPolygon.FullTile(set.TileSize, physicsLayer: 1));
        source.GetOrCreateTileData(Rock).SetCustomData(solid, solidType == CustomDataType.Bool ? "true" : "rock");
        source.GetOrCreateTileData(Mud).SetCustomData(weight, 3.0);
        set.NotifyChanged();
        return set;
    }

    // A width × height floor at (x0, y0) on a layer without bodies (navigation reads tile data, not bodies).
    private static TileMapLayer AddLayer(NavigationHarness h, TileSet set, Rect2I floor, Vector2 position = default)
    {
        var layer = new TileMapLayer { Name = "Tiles", TileSet = set, CollisionEnabled = false, Position = position };
        for (var y = floor.Position.Y; y < floor.End.Y; y++)
            for (var x = floor.Position.X; x < floor.End.X; x++)
                layer.SetCell(new Vector2I(x, y), 0, Floor);
        return h.Add(layer);
    }

    private static NavigationGrid2D AddGrid(NavigationHarness h, string path = "../Tiles") =>
        h.Add(new NavigationGrid2D { Name = "Grid", Source = NavigationGridSource.TileMapLayer, TileMapLayerPath = path });

    [Fact]
    public void TheTileMapLayerSourceIsBuiltIn()
    {
        Assert.IsType<TileMapLayerBaker>(NavigationGridBakers.Get(NavigationGridSource.TileMapLayer));
        NavigationGridBakers.Register(NavigationGridSource.TileMapLayer, new TileMapLayerBaker());
        NavigationGridBakers.Unregister(NavigationGridSource.TileMapLayer); // puts the built-in back
        Assert.IsType<TileMapLayerBaker>(NavigationGridBakers.Get(NavigationGridSource.TileMapLayer));
    }

    [Fact]
    public void TheLayerOwnsTheLayoutAndCellsMatchMapCells()
    {
        using var h = new NavigationHarness(physics: false);
        var layer = AddLayer(h, MakeTileSet(), new Rect2I(-2, 1, 6, 4), new Vector2(100, 50));
        var grid = AddGrid(h);
        h.Run(1);

        Assert.Equal(new Rect2I(-2, 1, 6, 4), grid.Region);
        Assert.Equal(new Vector2(16, 16), grid.CellSize);
        Assert.Equal(new Vector2(100, 50), grid.GlobalPosition);
        foreach (var cell in new[] { new Vector2I(-2, 1), new Vector2I(3, 4), new Vector2I(0, 2) })
        {
            Assert.Equal(layer.ToGlobal(layer.MapToLocal(cell)), grid.Grid.GetPointPosition(cell));
            Assert.Equal(cell, grid.CellAt(layer.ToGlobal(layer.MapToLocal(cell))));
        }

        Assert.Null(grid.Data); // a live source never writes Data
    }

    [Fact]
    public void CollisionOnThePhysicsLayerTheSolidDataAndWeightsAreRead()
    {
        using var h = new NavigationHarness(physics: false);
        var layer = AddLayer(h, MakeTileSet(), new Rect2I(0, 0, 6, 1));
        layer.SetCell(new Vector2I(0, 0), 0, Wall);
        layer.SetCell(new Vector2I(1, 0), 0, Fence);
        layer.SetCell(new Vector2I(2, 0), 0, Rock);
        layer.SetCell(new Vector2I(3, 0), 0, Mud);
        var grid = AddGrid(h);
        grid.WeightDataLayer = "weight";
        h.Run(1);

        var cells = grid.Grid;
        Assert.True(cells.IsPointSolid(new Vector2I(0, 0)));  // collision on physics layer 0
        Assert.False(cells.IsPointSolid(new Vector2I(1, 0))); // its collision is on layer 1
        Assert.True(cells.IsPointSolid(new Vector2I(2, 0)));  // "solid" custom data
        Assert.False(cells.IsPointSolid(new Vector2I(3, 0)));
        Assert.Equal(3f, cells.GetPointWeightScale(new Vector2I(3, 0)));
        Assert.Equal(1f, cells.GetPointWeightScale(new Vector2I(4, 0))); // no weight set: 1
        Assert.False(cells.IsPointSolid(new Vector2I(5, 0)));

        grid.TilePhysicsLayer = 1; // a source option re-reads the layer
        grid.SolidDataLayer = "";
        grid.WeightDataLayer = "";
        h.Run(1);
        Assert.False(cells.IsPointSolid(new Vector2I(0, 0)));
        Assert.True(cells.IsPointSolid(new Vector2I(1, 0)));
        Assert.False(cells.IsPointSolid(new Vector2I(2, 0)));
        Assert.Equal(1f, cells.GetPointWeightScale(new Vector2I(3, 0)));
    }

    [Fact]
    public void AChangedLayerIsReReadAtTheNextSyncAndAgentsRepath()
    {
        using var h = new NavigationHarness(physics: false);
        var layer = AddLayer(h, MakeTileSet(), new Rect2I(0, 0, 10, 5));
        var grid = AddGrid(h);
        var walker = h.AddWalker(layer.MapToLocal(new Vector2I(0, 2)));
        walker.Speed = 0f; // stays put: only the path matters
        h.Run(1);
        walker.Agent.TargetPosition = layer.MapToLocal(new Vector2I(9, 2));
        h.Run(2);
        Assert.All(walker.Agent.CurrentPath.Cells.ToArray(), cell => Assert.Equal(2, cell.Y)); // straight along row 2

        // A wall across x = 5 with a gap at the bottom row: the path goes round through row 4.
        var version = grid.Grid.Version;
        for (var y = 0; y < 4; y++)
            layer.SetCell(new Vector2I(5, y), 0, Wall);
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(5, 2))); // not before the sync
        h.Run(2);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(5, 2)));
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(5, 4)));
        Assert.True(grid.Grid.Version > version);
        Assert.Contains(new Vector2I(5, 4), walker.Agent.CurrentPath.Cells.ToArray());

        // Painting past the used rectangle grows the region.
        layer.SetCell(new Vector2I(12, 6), 0, Floor);
        h.Run(1);
        Assert.Equal(new Rect2I(0, 0, 13, 7), grid.Region);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(5, 1)));
    }

    [Fact]
    public void ALayerThatEntersAfterTheGridIsFoundAndAFreedGridStopsWatching()
    {
        using var h = new NavigationHarness(physics: false);
        var grid = AddGrid(h);
        var layer = AddLayer(h, MakeTileSet(), new Rect2I(0, 0, 4, 4));
        layer.SetCell(new Vector2I(1, 1), 0, Wall);
        h.Run(1);
        Assert.Equal(new Rect2I(0, 0, 4, 4), grid.Region);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(1, 1)));

        var baker = (TileMapLayerBaker)NavigationGridBakers.Get(NavigationGridSource.TileMapLayer)!;
        var links = baker.LinkCount;
        grid.QueueFree();
        h.Run(1);
        Assert.Equal(links - 1, baker.LinkCount);
        layer.SetCell(new Vector2I(2, 2), 0, Wall); // nothing listens for the freed grid
        h.Run(1);
    }

    [Fact]
    public void BadSourcesFailAndLeaveTheGridWalkable()
    {
        using var h = new NavigationHarness(physics: false);
        var layer = AddLayer(h, MakeTileSet(CustomDataType.String), new Rect2I(0, 0, 3, 1));
        layer.SetCell(new Vector2I(0, 0), 0, Wall);
        var grid = AddGrid(h);
        h.Run(1);
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(0, 0)));
        Assert.Null(grid.BakeData(out var error));
        Assert.Contains("'solid' is String, not Bool", error);

        grid.SolidDataLayer = "";
        grid.WeightDataLayer = "cost";
        Assert.Null(grid.BakeData(out error));
        Assert.Contains("no custom data layer named 'cost'", error);

        grid.WeightDataLayer = "";
        grid.TilePhysicsLayer = 2;
        Assert.Null(grid.BakeData(out error));
        Assert.Contains("no physics layer 2", error);

        grid.TilePhysicsLayer = 0;
        layer.TileSet!.TileShape = TileShape.Isometric;
        Assert.Null(grid.BakeData(out error));
        Assert.Contains("square tiles only", error);

        layer.TileSet.TileShape = TileShape.Square;
        grid.TileMapLayerPath = "../Missing";
        Assert.Null(grid.BakeData(out error));
        Assert.Contains("'../Missing' is not a TileMapLayer", error);

        grid.TileMapLayerPath = "../Tiles";
        h.Run(1);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(0, 0))); // fixed: read again
    }

    [Fact]
    public void BakingIntoDataUsesTheLayersLayoutWithoutTouchingTheLiveGrid()
    {
        using var h = new NavigationHarness(physics: false);
        var layer = AddLayer(h, MakeTileSet(), new Rect2I(0, 0, 4, 3));
        layer.SetCell(new Vector2I(2, 1), 0, Rock);
        var grid = AddGrid(h);
        h.Run(1);
        Assert.True(grid.Bake());
        Assert.Equal(new Rect2I(0, 0, 4, 3), grid.Data!.Region);
        Assert.Equal(NavigationGridSource.TileMapLayer, grid.Data.BakedFrom);
        h.Run(1);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(2, 1)));
        layer.EraseCell(new Vector2I(2, 1));
        h.Run(1);
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(2, 1))); // live, whatever Data holds
    }

    [Fact]
    public void ReReadingAWarmLayerAllocatesNothing()
    {
        using var h = new NavigationHarness(physics: false);
        var layer = AddLayer(h, MakeTileSet(), new Rect2I(0, 0, 128, 128));
        for (var i = 0; i < 2000; i++)
            layer.SetCell(new Vector2I(i * 37 % 128, i * 11 % 128), 0, (i % 3) switch { 0 => Wall, 1 => Rock, _ => Mud });
        var grid = AddGrid(h);
        grid.WeightDataLayer = "weight";
        h.Run(1);
        for (var i = 0; i < 3; i++)
        {
            grid.MarkSourceChanged();
            h.Map.ForceSync();
        }

        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                grid.MarkSourceChanged();
                h.Map.ForceSync();
            }
        });
        Assert.Equal(0, allocated);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(0, 0)));
    }
}
