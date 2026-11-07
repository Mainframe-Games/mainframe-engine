using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>
/// Navigation 3D (G3.11): the saved grid is what a physics bake of the walls gives, headless capsules walk to the target
/// without entering the walls, a new target sends them on, and the panel drives the debug draw.
/// </summary>
[Collection(nameof(SerialRmlUi))]
public sealed class Navigation3DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly NavigationServer3D _navigation = new();
    private uint _frame;

    public Navigation3DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        _servers.Register(_navigation);
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Navigation3DScene.Build());
        Tick(2);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private NavigationDemo3D Demo => Scene.GetNode<NavigationDemo3D>("Controller");

    private NavigationGrid3D Grid => Scene.GetNode<NavigationGrid3D>("Grid");

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    private int RunUntilArrived(int maxFrames)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            if (Demo.Arrived == Navigation3DScene.ChaserCount)
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
        Assert.Equal(Navigation3DScene.BakedData().Solid, baked!.Solid);
        Assert.True(baked.SolidCount > 100);
    }

    [Fact]
    public void EveryCapsuleWalksToTheTargetAndStaysOnTheFloor()
    {
        Assert.Equal(Navigation3DScene.ChaserCount, Demo.Chasers.Count);
        var frames = RunUntilArrived(60 * 30);
        Assert.True(frames >= 0, $"{Demo.Arrived} of {Navigation3DScene.ChaserCount} arrived");
        foreach (var chaser in Demo.Chasers)
        {
            Assert.InRange(chaser.GlobalPosition.Y, 0.6f, 0.8f);
            Assert.True(Vector3.Distance(chaser.GlobalPosition with { Y = 0 }, Demo.Target) < 1.5f);
        }

        Demo.SetTarget(new Vector3(-9.5f, 0, -4.5f)); // behind the long wall, round its end
        Assert.True(RunUntilArrived(60 * 30) >= 0, $"{Demo.Arrived} arrived at the second target");
    }

    [Fact]
    public void ThePanelDrivesTheDebugDrawAndRestoresIt()
    {
        var panel = Scene.GetNode<Navigation3DPanel>("Ui/Panel");
        Assert.True(panel.ShowNavigation);
        Assert.True(_navigation.DebugDrawEnabled);
        panel.ShowNavigation = false;
        Assert.False(_navigation.DebugDrawEnabled);
        panel.ShowNavigation = true;
        _tree.ChangeScene(new Node3D { Name = "Elsewhere" });
        Tick(2);
        Assert.False(_navigation.DebugDrawEnabled);
    }
}
