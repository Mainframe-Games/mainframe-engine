using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class Paths2DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public Paths2DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Paths2DScene.Build());
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
    public void TheSceneHoldsATrackALiftAndAPatrol()
    {
        Assert.True(Scene.GetNode<Path2D>("Track").Curve!.Closed);
        Assert.Equal(Paths2DScene.CarSpeeds.Length, Scene.GetNode("Track").Children.OfType<PathFollow2D>().Count());
        Assert.True(Scene.GetNode<PathFollow2D>("Arc/Lift").PingPong);
        Assert.NotNull(Scene.GetNode<PathFollow2D>("Route/Carrot"));
        Assert.NotNull(Scene.GetNode<Paths2DPanel>("Ui/Panel"));
    }

    [Fact]
    public void CarsLapTheTrackTurningWithIt()
    {
        var car = Scene.GetNode<PathFollow2D>("Track/Car0");
        var loops = 0;
        car.Looped += () => loops++;
        var length = Scene.GetNode<Path2D>("Track").Curve!.GetBakedLength();
        var frames = (int)MathF.Ceiling(length / Paths2DScene.CarSpeeds[0] * 60f) + 5;
        var start = car.Rotation;
        Tick(frames / 4);
        Assert.NotEqual(start, car.Rotation);
        Tick(frames);
        Assert.True(loops >= 1);
    }

    [Fact]
    public void TheLiftTurnsBackAtTheEnds()
    {
        var lift = Scene.GetNode<PathFollow2D>("Arc/Lift");
        var ends = 0;
        lift.EndReached += () => ends++;
        var length = Scene.GetNode<Path2D>("Arc").Curve!.GetBakedLength();
        Tick((int)(length / Paths2DScene.LiftSpeed * 60f * 2.2f));
        Assert.True(ends >= 2, $"{ends} turns");
        Assert.InRange(lift.Progress, 0f, length);
    }

    [Fact]
    public void TheGuardJoinsTheRouteAndCompletesALap()
    {
        var guard = Scene.GetNode<PathGuard2D>("Guard");
        var route = Scene.GetNode<Path2D>("Route");
        var length = route.Curve!.GetBakedLength();
        Tick((int)((length + 200f) / guard.Speed * 60f * 1.3f));
        Assert.True(guard.Laps >= 1, $"walked {guard.Travelled} of {length}");
        var local = route.ToLocal(guard.GlobalPosition);
        Assert.True(Vector2.Distance(route.Curve.GetClosestPoint(local), local) < 6f);
    }

    [Fact]
    public void ThePanelScalesAndPausesEveryFollower()
    {
        var panel = Scene.GetNode<Paths2DPanel>("Ui/Panel");
        var car = Scene.GetNode<PathFollow2D>("Track/Car1");
        panel.SpeedScale = 2f;
        Assert.Equal(Paths2DScene.CarSpeeds[1] * 2f, car.Speed);
        panel.Paused = true;
        var progress = car.Progress;
        Tick(10);
        Assert.Equal(progress, car.Progress);
        panel.Paused = false;
        Assert.Equal(Paths2DScene.CarSpeeds[1] * 2f, car.Speed);
    }
}
