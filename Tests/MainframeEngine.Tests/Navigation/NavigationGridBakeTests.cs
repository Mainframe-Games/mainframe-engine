using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.3: the physics bake of <see cref="NavigationGrid2D"/> through the query API (static bodies only).</summary>
[Collection(nameof(SerialBox2D))]
public sealed class NavigationGridBakeTests
{
    private static string Cells(NavigationGrid2D grid)
    {
        var g = grid.Grid;
        var rows = new List<string>();
        for (var y = 0; y < g.Region.Size.Y; y++)
        {
            var row = new char[g.Region.Size.X];
            for (var x = 0; x < row.Length; x++)
                row[x] = g.IsPointSolid(new Vector2I(x, y)) ? '#' : '.';
            rows.Add(new string(row));
        }

        return string.Join('\n', rows);
    }

    [Fact]
    public void StaticShapesBakeTheCellsTheyOverlap()
    {
        using var h = new NavigationHarness();
        var grid = h.AddGrid(8, 6, 10f);
        h.AddWall(new Vector2(30, 15), new Vector2(20, 10)); // exactly cells (2..3, 1): touching edges stay walkable
        var circle = new StaticBody2D { Name = "Pillar", Position = new Vector2(65, 45) };
        circle.AddChild(new CollisionShape2D { Shape = new CircleShape2D { Radius = 4 } });
        h.Add(circle);
        var capsule = new StaticBody2D { Name = "Post", Position = new Vector2(15, 40) };
        capsule.AddChild(new CollisionShape2D { Shape = new CapsuleShape2D { Radius = 3, Height = 16 } });
        h.Add(capsule);
        var polygon = new StaticBody2D { Name = "Wedge", Position = new Vector2(55, 5) };
        polygon.AddChild(new CollisionShape2D { Shape = new ConvexPolygonShape2D { Points = [new(-3, -3), new(3, -3), new(0, 3)] } });
        h.Add(polygon);
        h.Run(1);

        Assert.True(grid.Bake());
        h.Run(1);
        Assert.Equal("""
            .....#..
            ..##....
            ........
            .#......
            .#....#.
            ........
            """.ReplaceLineEndings("\n"), Cells(grid));
        Assert.Equal(NavigationGridSource.Physics, grid.Data!.BakedFrom);
        Assert.Equal(6, grid.Data.SolidCount);
        Assert.True(grid.LastBakeMilliseconds > 0);
        Assert.False(grid.IsBakeOutOfDate());
    }

    [Fact]
    public void AgentRadiusGrowsTheWallsAndTheMaskFilters()
    {
        using var h = new NavigationHarness();
        var grid = h.AddGrid(7, 3, 10f);
        h.AddWall(new Vector2(35, 15), new Vector2(10, 10)); // cell (3, 1)
        var other = h.AddWall(new Vector2(5, 5), new Vector2(10, 10), "OtherLayer");
        other.CollisionLayer = 4;
        h.Run(1);

        grid.Bake();
        h.Run(1);
        Assert.Equal("""
            .......
            ...#...
            .......
            """.ReplaceLineEndings("\n"), Cells(grid));

        grid.AgentRadius = 6f; // beyond half a cell: the neighbours too
        grid.Bake();
        h.Run(1);
        Assert.Equal("""
            ..###..
            ..###..
            ..###..
            """.ReplaceLineEndings("\n"), Cells(grid));

        grid.AgentRadius = 0f;
        grid.BakeCollisionMask = 4;
        grid.Bake();
        h.Run(1);
        Assert.Equal("""
            #......
            .......
            .......
            """.ReplaceLineEndings("\n"), Cells(grid));
    }

