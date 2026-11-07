using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>
/// Navigation Mesh 2D (G3.9): the saved polygons are what a bake of the scene gives, headless chasers cross a ferry and
/// reach the far bank without entering the walls or the river, the pond is carved or not, and the panel's controls reach
/// the controller and the server.
/// </summary>
[Collection(nameof(SerialRmlUi))]
public sealed class NavigationMesh2DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly NavigationServer2D _navigation = new();
    private uint _frame;

    public NavigationMesh2DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer2D(new PhysicsSettings2D()));
        _servers.Register(_navigation);
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(NavigationMesh2DScene.Build());
        Tick(2);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private NavigationMeshDemo2D Demo => Scene.GetNode<NavigationMeshDemo2D>("Controller");

    private NavigationMesh2DPanel Panel => Scene.GetNode<NavigationMesh2DPanel>("Ui/Panel");

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    private static bool InRiver(Vector2 p) => p.X > NavigationMesh2DScene.River.Position.X + 2f && p.X < NavigationMesh2DScene.River.End.X - 2f;

    [Fact]
    public void TheSavedPolygonsAreWhatABakeOfTheSceneGives()
    {
        foreach (var name in new[] { "Hall", "Bank" })
        {
            var region = Scene.GetNode<NavigationRegion2D>(name);
            var saved = region.NavigationPolygon!;
            var baked = region.BakeData(out var error);
            Assert.Null(error);
            Assert.Equal(saved.Vertices, baked!.Value.Vertices);
            Assert.Equal(saved.Polygons.Length, baked.Value.Polygons.Length);
            Assert.True(saved.PolygonCount > 0);
        }

        Assert.True(_navigation.GetMap(_tree.Root.World2D).PolygonCount > 10);
        foreach (var link in Scene.Children.OfType<NavigationLink2D>())
            Assert.True(link.IsConnected(), $"{link.Name} joins both regions");
    }

    [Fact]
    public void EveryChaserFerriesAcrossToTheTarget()
    {
        Assert.Equal(NavigationMesh2DScene.ChaserCount, Demo.Chasers.Count);
        var wet = 0;
        var where = "";
        for (var i = 0; i < 60 * 40 && Demo.Arrived < NavigationMesh2DScene.ChaserCount; i++)
        {
            Tick();
            foreach (var chaser in Demo.Chasers)
                if (InRiver(chaser.GlobalPosition))
                {
                    wet++;
                    if (where.Length < 400)
                        where += $"{i}:{chaser.Name}@{chaser.GlobalPosition} ";
                }
        }

        Assert.Equal(NavigationMesh2DScene.ChaserCount, Demo.Arrived);
        Assert.True(Demo.Crossings >= NavigationMesh2DScene.ChaserCount, $"{Demo.Crossings} crossings");
        Assert.True(wet == 0, where); // they hop the river, never wade it
        foreach (var chaser in Demo.Chasers)
            Assert.True(chaser.GlobalPosition.X > NavigationMesh2DScene.River.End.X);
    }

    [Fact]
    public void ThePondIsCarvedOrNot()
    {
        var map = _navigation.GetMap(_tree.Root.World2D);
        var inPond = new Vector2(-330, 150);
        Assert.NotEqual(inPond, map.GetClosestPoint(inPond));
        Panel.PondCarved = false;
        Tick(2);
        Assert.Equal(inPond, map.GetClosestPoint(inPond));
        Assert.True(Demo.LastBakeMilliseconds > 0);
        Panel.PondCarved = true;
        Tick(2);
        Assert.NotEqual(inPond, map.GetClosestPoint(inPond));
    }

    [Fact]
    public void ThePanelDrivesTheDebugDrawAndAvoidanceAndRestoresTheServer()
    {
        Assert.True(Panel.ShowNavigation);
        Assert.True(_navigation.DebugDrawEnabled);
        Assert.True(Panel.Avoidance);
        Panel.Avoidance = false;
        Assert.All(Demo.Chasers, c => Assert.False(c.Agent!.AvoidanceEnabled));
        Panel.ShowNavigation = false;
        Assert.False(_navigation.DebugDrawEnabled);
        _tree.ChangeScene(new Node2D { Name = "Elsewhere" });
        Tick(2);
        Assert.False(_navigation.DebugDrawEnabled); // back to what it was before the scene
    }

    [Fact]
    public void AClickSendsTheChasersThere()
    {
        Demo.SetTarget(new Vector2(-300, -150));
        Assert.Equal(new Vector2(-300, -150), Demo.Target);
        Assert.Equal(new Vector2(-300, -150), Demo.Chasers[0].Agent!.TargetPosition);
        Assert.Equal(new Vector2(-300, -150), Scene.GetNode<Node2D>("Target").GlobalPosition);
    }
}
