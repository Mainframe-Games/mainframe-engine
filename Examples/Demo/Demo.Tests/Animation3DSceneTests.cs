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
}
