using System.Numerics;
using MainframeEngine;

namespace Neon.Tests;

/// <summary>
/// The apartment from <c>Tools/apartment/build_apartment.py</c>, loaded as in the game: its doors, switches and
/// sockets, a walk through every room and up the stairs, and the containment (the railing, the front door). Skipped
/// when the model's LFS buffer is not checked out.
/// </summary>
public sealed class ApartmentTests
{
    private static ControllerHarness Load(out NeonApartment apartment)
    {
        var bin = Path.Combine(AppContext.BaseDirectory, NeonApartment.BufferPath);
        if (!File.Exists(bin) || File.ReadAllText(bin).StartsWith("version https://git-lfs", StringComparison.Ordinal))
            Assert.Skip("Neon's LFS art is not checked out (git lfs pull).");
        var h = new ControllerHarness(floor: false);
        apartment = NeonApartment.Create();
        h.Scene.AddChild(apartment);
        return h;
    }

    private static FirstPersonController AddPlayer(ControllerHarness h, Vector2 feet, float yaw, float y = 0f)
    {
        var player = h.AddPlayer(new Vector3(feet.X, y, feet.Y), yaw);
        h.RunSeconds(0.3f);
        return player;
    }

    [Fact]
    public void TheModelsNodesBecomeDoorsSwitchesLightsAndSockets()
    {
        using var h = Load(out var apartment);
        Assert.IsType<Door>(apartment.Doors["Door_Bath"]);
        Assert.IsType<Door>(apartment.Doors["Door_Computer"]);
        Assert.IsType<Door>(apartment.Doors["Door_Bed2"]);
        Assert.IsType<SlidingDoor>(apartment.Doors["Door_Balcony"]);
        Assert.NotNull(apartment.FrontDoor);
        Assert.Equal(["Balcony", "Bath", "Bed2", "Computer", "Living", "Loft"], apartment.Switches.Keys.Order());
        Assert.Equal(16, apartment.Lights.Count);
        foreach (var (room, lightSwitch) in apartment.Switches)
        {
            Assert.NotEmpty(lightSwitch.Lights);
            Assert.Single(lightSwitch.Fixtures);
            Assert.Equal("LED_" + room, lightSwitch.Fixtures[0].ResourceName);
            Assert.True(lightSwitch.Fixtures[0].EmissionEnergy > 1f, room);
        }

        foreach (var socket in new[] { "Spawn", "Seat", "Shower", "Tap_Kitchen", "Tap_Bath", "Tv", "Stereo", "Blinds_Living_1", "Valve_Shower", "Panel_Blinds_Living", "Monitor_1", "Monitor_6", "Zone_Living", "Zone_Balcony" })
            Assert.True(apartment.Sockets.ContainsKey(socket), socket);
        Assert.Equal(5, apartment.Lights.Count(l => l.Parent!.Name.StartsWith("Light_Living", StringComparison.Ordinal)));
        Assert.Equal(4, apartment.Lights.Count(l => l.CastsShadows)); // the engine's limit of shadowed point lights
    }

    [Fact]
    public void TheScenesSpawnIsTheModels()
    {
        using var h = Load(out var apartment);
        var (position, yaw) = apartment.Spawn;
        Assert.True(Vector3.Distance(position, NeonScene.Spawn) < 0.05f, $"model {position}, scene {NeonScene.Spawn}");
        Assert.Equal(NeonScene.SpawnYawDegrees, MathF.Abs(yaw), 1);
    }

    [Fact]
    public void ASwitchTurnsItsRoomsLightsAndGlowOffAndOn()
    {
        using var h = Load(out var apartment);
        var living = apartment.Switches["Living"];
        var energy = living.Fixtures[0].EmissionEnergy;
        living.Interact();
        Assert.All(living.Lights, l => Assert.False(l.Visible));
        Assert.Equal(0f, living.Fixtures[0].EmissionEnergy);
        Assert.True(apartment.Switches["Bath"].Lights.TrueForAll(l => l.Visible)); // other rooms stay lit
        living.Interact();
        Assert.All(living.Lights, l => Assert.True(l.Visible));
        Assert.Equal(energy, living.Fixtures[0].EmissionEnergy);
    }

