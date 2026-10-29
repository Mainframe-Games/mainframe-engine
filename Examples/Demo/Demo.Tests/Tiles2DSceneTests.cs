using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>Tiles 2D (G2b): the generated atlas and tile set, the level's layers, the hero walking the map and the panel.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class Tiles2DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public Tiles2DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Tiles2DScene.Build());
        Tick();
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private Tiles2DPanel Panel => Scene.GetNode<Tiles2DPanel>("Ui/Panel");

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    [Fact]
    public void TheCommittedAtlasAndTileSetAreTheGeneratedOnes()
    {
        var png = Png.ReadRgba8(ContentPaths.Resolve(Tiles2DScene.AtlasPath));
        Assert.Equal((TilesArt.Width, TilesArt.Height), (png.Width, png.Height));
        Assert.Equal(TilesArt.Pixels(), png.Pixels);

        var set = ResourceLoader.Load<TileSet>(Tiles2DScene.TileSetPath);
        try
        {
            var source = set.GetSource(0)!;
            Assert.Equal(8, source.Tiles.Length);
            Assert.Single(set.PhysicsLayers);
            var surface = set.GetCustomDataLayerByName("surface");
            Assert.Equal("stone", source.GetTileData(TilesArt.Stone).GetCustomString(surface));
            Assert.Equal(TilesArt.Cell, source.GetTileData(TilesArt.Canopy).YSortOrigin);
            Assert.Equal(8, source.GetTileData(TilesArt.Bush).Collision[0].Points.Length);
        }
        finally
        {
            set.Release();
        }
    }

    [Fact]
    public void TheLevelFillsThreeLayersAndTheObjectsLayerSortsTheHero()
    {
        var ground = Scene.GetNode<TileMapLayer>("Ground");
        var walls = Scene.GetNode<TileMapLayer>("Walls");
        var objects = Scene.GetNode<TileMapLayer>("Objects");
        Assert.Equal(Tiles2DScene.Level.Length * Tiles2DScene.Level[0].Length, ground.UsedCellCount);
        Assert.Equal(Tiles2DScene.Level.Sum(row => row.Count(c => c == '#')), walls.UsedCellCount);
        Assert.True(objects.YSortEnabled);
        Assert.IsType<TileHero2D>(objects.GetNode("Hero"));
        Assert.Equal(new Rect2I(0, 0, 42, 20), walls.GetUsedRect());
    }

    [Fact]
    public void TheHeroWalksTheMapWithoutLeavingItAndReportsTheSurface()
    {
        var hero = Scene.GetNode<TileHero2D>("Objects/Hero");
        var start = hero.Position;
        var surfaces = new HashSet<string>();
        for (var i = 0; i < 600; i++)
        {
            Tick();
            surfaces.Add(Panel.Surface);
        }

        Assert.True(Vector2.Distance(start, hero.Position) > 20f, $"the hero stayed at {hero.Position}");
        var walls = Scene.GetNode<TileMapLayer>("Walls");
        var cell = walls.LocalToMap(hero.Position);
        Assert.InRange(cell.X, 1, 40);
        Assert.InRange(cell.Y, 1, 18);
        Assert.Contains("dirt", surfaces);
        Assert.Equal("run", hero.GetNode<AnimatedSprite2D>("Sprite").Animation);
    }

    [Fact]
    public void ThePanelTogglesCollisionYSortAndWallCells()
    {
        var walls = Scene.GetNode<TileMapLayer>("Walls");
        var before = walls.UsedCellCount;
        Panel.ShowCollision = true;
        Assert.Equal(DebugVisibilityMode.ForceShow, walls.CollisionVisibilityMode);
        Panel.YSort = false;
        Assert.False(Scene.GetNode<TileMapLayer>("Objects").YSortEnabled);
        Panel.ToggleWall(new Vector2I(5, 5));
        Assert.Equal(before + 1, Panel.WallCells);
        Panel.ToggleWall(new Vector2I(5, 5));
        Assert.Equal(before, Panel.WallCells);
        Panel.ToggleWall(new Vector2I(0, 0)); // removes a border wall
        Assert.True(walls.GetCell(new Vector2I(0, 0)).IsEmpty);
        Panel.Speed = 500f;
        Assert.Equal(200f, Panel.Speed);
    }
}
