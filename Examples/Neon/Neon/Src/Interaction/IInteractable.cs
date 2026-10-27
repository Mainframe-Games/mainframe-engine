using MainframeEngine;

namespace Neon;

/// <summary>
/// Something the player can use with the <c>interact</c> action (E, gamepad X): a door, a switch, later the shower, the
/// taps, the TV and the computer. <see cref="Interactor"/> finds it by casting a ray from the camera against
/// <see cref="NeonLayers.Interact"/>: the collider it hits, or one of the collider's ancestors, implements this.
/// </summary>
public interface IInteractable
{
    /// <summary>What the action would do, shown in the prompt ("Open", "Turn off", "Locked"). A constant string.</summary>
    string Verb { get; }

    /// <summary>Whether the action does anything now (the prompt is shown either way).</summary>
    bool CanInteract { get; }

    /// <summary>Runs the action.</summary>
    void Interact();
}

/// <summary>The collision layers the apartment uses.</summary>
public static class NeonLayers
{
    /// <summary>Layer 1: the shell, furniture, doors, the player (<see cref="CollisionLayers.Default"/>).</summary>
    public const uint World = CollisionLayers.Default;

    /// <summary>
    /// Layer 5: what <see cref="Interactor"/> looks for. Doors are on <see cref="World"/> too (they block the player);
    /// switches are only on this layer (with no mask), so the player walks through their small boxes.
    /// </summary>
    public const uint Interact = 1u << 4;
}

/// <summary>The verbs the prompt shows (constants, so the prompt only updates when they change).</summary>
public static class Verbs
{
    public const string Open = "Open";
    public const string Close = "Close";
    public const string Locked = "Locked";
    public const string TurnOn = "Turn on";
    public const string TurnOff = "Turn off";
    public const string NextChannel = "Next channel";
    public const string PlayMusic = "Play music";
    public const string NextTrack = "Next track";
    public const string StopMusic = "Stop music";
    public const string OpenBlinds = "Open blinds";
    public const string CloseBlinds = "Close blinds";
    public const string Sit = "Sit";
}