    [Fact]
    public void MovingBodiesAreNotBaked()
    {
        using var h = new NavigationHarness();
        var grid = h.AddGrid(4, 2, 10f);
        var rigid = new RigidBody2D { Name = "Crate", Position = new Vector2(5, 5), GravityScale = 0 };
        rigid.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(8) } });
        h.Add(rigid);
        var character = new CharacterBody2D { Name = "Hero", Position = new Vector2(15, 5) };
        character.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(8) } });
        h.Add(character);
        var area = new Area2D { Name = "Trigger", Position = new Vector2(25, 5) };
        area.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(8) } });
        h.Add(area);
        h.AddWall(new Vector2(35, 5), new Vector2(8, 8));
        h.Run(1);
        grid.Bake();
        h.Run(1);
        Assert.Equal("...#\n....", Cells(grid));
    }

    [Fact]
    public void TheBakeIsOutOfDateWhenAStaticBodyMoves()
    {
        using var h = new NavigationHarness();
        var grid = h.AddGrid(6, 6, 10f);
        var wall = h.AddWall(new Vector2(25, 25), new Vector2(10, 10));
        h.Run(1);
        grid.Bake();
        Assert.False(grid.IsBakeOutOfDate());
        wall.Position = new Vector2(35, 25);
        h.Run(1);
        Assert.True(grid.IsBakeOutOfDate());
        grid.Bake();
        Assert.False(grid.IsBakeOutOfDate());
        grid.Region = new Rect2I(0, 0, 7, 6);
        Assert.True(grid.IsBakeOutOfDate()); // baked for another region
    }

    [Fact]
    public void BakeOnReadyBakesAfterTheScenesBodiesEntered()
    {
        using var h = new NavigationHarness();
        var level = new Node2D { Name = "Level" };
        level.AddChild(new NavigationGrid2D { Name = "Grid", Region = new Rect2I(0, 0, 4, 4), CellSize = new Vector2(10), BakeOnReady = true });
        var wall = new StaticBody2D { Name = "Wall", Position = new Vector2(15, 15) };
        wall.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(8) } });
        level.AddChild(wall); // after the grid in tree order
        h.Add(level);
        h.Run(2);
        var grid = level.GetNode<NavigationGrid2D>("Grid");
        Assert.NotNull(grid.Data);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(1, 1)));
    }

    [Fact]
    public void ManualAndUnregisteredSourcesDoNotBakeFromPhysics()
    {
        using var h = new NavigationHarness();
        var grid = h.AddGrid(4, 4, 10f);
        h.AddWall(new Vector2(15, 15), new Vector2(8, 8));
        h.Run(1);
        grid.Source = NavigationGridSource.Manual;
        Assert.True(grid.Bake());
        Assert.Null(grid.Data);
        grid.Source = NavigationGridSource.TileMapLayer; // G3.6 registers its baker
        Assert.Null(NavigationGridBakers.Get(NavigationGridSource.TileMapLayer));
        Assert.False(grid.Bake());
        Assert.Throws<ArgumentOutOfRangeException>(() => NavigationGridBakers.Register(NavigationGridSource.Manual, new GridPhysicsBaker2D()));
        Assert.False(new NavigationGrid2D().Bake()); // not in a tree
    }

    [Fact]
    public void ALiveSourceRebakesTheLiveGridOnceAtTheNextSync()
    {
        using var h = new NavigationHarness();
        var baker = new CountingBaker();
        NavigationGridBakers.Register(NavigationGridSource.TileMapLayer, baker);
        try
        {
            var grid = new NavigationGrid2D { Name = "Tiles", Source = NavigationGridSource.TileMapLayer, Region = new Rect2I(0, 0, 4, 4) };
            h.Add(grid);
            Assert.Equal(1, baker.Attached);
            h.Run(1);
            Assert.True(grid.Grid.IsPointSolid(new Vector2I(baker.Bakes % 4, 0)));
            var bakes = baker.Bakes;
            var iteration = h.Map.IterationId;
            grid.MarkSourceChanged();
            grid.MarkSourceChanged();
            h.Run(1);
            Assert.Equal(bakes + 1, baker.Bakes); // at most once per sync
            Assert.True(h.Map.IterationId > iteration);
            Assert.Null(grid.Data); // a live source never touches Data
            grid.QueueFree();
            h.Run(1);
            Assert.Equal(1, baker.Detached);
        }
        finally
        {
            NavigationGridBakers.Unregister(NavigationGridSource.TileMapLayer);
        }
    }

    // Marks cell (bakes % 4, 0) solid and counts calls (a stand-in for G3.6's tile layer source).
    private sealed class CountingBaker : INavigationGridBaker
    {
        public int Bakes, Attached, Detached;

        public bool Bake(NavigationGrid2D node, AStarGrid2D cells, out string? failure)
        {
            failure = null;
            Bakes++;
            cells.SetPointSolid(new Vector2I(Bakes % 4, 0));
            return true;
        }

        public void Attach(NavigationGrid2D node) => Attached++;

        public void Detach(NavigationGrid2D node) => Detached++;
    }
}
