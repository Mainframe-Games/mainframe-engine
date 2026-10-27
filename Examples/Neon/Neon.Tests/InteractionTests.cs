using System.Numerics;
using MainframeEngine;

namespace Neon.Tests;

/// <summary>The interaction framework on its own: doors, the locked front door, a light switch, reach and the safety net.</summary>
public sealed class InteractionTests
{
    /// <summary>A hinged door in the harness wall's opening: hinge at x = 0.5, the leaf along −X, swinging to −Z.</summary>
    private static Door AddDoor(ControllerHarness h)
    {
        var door = new Door { Name = "Door", Position = new Vector3(0.5f, 0f, 0f), OpenDegrees = -100f, SwingSeconds = 0.5f };
        h.Scene.AddChild(door);
        door.AddBody(new Aabb(new Vector3(-0.98f, 0f, -0.03f), new Vector3(-0.02f, 2.15f, 0.03f)));
        return door;
    }

    [Fact]
    public void TheDoorSwingsOpenAndShutEased()
    {
        using var h = new ControllerHarness();
        var door = AddDoor(h);

        door.Toggle();
        Assert.True(door.IsOpen);
        Assert.Equal(Verbs.Close, door.Verb);
        h.RunSeconds(0.25f);
        Assert.InRange(door.Openness, 0.3f, 0.7f);
        Assert.InRange(door.Leaf.RotationDegrees.Y, -80f, -20f); // eased: past a quarter of the way, short of the end
        h.RunSeconds(0.5f);
        Assert.Equal(1f, door.Openness);
        Assert.Equal(-100f, door.Leaf.RotationDegrees.Y, 3);

        door.Toggle();
        h.RunSeconds(0.75f);
        Assert.False(door.IsOpen);
        Assert.Equal(Verbs.Open, door.Verb);
        Assert.Equal(0f, door.Leaf.RotationDegrees.Y, 3);
    }

    [Fact]
    public void TheShutDoorBlocksThePlayerAndInteractOpensItToLetThemThrough()
    {
        using var h = new ControllerHarness();
        h.AddWallWithOpening();
        var door = AddDoor(h);
        var player = h.AddPlayer(new Vector3(0f, 0f, 2.5f)); // facing the door (−Z)
        var interactor = player.GetNode<Interactor>("Interactor");

        h.Hold("move_forward");
        h.RunSeconds(2.5f);
        h.Release("move_forward");
        h.RunSeconds(0.2f);
        Assert.True(player.GlobalPosition.Z > 0.2f, $"the shut door let the player through to {player.GlobalPosition}");
        Assert.Same(door, interactor.Current);
        Assert.Equal(Verbs.Open, interactor.Current!.Verb);

        h.PressInteract();
        Assert.True(door.IsOpen);
        h.RunSeconds(0.8f);
        h.Hold("move_forward");
        h.RunSeconds(2f);
        h.Release("move_forward");
        Assert.True(player.GlobalPosition.Z < -1.5f, $"the open door kept the player at {player.GlobalPosition}");

        // Shut again behind them, it blocks the way back.
        door.Toggle();
        h.RunSeconds(0.8f);
        ControllerHarness.Face(player, 180f);
        h.Hold("move_forward");
        h.RunSeconds(2.5f);
        h.Release("move_forward");
        Assert.True(player.GlobalPosition.Z < -0.2f, $"the shut door let the player back through to {player.GlobalPosition}");
    }

