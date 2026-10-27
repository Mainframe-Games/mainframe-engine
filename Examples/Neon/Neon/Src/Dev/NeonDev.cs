using System.Globalization;
using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// Game flags for looking around without playing (after <c>++</c>): <c>--view x,y,z,yaw[,pitch]</c> puts the player's
/// feet at (x, y, z) in the apartment, turned yaw degrees (0 looks along −Z, the front door; 180 the windows) and
/// pitched; <c>--open-doors</c> opens every door that opens; <c>--lights-off &lt;Room&gt;</c> flips a room's switch;
/// <c>--tv &lt;channel&gt;</c> turns the TV on at a channel; <c>--music</c>, <c>--shower</c> and <c>--blinds-closed</c> start
/// the stereo, run the shower and close every blind.
/// <see cref="NeonApartment"/> applies them when it is ready, before the player is.
/// </summary>
public static class NeonDev
{
    public static void Apply(NeonApartment apartment, IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--view" when i + 1 < args.Count && ParseView(args[++i]) is { } view:
                    if (apartment.Parent?.GetNodeOrNull<FirstPersonController>("Player") is { } player)
                    {
                        player.Position = view.Feet;
                        if (player.GetNodeOrNull<Node3D>("Head") is { } head)
                            head.RotationDegrees = new Vector3(view.Pitch, view.Yaw, 0f);
                    }

                    break;
                case "--open-doors":
                    foreach (var door in apartment.Doors.Values)
                        if (door is DoorBase { IsOpen: false } closed)
                            closed.Toggle();
                    break;
                case "--tv" when i + 1 < args.Count && int.TryParse(args[++i], CultureInfo.InvariantCulture, out var channel):
                    if (apartment.Tv is { } tv)
                    {
                        var tween = apartment.CreateTween();
                        tween.TweenInterval(0.5);
                        tween.TweenCallback(() =>
                        {
                            for (var c = 0; c <= channel; c++)
                                tv.Interact();
                        });
                    }

                    break;
                case "--music":
                    apartment.Stereo?.Play(0);
                    break;
                case "--shower":
                    apartment.Shower?.SetOn(true);
                    break;
                case "--blinds-closed":
                    foreach (var panel in apartment.BlindsPanels.Values)
                        panel.SetOn(false);
                    break;
                case "--lights-off" when i + 1 < args.Count:
                    if (apartment.Switches.TryGetValue(args[++i], out var lightSwitch))
                        lightSwitch.SetOn(false);
                    break;
            }
        }
    }

    /// <summary><c>x,y,z,yaw[,pitch]</c>, or null.</summary>
    public static (Vector3 Feet, float Yaw, float Pitch)? ParseView(string text)
    {
        var parts = text.Split(',');
        if (parts.Length is not (4 or 5))
            return null;
        var values = new float[5];
        for (var i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return null;
        return (new Vector3(values[0], values[1], values[2]), values[3], values[4]);
    }
}
