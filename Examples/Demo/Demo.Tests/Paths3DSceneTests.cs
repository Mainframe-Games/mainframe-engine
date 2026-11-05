using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class Paths3DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public Paths3DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Paths3DScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    [Fact]
    public void TheSceneHoldsPlatformsAGuardARailAndARider()
    {
        Assert.NotNull(Scene.GetNode<AnimatableBody3D>("LiftPath/Follow/Lift"));
        Assert.NotNull(Scene.GetNode<AnimatableBody3D>("FerryPath/Follow/Ferry"));
        Assert.NotNull(Scene.GetNode<Turntable3D>("Turntable"));
        Assert.NotNull(Scene.GetNode<PathGuard3D>("Guard"));
        Assert.True(Scene.GetNode<Path3D>("Rail").Curve!.Closed);
        Assert.NotNull(Scene.GetNode<PathRider3D>("Rider"));
        Assert.NotNull(Scene.GetNode<Paths3DPanel>("Ui/Panel"));
    }

    [Fact]
    public void TheRiderRidesTheLiftUpAndDown()
    {
        var lift = Scene.GetNode<AnimatableBody3D>("LiftPath/Follow/Lift");
        var rider = Scene.GetNode<PathRider3D>("Rider");
        Tick(30);
        var offset = rider.GlobalPosition - lift.GlobalPosition;
        var highest = 0f;
        for (var i = 0; i < 360; i++) // 6 s: up to the landing (2.8 s) and back down
        {
            Tick();
            highest = MathF.Max(highest, rider.GlobalPosition.Y);
            Assert.True(Vector3.Distance(rider.GlobalPosition - lift.GlobalPosition, offset) < 0.01f, $"frame {i}");
        }

        Assert.True(highest > Paths3DScene.LiftTop.Y, $"the rider only reached {highest}");
    }

    [Fact]
    public void TheGuardJoinsItsRouteAndCompletesALap()
    {
        var guard = Scene.GetNode<PathGuard3D>("Guard");
        var length = Scene.GetNode<Path3D>("Route").Curve!.GetBakedLength();
        Tick((int)((length + 6f) / guard.Speed * 60f * 1.3f));
        Assert.True(guard.Laps >= 1, $"walked {guard.Travelled} of {length}");
    }

    [Fact]
    public void ThePanelRidesTheRailAndScalesThePlatforms()
    {
        var panel = Scene.GetNode<Paths3DPanel>("Ui/Panel");
        var rail = Scene.GetNode<Camera3D>("Rail/Follow/Camera");
        panel.RideRail = true;
        Assert.True(rail.Current);
        Assert.False(Scene.GetNode<Camera3D>("Camera").Current);
        panel.SpeedScale = 2f;
        Assert.Equal(Paths3DScene.LiftSpeed * 2f, Scene.GetNode<PathFollow3D>("LiftPath/Follow").Speed);
        panel.Paused = true;
        Assert.Equal(0f, Scene.GetNode<Turntable3D>("Turntable").SpeedScale);
    }
}
