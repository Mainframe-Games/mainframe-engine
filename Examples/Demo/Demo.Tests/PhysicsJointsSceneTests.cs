using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>The G9 joints in the Physics 3D and 2D scenes: the bridges hold, the mannequin falls, the car drives.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class PhysicsJointsSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public PhysicsJointsSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        _servers.Register(new PhysicsServer2D(new PhysicsSettings2D()));
        _tree = new SceneTree(_servers);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private void Run(int frames)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    [Fact]
    public void TheRopeBridgeHoldsAndTheSignHangs()
    {
        _tree.ChangeScene(Physics3DScene.Build());
        Run(120);
        for (var i = 0; i <= Joints3DBuild.Planks; i++)
            Assert.True(Scene.GetNode<HingeJoint3D>($"Bridge/Hinge{i}").IsActive, $"Hinge{i}");
        for (var i = 0; i < Joints3DBuild.Planks; i++)
            Assert.True(Scene.GetNode<RigidBody3D>($"Bridge/Plank{i}").GlobalPosition.Y > 1.5f, $"Plank{i} fell");
        var weld = Scene.GetNode<WeldJoint3D>("SignPost/Weld");
        Assert.True(weld.IsActive);
        Assert.False(weld.IsBroken);
    }

    [Fact]
    public void TheMannequinStandsUntilACrateHitsItThenFallsAndResetStandsItUp()
    {
        _tree.ChangeScene(Physics3DScene.Build());
        var mannequin = Scene.GetNode<Mannequin3D>("Mannequin");
        var head = mannequin.GetNode<RigidBody3D>("Head");
        Run(60);
        Assert.False(mannequin.Simulating);
        var standing = head.GlobalPosition.Y;
        Assert.InRange(standing, 1.8f, 1.95f);

        Scene.GetNode<Dropper3D>("Crates").Drop(head.GlobalPosition + new Vector3(0.1f, 1.5f, 0));
        Run(180);
        Assert.True(mannequin.Simulating);
        Assert.Equal(1, mannequin.Falls);
        Assert.True(head.GlobalPosition.Y < 1f, $"knocked down: head at {head.GlobalPosition}");

        Scene.GetNode<Physics3DPanel>("Ui/Panel").ResetScene();
        Run(2);
        Assert.False(mannequin.Simulating);
        Assert.InRange(head.GlobalPosition.Y, standing - 0.01f, standing + 0.01f);
    }

    [Fact]
    public void ACrateOnTheSignBreaksItsWeld()
    {
        _tree.ChangeScene(Physics3DScene.Build());
        var board = Scene.GetNode<RigidBody3D>("SignPost/Board");
        var weld = Scene.GetNode<WeldJoint3D>("SignPost/Weld");
        Run(30);
        var crate = Scene.GetNode<Dropper3D>("Crates").Drop(board.GlobalPosition + new Vector3(0, 4f, 0));
        crate.Mass = 6;
        var brokeAt = -1;
        weld.Broken += () => brokeAt = (int)_frame;
        Run(240);
        Assert.True(weld.IsBroken);
        Assert.True(board.GlobalPosition.Y < 1f, $"the sign fell: {board.GlobalPosition}, broke at frame {brokeAt} of {_frame}, crate {crate.GlobalPosition}");

        Scene.GetNode<Physics3DPanel>("Ui/Panel").ResetScene();
        Run(30);
        Assert.False(weld.IsBroken);
        Assert.True(weld.IsActive);
    }

    // The 2D scene without its rain of bodies (they would land on the bridge and get under the car).
    private void Load2D()
    {
        var root = Physics2DScene.Build();
        root.GetNode<Spawner2D>("Bodies").SpawnStack = false;
        _tree.ChangeScene(root);
    }

    [Fact]
    public void TheChainHangsTheBridgeHoldsAndTheCarDrives()
    {
        Load2D();
        var chassis = Scene.GetNode<RigidBody2D>("Car/Chassis");
        var start = chassis.GlobalPosition.X;
        Run(120);
        for (var i = 0; i < Joints2DBuild.Links; i++)
            Assert.True(Scene.GetNode<PinJoint2D>($"Chain/Pin{i}").IsActive, $"Pin{i}");
        var last = Scene.GetNode<RigidBody2D>($"Chain/Link{Joints2DBuild.Links - 1}");
        Assert.True(last.GlobalPosition.Y > -330 + Joints2DBuild.Links * 24 * 0.7f, $"the chain hangs: {last.GlobalPosition}");
        Assert.True(MathF.Abs(chassis.GlobalPosition.X - start) > 50f, $"the car drove: {start} → {chassis.GlobalPosition.X}");
        Assert.True(Scene.GetNode<WheelJoint2D>("Car/WheelLJoint").IsActive);
        for (var i = 0; i <= Joints2DBuild.Planks; i++)
            Assert.False(Scene.GetNode<PinJoint2D>($"Bridge/Pin{i}").IsBroken, $"Pin{i}");
    }

    [Fact]
    public void TheCarTurnsBackAtTheEndsOfItsRange()
    {
        Load2D();
        var car = Scene.GetNode<Car2D>("Car");
        var chassis = car.GetNode<RigidBody2D>("Chassis");
        var lowest = float.MaxValue;
        var highest = float.MinValue;
        for (var i = 0; i < 60 * 12; i++)
        {
            Run(1);
            lowest = MathF.Min(lowest, chassis.GlobalPosition.X);
            highest = MathF.Max(highest, chassis.GlobalPosition.X);
        }

        Assert.True(car.Turns >= 2, $"{car.Turns} turns between {lowest} and {highest}");
        Assert.InRange(highest, car.Range.Y - 10, car.Range.Y + 150);
    }
}