    /// <summary>
    /// From the spawn, through every room: the kitchen, the bathroom and the computer room (opening their doors with
    /// <c>interact</c>), up the stairs to bedroom 2 and the loft, down again and out through the slider to the balcony.
    /// </summary>
    [Fact]
    public void ThePlayerWalksThroughEveryRoomAndUpTheStairs()
    {
        using var h = Load(out var apartment);
        var player = AddPlayer(h, new Vector2(NeonScene.Spawn.X, NeonScene.Spawn.Z), NeonScene.SpawnYawDegrees);
        var interactor = player.GetNode<Interactor>("Interactor");

        var route = "";
        void Walk(float x, float z, string where)
        {
            Assert.True(h.WalkTo(player, new Vector2(x, z)), $"{where}: stuck at {player.GlobalPosition}, heading for ({x}, {z}); {h.Blocked}\nwalked:\n{route}");
            route += $"  {where}: {player.GlobalPosition}\n";
        }

        void Open(string door, float yaw)
        {
            ControllerHarness.Face(player, yaw);
            h.Run(2);
            Assert.Same(apartment.Doors[door], interactor.Current);
            h.PressInteract();
            Assert.True(((DoorBase)apartment.Doors[door]).IsOpen, door);
            h.RunSeconds(1f);
        }

        Walk(2f, -4.2f, "the kitchen");
        Walk(-0.9f, -4.2f, "under the landing");
        Walk(-0.9f, -3.15f, "the bathroom door");
        Open("Door_Bath", 90f);
        Walk(-2.5f, -3.15f, "the bathroom's doorway");
        Walk(-4.5f, -3f, "the bathroom");
        Walk(-2.5f, -3.15f, "out of the bathroom");
        Walk(-0.9f, -3.15f, "out of the bathroom");
        Walk(0f, -2f, "the living room");
        Walk(0f, 4.45f, "past the stairs");
        Walk(-0.9f, 4.45f, "the computer room door");
        Open("Door_Computer", 90f);
        Walk(-2.5f, 4.45f, "the computer room's doorway");
        Walk(-4f, 3f, "the computer room");
        Walk(-2.5f, 4.45f, "out of the computer room");
        Walk(-0.95f, 4.6f, "the foot of the stairs");
        Walk(-0.95f, -1.4f, "up the stairs");
        Assert.InRange(player.GlobalPosition.Y, 2.9f, 3.2f);
        Walk(-2.3f, -3.55f, "bedroom 2's door");
        Open("Door_Bed2", 90f);
        Walk(-4.2f, -3.55f, "bedroom 2's doorway");
        Walk(-4.5f, -2.8f, "bedroom 2");
        Walk(-2.3f, -3.55f, "out of bedroom 2");
        Walk(-2.2f, -0.5f, "the hall");
        Walk(-3.5f, 1f, "the loft");
        Walk(-3.5f, 4f, "the loft's window");
        Assert.InRange(player.GlobalPosition.Y, 2.9f, 3.2f);
        Walk(-2.2f, -1.1f, "the top of the stairs");
        Walk(-0.95f, -1.1f, "the top of the stairs");
        Walk(-0.95f, 4.6f, "down the stairs");
        Assert.InRange(player.GlobalPosition.Y, -0.1f, 0.2f);
        Walk(2.7f, 4.6f, "the slider");
        Open("Door_Balcony", 180f);
        Walk(2.7f, 6.8f, "the balcony");
        Assert.InRange(player.GlobalPosition.Y, -0.1f, 0.2f);
    }

    [Fact]
    public void TheRailingKeepsThePlayerOnTheBalcony()
    {
        using var h = Load(out _);
        foreach (var (start, yaw) in new[] { (new Vector2(2.7f, 6.5f), 180f), (new Vector2(6f, 7.2f), -90f), (new Vector2(-6f, 7.2f), 90f), (new Vector2(6.5f, 7.5f), -135f) })
        {
            var player = AddPlayer(h, start, yaw);
            h.Hold("move_forward");
            h.Hold("sprint");
            for (var i = 0; i < 12; i++) // run and jump at it for 4 s
            {
                h.Hold("jump");
                h.Run(2);
                h.Release("jump");
                h.Run(18);
            }

            h.Release("sprint");
            h.Release("move_forward");
            h.RunSeconds(1f);
            var p = player.GlobalPosition;
            Assert.True(p.Z < 8f - 0.25f && MathF.Abs(p.X) < 7.2f - 0.25f && p.Y > -0.1f && p.Y < 0.2f, $"from {start}: the player got to {p}");
            player.Free();
        }
    }

    [Fact]
    public void TheFrontDoorKeepsThePlayerIn()
    {
        using var h = Load(out var apartment);
        var player = AddPlayer(h, new Vector2(5.1f, -4.2f), 0f); // facing the front door (−Z)
        var interactor = player.GetNode<Interactor>("Interactor");
        h.Hold("move_forward");
        h.RunSeconds(2f);
        h.Release("move_forward");
        h.Run(2);
        Assert.Same(apartment.FrontDoor, interactor.Current);
        Assert.Equal(Verbs.Locked, interactor.Current!.Verb);
        h.PressInteract();
        Assert.Equal(1, apartment.FrontDoor!.Attempts);
        h.Hold("move_forward");
        h.RunSeconds(2f);
        h.Release("move_forward");
        Assert.True(player.GlobalPosition.Z > -5.5f + 0.25f, $"the player got through the front door to {player.GlobalPosition}");
    }

    [Fact]
    public void ShutDoorsBlockAndOpenOnesLetThePlayerThrough()
    {
        using var h = Load(out var apartment);
        var player = AddPlayer(h, new Vector2(-0.9f, -3.15f), 90f); // in front of the bathroom door, facing west
        h.Hold("move_forward");
        h.RunSeconds(2f);
        h.Release("move_forward");
        Assert.True(player.GlobalPosition.X > -1.5f, $"the shut bathroom door let the player through to {player.GlobalPosition}");
        ((Door)apartment.Doors["Door_Bath"]).Toggle();
        h.RunSeconds(1f);
        h.Hold("move_forward");
        h.RunSeconds(2f);
        h.Release("move_forward");
        Assert.True(player.GlobalPosition.X < -2.5f, $"the open bathroom door kept the player at {player.GlobalPosition}");
    }
}
