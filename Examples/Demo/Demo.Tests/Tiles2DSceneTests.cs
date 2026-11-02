using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>
/// Tiles 2D (G2b, G3.6): the generated atlas and tile set, the level's layers, the hero walking the map, the shadow chasing it
/// on a navigation grid read from the walls layer, and the panel.
/// </summary>
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
        Assert.Equal(Tiles2DScene.Level.Sum(row => row.Count(c => c is '#' or '~')), walls.UsedCellCount);
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
    public void TheShadowChasesTheHeroRoundWallsAndWater()
    {
        var hero = Scene.GetNode<TileHero2D>("Objects/Hero");
        var shadow = Scene.GetNode<TileShadow2D>("Objects/Shadow");
        var walls = Scene.GetNode<TileMapLayer>("Walls");
        var grid = Scene.GetNode<NavigationGrid2D>("Navigation");
        Assert.Equal(walls.GetUsedRect(), grid.Region);
        Assert.Equal(new Vector2(TilesArt.Cell), grid.CellSize);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(8, 3)));  // water
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(19, 7))); // stone
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(13, 9)));

        hero.Autopilot = false; // stands still at its start
        Assert.True(Vector2.Distance(shadow.GlobalPosition, hero.GlobalPosition) > 300f);
        for (var i = 0; i < 1500; i++)
        {
            Tick();
            var cell = walls.LocalToMap(walls.ToLocal(shadow.GlobalPosition));
            Assert.True(walls.GetCell(cell).IsEmpty, $"the shadow stepped on {cell} at tick {i}");
        }

        Assert.True(Vector2.Distance(shadow.GlobalPosition, hero.GlobalPosition) <= shadow.CatchDistance + 1f,
            $"the shadow stopped at {shadow.GlobalPosition}, the hero is at {hero.GlobalPosition}");
    }

    [Fact]
    public void AWallOnTheShadowsPathMakesItRepath()
    {
        Scene.GetNode<TileHero2D>("Objects/Hero").Autopilot = false;
        var shadow = Scene.GetNode<TileShadow2D>("Objects/Shadow");
        var grid = Scene.GetNode<NavigationGrid2D>("Navigation");
        Tick(30);
        // Paths are string-pulled (corners only): block the cell halfway along the longest leg.
        var points = shadow.Agent!.CurrentPath.Points.ToArray();
        Assert.True(points.Length >= 2, $"the path has {points.Length} points");
        var leg = 0;
        for (var i = 1; i < points.Length - 1; i++)
            if (Vector2.Distance(points[i], points[i + 1]) > Vector2.Distance(points[leg], points[leg + 1]))
                leg = i;
        var blocked = grid.CellAt((points[leg] + points[leg + 1]) * 0.5f);
        Assert.Contains(blocked, CellsAlong(grid, points));
        Panel.ToggleWall(blocked);
        Tick(2);
        Assert.True(grid.Grid.IsPointSolid(blocked));
        var repathed = shadow.Agent.CurrentPath.Points.ToArray();
        Assert.True(repathed.Length >= 2);
        Assert.DoesNotContain(blocked, CellsAlong(grid, repathed));
    }

    // The cells a polyline passes through, sampled every pixel.
    private static HashSet<Vector2I> CellsAlong(NavigationGrid2D grid, Vector2[] points)
    {
        var cells = new HashSet<Vector2I>();
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var steps = Math.Max(1, (int)Vector2.Distance(points[i], points[i + 1]));
            for (var s = 0; s <= steps; s++)
                cells.Add(grid.CellAt(Vector2.Lerp(points[i], points[i + 1], s / (float)steps)));
        }

        return cells;
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
        Panel.Chase = false;
        Assert.False(Scene.GetNode<TileShadow2D>("Objects/Shadow").Chasing);
        Panel.ShowNavigation = true;
        Assert.True(_tree.Servers.Get<NavigationServer2D>()!.DebugDrawEnabled);
    }
}
