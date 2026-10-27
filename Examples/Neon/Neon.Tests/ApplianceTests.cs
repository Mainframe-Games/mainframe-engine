using System.Numerics;
using MainframeEngine;

namespace Neon.Tests;

/// <summary>
/// The apartment's things to use (N2): the shower and its misting glass, the taps, the TV's channels, the stereo, the
/// blinds and their panels, and sitting at the computer, each found and used as the player would (looking at it and
/// pressing <c>interact</c>). Skipped when the model's LFS buffer is not checked out.
/// </summary>
public sealed class ApplianceTests
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

    /// <summary>A player standing at <paramref name="feet"/> (XZ, on the given floor) looking at <paramref name="target"/>.</summary>
    private static FirstPersonController StandLookingAt(ControllerHarness h, Vector2 feet, Vector3 target, float floor = 0f)
    {
        var player = h.AddPlayer(new Vector3(feet.X, floor, feet.Y));
        h.RunSeconds(0.3f);
        LookAt(player, target);
        h.Run(2);
        return player;
    }

    private static void LookAt(FirstPersonController player, Vector3 target)
    {
        var eye = player.Camera!.GlobalPosition;
        var d = target - eye;
        var yaw = float.RadiansToDegrees(MathF.Atan2(-d.X, -d.Z));
        var pitch = float.RadiansToDegrees(MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z)));
        player.SetLook(yaw, pitch);
    }

    private static string? Prompt(FirstPersonController player) => player.GetNode<Interactor>("Interactor").Current?.Verb;

    [Fact]
    public void TheShowerValveRunsTheWaterAndMistsTheGlass()
    {
        using var h = Load(out var apartment);
        var shower = apartment.Shower!;
        var player = StandLookingAt(h, new Vector2(-5.0f, -3.4f), new Vector3(-5.25f, 1.15f, -5.44f));
        Assert.Equal(Verbs.TurnOn, Prompt(player));
        h.PressInteract();
        Assert.True(shower.IsOn);
        Assert.True(shower.GetNode<GpuParticles3D>("Water").Emitting);
        Assert.True(shower.GetNode<GpuParticles3D>("Steam").Emitting);
        Assert.Equal(Verbs.TurnOff, Prompt(player));
        var clear = shower.Glass!.AlbedoColor.A;
        h.RunSeconds(10f);
        Assert.True(shower.Mist > 0.4f, $"mist {shower.Mist}");
        Assert.True(shower.Glass.AlbedoColor.A > clear);
        h.PressInteract();
        Assert.False(shower.IsOn);
        h.RunSeconds(15f);
        Assert.Equal(0f, shower.Mist);
        Assert.Equal(clear, shower.Glass.AlbedoColor.A);
    }

    [Fact]
    public void TheKitchenTapRuns()
    {
        using var h = Load(out var apartment);
        var tap = apartment.Taps["Kitchen"];
        StandLookingAt(h, new Vector2(1.7f, -4.3f), tap.GlobalPosition);
        h.PressInteract();
        Assert.True(tap.IsOn);
        Assert.True(tap.GetNode<GpuParticles3D>("Water").Emitting);
        h.PressInteract();
        Assert.False(tap.IsOn);
    }

    [Fact]
    public void TheTvFlipsThroughItsChannelsThenTurnsOff()
    {
        using var h = Load(out var apartment);
        var tv = apartment.Tv!;
        var player = StandLookingAt(h, new Vector2(5.7f, 2.0f), new Vector3(6.9f, 1.5f, 2.0f));
        var channels = new List<int>();
        tv.ChannelChanged += channels.Add;
        Assert.Equal(Verbs.TurnOn, Prompt(player));
        h.PressInteract();
        Assert.True(tv.IsOn);
        Assert.True(tv.Screen!.EmissionEnergy > 0f);
        Assert.Equal(Verbs.NextChannel, Prompt(player));
        h.PressInteract();
        h.PressInteract();
        Assert.Equal([1, 2], channels);
        Assert.Equal(Verbs.TurnOff, Prompt(player));
        h.PressInteract();
        Assert.False(tv.IsOn);
        Assert.Equal(0f, tv.Screen.EmissionEnergy);
    }

    [Fact]
    public void TheStereoPlaysSkipsAndStops()
    {
        using var h = Load(out var apartment);
        var stereo = apartment.Stereo!;
        var tracks = new List<int>();
        stereo.TrackChanged += tracks.Add;
        var player = StandLookingAt(h, new Vector2(5.9f, 1.6f), stereo.GlobalPosition);
        Assert.Equal(Verbs.PlayMusic, Prompt(player));
        h.PressInteract();
        h.PressInteract();
        h.PressInteract();
        Assert.Equal([0, 1, -1], tracks);
        Assert.False(stereo.IsOn);
        stereo.Play(1);
        Assert.True(stereo.IsOn);
        Assert.Equal(1, stereo.Track);
    }

    [Fact]
    public void TheLivingRoomPanelClosesAndOpensItsBlinds()
    {
        using var h = Load(out var apartment);
        var panel = apartment.BlindsPanels["Living"];
        Assert.Equal(3, panel.Blinds.Count);
        Assert.All(apartment.Blinds.Values, b => Assert.Equal(1f, b.Openness));
        var player = StandLookingAt(h, new Vector2(1.6f, 4.6f), panel.GlobalPosition);
        Assert.Equal(Verbs.CloseBlinds, Prompt(player));
        h.PressInteract();
        h.RunSeconds(4f);
        Assert.All(panel.Blinds, b => Assert.Equal(0f, b.Openness));
        Assert.Equal(1f, apartment.Blinds["Blinds_Computer_1"].Openness); // another room's
        h.PressInteract();
        h.RunSeconds(4f);
        Assert.All(panel.Blinds, b => Assert.Equal(1f, b.Openness));
    }

    [Fact]
    public void SittingAtTheComputerLocksTheBodyAndStandingUpPutsItBack()
    {
        using var h = Load(out var apartment);
        var seat = apartment.Seat!;
        var player = StandLookingAt(h, new Vector2(-4.0f, 2.0f), new Vector3(-5.2f, 0.7f, 2.0f));
        var start = player.Position;
        Assert.Equal(Verbs.Sit, Prompt(player));
        h.PressInteract();
        Assert.True(seat.IsSeated);
        Assert.True(player.MovementLocked);
        h.RunSeconds(1f);
        Assert.True(MathF.Abs(player.Position.X - seat.GlobalPosition.X) < 0.01f);
        Assert.Equal(seat.EyeHeight, player.Head!.Position.Y, 2);
        Assert.Null(player.GetNode<Interactor>("Interactor").Current); // no prompt while seated
        h.Hold("move_forward");
        h.RunSeconds(0.5f);
        h.Release("move_forward");
        Assert.True(MathF.Abs(player.Position.X - seat.GlobalPosition.X) < 0.01f); // seated: no walking off

        h.PressInteract();
        Assert.False(seat.IsSeated);
        h.RunSeconds(1f);
        Assert.False(player.MovementLocked);
        Assert.True(Vector3.Distance(player.Position, start) < 0.05f);
    }

    [Fact]
    public void RunningEverythingAllocatesNothingPerFrame()
    {
        using var h = Load(out var apartment);
        h.AddPlayer(new Vector3(2f, 0f, 2f));
        apartment.Shower!.SetOn(true);
        apartment.Taps["Kitchen"].SetOn(true);
        apartment.Tv!.Interact();
        apartment.Stereo!.Interact();
        apartment.BlindsPanels["Living"].Interact();
        h.RunSeconds(1f);
        // The smallest of a few windows (a stray runtime allocation in one window is not the game's).
        var smallest = long.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            h.Run(30);
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, smallest);
    }
}