    [Fact]
    public void TheFrontDoorIsAlwaysLocked()
    {
        using var h = new ControllerHarness();
        h.AddWallWithOpening();
        var front = new FrontDoor { Name = "FrontDoor" };
        var body = new StaticBody3D { Name = "Body", CollisionLayer = NeonLayers.World | NeonLayers.Interact };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1f, 2.2f, 0.08f) }, Position = new Vector3(0f, 1.1f, 0f) });
        front.AddChild(body);
        h.Scene.AddChild(front);
        var player = h.AddPlayer(new Vector3(0f, 0f, 1.5f));
        var interactor = player.GetNode<Interactor>("Interactor");
        h.RunSeconds(0.2f);

        Assert.Same(front, interactor.Current);
        Assert.Equal(Verbs.Locked, interactor.Current!.Verb);
        var rattles = 0;
        front.Rattled += () => rattles++;
        for (var i = 0; i < 3; i++)
            h.PressInteract();
        Assert.Equal(3, front.Attempts);
        Assert.Equal(3, rattles);
        Assert.Equal(Verbs.Locked, interactor.Current!.Verb);

        h.Hold("move_forward");
        h.RunSeconds(3f);
        h.Release("move_forward");
        Assert.True(player.GlobalPosition.Z > 0.2f, $"the front door let the player out to {player.GlobalPosition}");
    }

    [Fact]
    public void TheSwitchTurnsTheLightsAndTheFixturesGlowOffAndOn()
    {
        using var h = new ControllerHarness();
        var light = new OmniLight3D { Name = "Light", Position = new Vector3(0f, 2.5f, -2f) };
        h.Scene.AddChild(light);
        var glow = new StandardMaterial3D { ResourceName = "LED_Test", EmissionColor = System.Drawing.Color.White, EmissionEnergy = 5f };
        var lightSwitch = new LightSwitch { Name = "Switch", Position = new Vector3(0f, 1.2f, -1f) };
        lightSwitch.AddBody(new Vector3(0.16f, 0.2f, 0.16f));
        lightSwitch.Lights.Add(light);
        lightSwitch.AddFixture(glow);
        h.Scene.AddChild(lightSwitch);
        // Eyes 1.63 m up, 1.5 m from the switch: look down at it.
        var player = h.AddPlayer(new Vector3(0f, 0f, 0.5f), pitchDegrees: -16f);
        var interactor = player.GetNode<Interactor>("Interactor");
        h.RunSeconds(0.2f);

        Assert.Same(lightSwitch, interactor.Current);
        Assert.Equal(Verbs.TurnOff, interactor.Current!.Verb);
        h.PressInteract();
        Assert.False(lightSwitch.IsOn);
        Assert.False(light.Visible);
        Assert.Equal(0f, glow.EmissionEnergy);
        Assert.Equal(Verbs.TurnOn, interactor.Current!.Verb);

        h.PressInteract();
        Assert.True(lightSwitch.IsOn);
        Assert.True(light.Visible);
        Assert.Equal(5f, glow.EmissionEnergy);
    }

    [Fact]
    public void TheSwitchDoesNotBlockThePlayer()
    {
        using var h = new ControllerHarness();
        var lightSwitch = new LightSwitch { Name = "Switch", Position = new Vector3(0f, 0.9f, -1f) };
        lightSwitch.AddBody(new Vector3(0.4f, 0.4f, 0.4f));
        h.Scene.AddChild(lightSwitch);
        var player = h.AddPlayer(new Vector3(0f, 0f, 0.5f));
        h.Hold("move_forward");
        h.RunSeconds(1.5f);
        h.Release("move_forward");
        Assert.True(player.GlobalPosition.Z < -2f, $"the switch's box stopped the player at {player.GlobalPosition}");
    }

    [Fact]
    public void NothingIsInReachBeyondTheReachOrBehindAWall()
    {
        using var h = new ControllerHarness();
        var lightSwitch = new LightSwitch { Name = "Switch", Position = new Vector3(0f, 1.63f, -3f) };
        lightSwitch.AddBody(new Vector3(0.16f, 0.2f, 0.16f));
        h.Scene.AddChild(lightSwitch);
        var player = h.AddPlayer(new Vector3(0f, 0f, 0f));
        var interactor = player.GetNode<Interactor>("Interactor");
        h.RunSeconds(0.2f);
        Assert.Null(interactor.Current); // 3 m: out of reach

        player.Position = new Vector3(0f, 0.02f, -1.5f);
        h.RunSeconds(0.2f);
        Assert.Same(lightSwitch, interactor.Current);

        h.AddBox(new Vector3(0f, 1.5f, -2.4f), new Vector3(2f, 3f, 0.1f), "Wall"); // between the eyes and the switch
        h.RunSeconds(0.2f);
        Assert.Null(interactor.Current);
        h.PressInteract();
        Assert.True(lightSwitch.IsOn);
        Assert.Equal(0, interactor.Interactions);
    }

    [Fact]
    public void TheSafetyNetPutsAFallingPlayerBackAtTheSpawn()
    {
        using var h = new ControllerHarness(floor: false);
        h.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(4f, 1f, 4f), "Floor");
        var safety = FallSafety.Create(new Vector3(0f, 0f, 0f));
        h.Scene.AddChild(safety);
        var player = h.AddPlayer(new Vector3(20f, 0f, 0f)); // off the floor's edge
        h.RunSeconds(3f);
        Assert.Equal(1, safety.Respawns);
        h.RunSeconds(1f);
        Assert.True(Vector3.Distance(player.GlobalPosition, Vector3.Zero) < 0.2f, $"not back at the spawn: {player.GlobalPosition}");
        Assert.True(player.IsOnFloor());
    }
}
