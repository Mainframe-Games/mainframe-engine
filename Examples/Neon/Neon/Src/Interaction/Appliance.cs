using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// Something with an on/off state the player toggles with <c>interact</c>: the shower, the taps, the TV, the stereo.
/// Subclasses react in <see cref="OnSwitched"/>; <see cref="Switched"/> tells others (the audio). The interact box is a
/// small collider on <see cref="NeonLayers.Interact"/> only, so it never blocks the player.
/// </summary>
public abstract class Appliance : Node3D, IInteractable
{
    /// <summary>Whether it is running.</summary>
    [Export] public bool IsOn { get; set; }

    /// <summary>Raised after <see cref="IsOn"/> changed through <see cref="SetOn"/> (true = turned on).</summary>
    [Signal] public event Action<bool>? Switched;

    public virtual string Verb => IsOn ? Verbs.TurnOff : Verbs.TurnOn;

    public bool CanInteract => true;

    public virtual void Interact() => SetOn(!IsOn);

    /// <summary>Turns it on or off (nothing happens when already in that state).</summary>
    public void SetOn(bool on)
    {
        if (IsOn == on)
            return;
        IsOn = on;
        OnSwitched(on);
        Switched?.Invoke(on);
    }

    /// <summary>The interact box (interact layer only), <paramref name="size"/> metres, centred at <paramref name="offset"/>.</summary>
    public StaticBody3D AddBody(Vector3 size, Vector3 offset = default)
    {
        var body = new StaticBody3D { Name = "Body", CollisionLayer = NeonLayers.Interact, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = offset });
        AddChild(body);
        return body;
    }

    protected override void OnReady() => OnSwitched(IsOn);

    /// <summary>Applies the state (also once when ready, for the initial state).</summary>
    protected abstract void OnSwitched(bool on);
}
