using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class Animation3DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public Animation3DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Animation3DScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private Animation3DPanel Panel => Scene.GetNode<Animation3DPanel>("Ui/Panel");

    private AnimationPlayer Player => Scene.GetNode<AnimationPlayer>("Crew/AnimationPlayer");

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    [Fact]
    public void TheCharacterRunsWithItsHammerAndTorchOnTheHand()
    {
        Tick();
        Assert.Equal("run", Player.CurrentAnimation);
        var skeleton = Scene.GetNode<Skeleton3D>("Crew/Armature");
        Assert.Equal(31, skeleton.GetBoneCount());
        var hand = Scene.GetNode<BoneAttachment3D>("Crew/Armature/HandSlot");
        Assert.Equal(skeleton.FindBone("Hand.R"), hand.BoneIdx);
        Assert.NotNull(hand.GetNodeOrNull<OmniLight3D>("Torch"));
        var expected = skeleton.GetBoneGlobalPose(hand.BoneIdx).Origin;
        Assert.True(System.Numerics.Vector3.Distance(expected, hand.Position) < 1e-4f, $"hand at {hand.Position}, bone at {expected}");
        Tick(40);
        Assert.StartsWith("Step", Panel.LastEvent); // the run's footsteps
    }

    [Fact]
    public void PickingAClipCrossfadesAndEventsShowInThePanel()
    {
        Tick();
        Panel.Blend = 0.2f;
        Panel.Clip = "walk";
        Tick();
        Assert.Equal("walk", Player.CurrentAnimation);
        Assert.True(Player.IsBlending);
        Tick(60);
        Assert.False(Player.IsBlending);
        Assert.Equal("walk", Panel.Clip);
    }

    [Fact]
    public void AOneShotClipReturnsToIdleAndTheSpeedScales()
    {
        Tick();
        Panel.Speed = 2f;
        Assert.Equal(2f, Player.SpeedScale);
        Panel.Clip = "swing";
        Tick(30); // 0.8 s at double speed
        Assert.StartsWith("HammerFist", Panel.LastEvent);
        Tick(2);
        Assert.Equal("idle", Panel.Clip);
        Assert.Equal("idle", Player.CurrentAnimation);
    }

    [Fact]
    public void RootMotionWalksAndJumpsTheWalkerRoundAClosedLoop()
    {
        var walker = Scene.GetNode<RootMotionWalker>("Walker");
        var player = walker.GetNode<AnimationPlayer>("Model/AnimationPlayer");
        Tick();
        var start = walker.GlobalPosition;
        Assert.Equal("circle", player.CurrentAnimation);
        Assert.Equal("Armature", player.RootMotionTrack.ToString());

        // Nearly half a circle to its left (+Z, it faces −X): across the circle, about a diameter on.
        Tick(110);
        Assert.Equal(0, walker.Jumps);
        Assert.InRange(walker.GlobalPosition.Z - start.Z, 1.4f, 1.8f);
        Assert.InRange(walker.GlobalPosition.X - start.X, -0.35f, 0.05f);
        Tick(10);
        Assert.Equal(1, walker.Jumps);
        Assert.Equal("jump", player.CurrentAnimation);

        // The jump, the other half circle and the jump back close the loop.
        for (var i = 0; i < 600 && !(walker.Jumps == 2 && player.CurrentAnimation == "circle"); i++)
            Tick();
        Tick(); // the body moves by its player's motion a step later
        Assert.Equal(2, walker.Jumps);
        Assert.True(System.Numerics.Vector3.Distance(start, walker.GlobalPosition) < 0.02f, $"back at {walker.GlobalPosition}, started at {start}");
        Assert.True(walker.IsOnFloor());
    }

    [Fact]
    public void TheWorkshopLiftsUnlocksSwingsFlickersAndLocksOnItsTracks()
    {
        var lift = Scene.GetNode<AnimatableBody3D>("Workshop/Lift");
        var hinge = Scene.GetNode<Node3D>("Workshop/Hinge");
        var latch = Scene.GetNode<DoorLatch>("Workshop/Latch");
        var lamp = Scene.GetNode<OmniLight3D>("Workshop/Lamp");
        var player = Scene.GetNode<AnimationPlayer>("Workshop/WorkshopPlayer");
        Tick();
        Assert.Equal("workshop", player.CurrentAnimation);
        Assert.False(latch.IsUnlocked);

        Tick(33); // 0.55 s: the lamp's first flicker
        Assert.True(lamp.Energy < 1f, $"lamp at {lamp.Energy}");
        Tick(57); // 1.5 s: unlocked (the method key at 1.4 s), the lift is rising
        Assert.True(latch.IsUnlocked);
        Assert.Equal(1, latch.Unlocks);
        Assert.InRange(lift.Position.Y, 0.2f, 1.2f);
        Tick(48); // 2.3 s: the door is open
        Assert.True(DoorAngle(hinge) > 1.4f, $"door at {DoorAngle(hinge)} rad");
        Tick(30); // 2.8 s: the lift is at the top
        Assert.Equal(1.4f, lift.Position.Y, 2);
        Tick(144); // 5.2 s: the door is shut and locked again
        Assert.False(latch.IsUnlocked);
        Assert.True(DoorAngle(hinge) < 0.01f, $"door at {DoorAngle(hinge)} rad");
        Tick(138); // 7.5 s: it looped, unlocked again at 6 + 1.4 s
        Assert.Equal(2, latch.Unlocks);
    }

    private static float DoorAngle(Node3D hinge) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(hinge.Rotation.W), 0f, 1f));
}
