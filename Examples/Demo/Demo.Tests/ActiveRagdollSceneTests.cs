using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>
/// The Animation 3D character as an active ragdoll (G9.10c): it stands under physics, staggers when pushed and recovers,
/// falls when let go, and one arm can be active alone. Two physics substeps, as the Demo project runs (soft drives on a
/// stack of light bodies need them).
/// </summary>
[Collection(nameof(SerialRmlUi))]
public sealed class ActiveRagdollSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public ActiveRagdollSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false, SubstepCount = 2 }));
        _servers.Register(new PhysicsServer2D(new PhysicsSettings2D()));
        _tree = new SceneTree(_servers);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private void Run(int frames)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(_tree, ref _frame);
    }

    // The character idling, its physical skeleton ready.
    private Animation3DPanel LoadIdle()
    {
        _tree.ChangeScene(Animation3DScene.Build());
        Run(30);
        var panel = _tree.CurrentScene!.GetNode<Animation3DPanel>("Ui/Panel");
        panel.Clip = "idle";
        Run(30);
        Assert.NotNull(panel.Ragdoll);
        return panel;
    }

    private static PhysicalBone3D Body(PhysicalBoneSimulator3D ragdoll, string bone)
    {
        foreach (var body in ragdoll.PhysicalBones)
            if (body.BoneName == bone)
                return body;
        throw new InvalidOperationException(bone);
    }

    [Fact]
    public void TheDemoHoldsThePelvisAndHeadAndRunsTwoSubsteps()
    {
        var panel = LoadIdle();
        var ragdoll = panel.Ragdoll!;
        Assert.Equal(1f, Body(ragdoll, "Hips").HoldStrength);
        Assert.Equal(1f, Body(ragdoll, "Head").HoldStrength);
        Assert.Equal(0f, Body(ragdoll, "Chest").HoldStrength);
        Assert.Equal(2, ProjectSettings.Load(Path.Combine(AppContext.BaseDirectory, "project.mfproj")).Physics.Physics3D.SubstepCount);
    }

    [Fact]
    public void AnActiveCharacterStandsWithItsPelvisAndHeadOnTheAnimation()
    {
        var panel = LoadIdle();
        var ragdoll = panel.Ragdoll!;
        panel.ToggleActive();
        Assert.True(panel.IsActive && ragdoll.IsSimulating);
        Run(30); // the drives fade in over 0.3 s
        Assert.Equal(1f, ragdoll.DriveStrength);
        var (hips, head) = (Body(ragdoll, "Hips"), Body(ragdoll, "Head"));
        for (var i = 0; i < 180; i++)
        {
            Run(1);
            Assert.True(hips.GetHoldError() < 0.05f, $"frame {i}: the pelvis is {hips.GetHoldError()} m off its animated pose");
            Assert.True(head.GetHoldError() < 0.10f, $"frame {i}: the head is {head.GetHoldError()} m off");
        }
    }

    [Fact]
    public void APushedActiveCharacterStaggersAndRecovers()
    {
        var panel = LoadIdle();
        var ragdoll = panel.Ragdoll!;
        panel.ToggleActive();
        Run(60);
        var chest = Body(ragdoll, "Chest");
        panel.Push();
        var furthest = 0f;
        for (var i = 0; i < 30; i++)
        {
            Run(1);
            furthest = MathF.Max(furthest, chest.GetHoldError());
        }

        Assert.True(furthest > 0.10f, $"the chest staggered only {furthest} m");
        Run(60); // 1.5 s after the push
        Assert.True(chest.GetHoldError() < 0.05f, $"the chest is still {chest.GetHoldError()} m off");
        Assert.True(Body(ragdoll, "Hips").GetHoldError() < 0.05f);
    }

    [Fact]
    public void LettingGoFadesTheDrivesOutAndTheCharacterFalls()
    {
        var panel = LoadIdle();
        var ragdoll = panel.Ragdoll!;
        panel.ToggleActive();
        Run(60);
        var hips = Body(ragdoll, "Hips");
        var standing = hips.GlobalPosition.Y;

        panel.ToggleActive(); // let go
        Assert.False(panel.IsActive);
        Run(120);
        Assert.Equal((0f, 0f), (ragdoll.DriveStrength, ragdoll.HoldStrength));
        Assert.True(ragdoll.IsSimulating);
        Assert.True(hips.GlobalPosition.Y < standing - 0.5f, $"it fell: the pelvis at {hips.GlobalPosition.Y} (from {standing})");
        Assert.True(hips.GetHoldError() > 0.3f, "its hold error shows it lost its balance");

        panel.ToggleRagdoll(); // stand up: held again, the animation plays
        Run(30);
        Assert.False(ragdoll.IsSimulating);
        Assert.InRange(hips.GlobalPosition.Y, standing - 0.15f, standing + 0.15f);
    }

    [Fact]
    public void OneArmCanBeActiveAloneAndReactsToAHit()
    {
        var panel = LoadIdle();
        var ragdoll = panel.Ragdoll!;
        ragdoll.DriveStrength = 1f;
        ragdoll.PhysicalBonesStartSimulation(["Shoulder.L", "UpperArm.L", "LowerArm.L", "Hand.L"]);
        Run(120);
        var hand = Body(ragdoll, "Hand.L");
        var lower = Body(ragdoll, "LowerArm.L");
        Assert.False(Body(ragdoll, "Chest").IsSimulatingPhysics); // the rest still animates
        Assert.True(lower.GetDriveErrorDegrees() < 10f, $"the forearm is {lower.GetDriveErrorDegrees()}° off its animated pose");
        Assert.True(hand.GetDriveErrorDegrees() < 10f, $"the hand is {hand.GetDriveErrorDegrees()}° off");

        var start = hand.GlobalPosition;
        hand.ApplyImpulse(new Vector3(0, 0, 3));
        var furthest = 0f;
        for (var i = 0; i < 30; i++)
        {
            Run(1);
            furthest = MathF.Max(furthest, Vector3.Distance(hand.GlobalPosition, start));
        }

        Assert.True(furthest > 0.10f, $"the hand moved only {furthest} m");
        Run(90);
        Assert.True(lower.GetDriveErrorDegrees() < 10f, $"the forearm came back: {lower.GetDriveErrorDegrees()}°");
    }
}
