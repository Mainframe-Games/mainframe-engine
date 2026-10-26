using System.Numerics;
using MainframeEngine;

namespace Forest.Tests;

/// <summary>The log cabin's door (a toggle the interact action drives, an eased swing) and its place on the map.</summary>
public sealed class CabinTests
{
    [Fact]
    public void TheDoorSwingsOpenAndShutEased()
    {
        using var h = new ControllerHarness();
        var door = new CabinDoor { Name = "Door", OpenDegrees = -100f, SwingSeconds = 0.5f };
        h.Scene.AddChild(door);

        door.Toggle();
        Assert.True(door.IsOpen);
        h.RunSeconds(0.25f);
        Assert.InRange(door.Openness, 0.3f, 0.7f);
        Assert.InRange(door.RotationDegrees.Y, -80f, -20f); // eased: past a quarter of the way, short of the end
        h.RunSeconds(0.5f);
        Assert.Equal(1f, door.Openness);
        Assert.Equal(-100f, door.RotationDegrees.Y, 3);

        door.Toggle();
        h.RunSeconds(0.75f);
        Assert.False(door.IsOpen);
        Assert.Equal(0f, door.Openness);
        Assert.Equal(0f, door.RotationDegrees.Y, 3);
    }

    [Fact]
    public void ThePlayerOpensTheDoorWithInteractWhenInReachAndLookingAtIt()
    {
        using var h = new ControllerHarness();
        var door = new CabinDoor { Name = "Door", Interactive = true, Middle = new Vector3(0.45f, 1f, 0f) };
        h.Scene.AddChild(door);
        var player = h.AddPlayer(new Vector3(0.4f, 0f, 1.6f)); // facing north (−Z), at the door
        h.RunSeconds(0.2f);

        h.Hold("interact");
        h.Run(1);
        h.Release("interact");
        h.RunSeconds(1f);
        Assert.True(door.IsOpen);
        Assert.Equal(1f, door.Openness);

        // Out of reach (or looking away) the action does nothing.
        player.Position = new Vector3(0.4f, 0.02f, 6f);
        h.RunSeconds(0.2f);
        h.Hold("interact");
        h.Run(1);
        h.Release("interact");
        h.RunSeconds(0.2f);
        Assert.True(door.IsOpen);
    }

    [Fact]
    public void TheShutDoorKeepsThePlayerOutAndTheOpenOneLetsThemIn()
    {
        var bin = ForestAssets.Resolve(ForestAssets.CabinBuffer);
        if (!File.Exists(bin) || File.ReadAllText(bin).StartsWith("version https://git-lfs", StringComparison.Ordinal))
            Assert.Skip("The Forest's LFS art is not checked out (git lfs pull).");

        using var h = new ControllerHarness();
        // The cabin square to the axes at the origin: its door wall faces +Z, the door's middle at x ≈ 1.4.
        var cabin = ForestCabin.Create(0f, gameplay: false);
        cabin.Position = Vector3.Zero;
        cabin.RotationDegrees = Vector3.Zero;
        h.Scene.AddChild(cabin);
        var player = h.AddPlayer(new Vector3(1.4f, 0f, 5.5f)); // facing north (−Z), at the steps
        h.RunSeconds(0.3f);

        h.Hold("move_forward");
        h.RunSeconds(3f);
        Assert.True(player.GlobalPosition.Z > 2.5f, $"the shut door let the player through to {player.GlobalPosition}");

        cabin.Door!.Toggle();
        h.RunSeconds(3f);
        h.Release("move_forward");
        Assert.True(player.GlobalPosition.Z < 1.5f, $"the open door kept the player out at {player.GlobalPosition}");
        Assert.InRange(player.GlobalPosition.Y, 0.4f, 0.6f); // on the floor (0.45 m above the pad)
    }

    [Fact]
    public void TheCabinShotAndSpurStandOnThePad()
    {
        var r9 = ValleyLayout.Shots[^1];
        Assert.Equal("r9-cabin", r9.Name);
        // The camera stands inside the footprint (the cabin's walls are at ±3.3 × ±2.8 m), looking across the room.
        var local = r9.Eye - ValleyLayout.CabinCentre;
        Assert.True(local.Length() < 3f, $"R9 at {r9.Eye} is {local.Length():0.0} m from the cabin's centre");
        Assert.True(ValleyGenerator.CabinPadMask(r9.Eye.X, r9.Eye.Y) >= 1f);
        Assert.True(ValleyGenerator.CabinPadMask(r9.Target.X, r9.Target.Y) >= 1f);
        Assert.NotNull(ForestAssets.FindProp("Rockingchair_01"));
        Assert.Null(ForestAssets.FindProp("lava_lamp"));
    }
}
