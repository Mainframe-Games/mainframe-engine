using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>Navigation 2D (G3a): the baked grid matches a real physics bake, headless chasers reach the target, walls added
/// by clicks reroute them, and the panel's controls reach the grid and the server.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class Navigation2DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly NavigationServer2D _navigation = new();
    private uint _frame;

    public Navigation2DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer2D(new PhysicsSettings2D()));
        _servers.Register(_navigation);
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Navigation2DScene.Build());
        Tick(2);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private NavigationDemo2D Demo => Scene.GetNode<NavigationDemo2D>("Controller");

    private NavigationGrid2D Grid => Scene.GetNode<NavigationGrid2D>("Grid");

    private Navigation2DPanel Panel => Scene.GetNode<Navigation2DPanel>("Ui/Panel");

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    private int RunUntilArrived(int maxFrames)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            if (Demo.Arrived == Navigation2DScene.ChaserCount)
                return i;
            Tick();
        }

        return -1;
    }

    [Fact]
    public void TheSavedGridIsWhatAPhysicsBakeOfTheWallsGives()
    {
        var baked = Grid.BakeData(out var error);
        Assert.Null(error);
        var saved = Navigation2DScene.BakedData();
        Assert.Equal(saved.Solid, baked!.Solid);
        Assert.Equal(Navigation2DScene.Walls.Sum(w => w.Area), baked.SolidCount);
        Assert.True(Grid.Grid.GetPointWeightScale(new Vector2I(7, 10)) == Navigation2DScene.MudWeight);
    }

    [Fact]
    public void EveryChaserWalksTheMazeToTheTarget()
    {
        Assert.Equal(Navigation2DScene.ChaserCount, Demo.Chasers.Count);
        var frames = RunUntilArrived(60 * 30);
        Assert.True(frames >= 0, $"{Demo.Arrived} of {Navigation2DScene.ChaserCount} arrived");
        foreach (var chaser in Demo.Chasers)
        {
            Assert.True(chaser.Agent!.IsNavigationFinished());
            Assert.True(chaser.GlobalPosition.X > Navigation2DScene.Origin.X + 20 * Navigation2DScene.Cell); // past the last wall
        }

        Assert.Equal(Navigation2DScene.ChaserCount, Panel.Grid is null ? -1 : Demo.Arrived);
    }

    [Fact]
    public void ClickedWallsRerouteEveryChaserAndCanBeRemoved()
    {
        Tick(5);
        var agent = Demo.Chasers[0].Agent!;
        var changes = 0;
        agent.PathChanged += () => changes++;
        // Block the gap in the first wall (cells (5, 9) and (5, 10)): the chasers must go round below it.
        Assert.True(Demo.ToggleWall(new Vector2I(5, 9)));
        Assert.True(Demo.ToggleWall(new Vector2I(5, 10)));
        Assert.False(Demo.ToggleWall(new Vector2I(5, 0))); // the maze's own wall
        Assert.False(Demo.ToggleWall(new Vector2I(99, 0)));
        Assert.Equal(2, Demo.AddedWalls);
        Tick(2);
        Assert.Equal(1, changes);
        Assert.DoesNotContain(new Vector2I(5, 9), agent.CurrentPath.Cells.ToArray());
        Assert.True(Grid.Grid.IsPointSolid(new Vector2I(5, 10)));

        Assert.True(Demo.ToggleWall(new Vector2I(5, 9)));
        Tick(2);
        Assert.False(Grid.Grid.IsPointSolid(new Vector2I(5, 9)));
        Assert.Equal(1, Demo.AddedWalls);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ThePanelDrivesTheGridAndTheDebugDraw()
    {
        Assert.True(_navigation.DebugDrawEnabled); // the scene turns it on
        Assert.Equal("none", Panel.Diagonal);
        Assert.Equal("octile", Panel.Heuristic);
        Panel.Diagonal = "always";
        Panel.Heuristic = "manhattan";
        Panel.Jumping = true;
        Assert.Equal(GridDiagonalMode.Always, Grid.DiagonalMode);
        Assert.Equal(GridHeuristic.Manhattan, Grid.Heuristic);
        Assert.True(Grid.JumpingEnabled);
        Panel.ShowNavigation = false;
        Assert.False(_navigation.DebugDrawEnabled);
        Panel.AddWalls = true;
        Assert.True(Demo.AddWalls);
        Demo.SetTarget(new Vector2(-300, 150));
        Assert.Equal(new Vector2(-300, 150), Scene.GetNode<Node2D>("Target").GlobalPosition);

        // Leaving the scene restores the server's debug draw as it was before.
        _navigation.DebugDrawEnabled = false;
        _tree.ChangeScene(new Node2D { Name = "Empty" });
        Tick();
        Assert.False(_navigation.DebugDrawEnabled);
    }
}
