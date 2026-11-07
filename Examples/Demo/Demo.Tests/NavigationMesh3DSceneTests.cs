using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>
/// Navigation Mesh 3D (G3.12): the saved mesh is what a Recast bake of the scene gives, capsules walk up the ramp onto the
/// terrace and up the stairs onto the deck, and the panel drives the debug draw.
/// </summary>
[Collection(nameof(SerialRmlUi))]
public sealed class NavigationMesh3DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly NavigationServer3D _navigation = new();
    private uint _frame;

    public NavigationMesh3DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        _servers.Register(_navigation);
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(NavigationMesh3DScene.Build());
        Tick(2);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private NavigationDemo3D Demo => Scene.GetNode<NavigationDemo3D>("Controller");

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    private int RunUntilArrived(int maxFrames)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            if (Demo.Arrived == NavigationMesh3DScene.ChaserCount)
                return i;
            Tick();
        }

        return -1;
    }

    [Fact]
    public void TheSavedMeshIsWhatABakeOfTheSceneGives()
    {
        var region = Scene.GetNode<NavigationRegion3D>("Level");
        var baked = region.BakeData(out var error);
        Assert.Null(error);
        Assert.Equal(region.NavigationMesh!.DetourData, baked!.Value.DetourData);
        Assert.True(region.NavigationMesh.PolygonCount > 10);
    }

    [Fact]
    public void CapsulesClimbTheRampAndTheStairs()
    {
        Assert.Equal(NavigationMesh3DScene.ChaserCount, Demo.Chasers.Count);
        Assert.True(RunUntilArrived(60 * 30) >= 0, $"{Demo.Arrived} of {NavigationMesh3DScene.ChaserCount} reached the terrace");
        Assert.All(Demo.Chasers, c => Assert.InRange(c.GlobalPosition.Y, 2.0f, 2.4f)); // 0.7 m above the 1.5 m terrace

        Demo.SetTarget(new Vector3(-6, 1.2f, 3.5f)); // the deck, by the stairs
        Assert.True(RunUntilArrived(60 * 40) >= 0, $"{Demo.Arrived} reached the deck");
        Assert.All(Demo.Chasers, c => Assert.InRange(c.GlobalPosition.Y, 1.7f, 2.1f));
    }

    [Fact]
    public void ThePanelDrivesTheDebugDraw()
    {
        var panel = Scene.GetNode<Navigation3DPanel>("Ui/Panel");
        Assert.Equal("Assets/Navigation/navmesh_3d_panel.rml", panel.Source);
        Assert.True(_navigation.DebugDrawEnabled);
        panel.ShowNavigation = false;
        Assert.False(_navigation.DebugDrawEnabled);
    }
}
