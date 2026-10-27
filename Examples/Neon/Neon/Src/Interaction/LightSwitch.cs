using MainframeEngine;

namespace Neon;

/// <summary>
/// A room's light switch: <c>interact</c> turns its <see cref="Lights"/> on or off (their <see cref="Node3D.Visible"/>)
/// and the fixtures' glow with them (each <see cref="StandardMaterial3D"/> in <see cref="Fixtures"/> goes back to the
/// emission energy it had when it was added, or to 0). The switch's small box is on <see cref="NeonLayers.Interact"/>
/// only, so it does not block the player.
/// </summary>
public sealed class LightSwitch : Node3D, IInteractable
{
    private readonly List<StandardMaterial3D> _fixtures = [];
    private readonly List<float> _energies = [];

    /// <summary>The lights the switch controls.</summary>
    public List<Light3D> Lights { get; } = [];

    /// <summary>The emissive fixture materials the switch dims with the lights.</summary>
    public IReadOnlyList<StandardMaterial3D> Fixtures => _fixtures;

    /// <summary>Whether the room's lights are on.</summary>
    [Export] public bool IsOn { get; set; } = true;

    /// <summary>Raised when the player flips the switch (the new state), not when code calls <see cref="SetOn"/>: the click's hook.</summary>
    [Signal] public event Action<bool>? Switched;

    public string Verb => IsOn ? Verbs.TurnOff : Verbs.TurnOn;

    public bool CanInteract => true;

    public void Interact()
    {
        SetOn(!IsOn);
        Switched?.Invoke(IsOn);
    }

    /// <summary>Adds an emissive fixture material; its current energy is its "on" energy.</summary>
    public void AddFixture(StandardMaterial3D material)
    {
        if (_fixtures.Contains(material))
            return;
        _fixtures.Add(material);
        _energies.Add(material.EmissionEnergy);
        if (!IsOn)
            material.EmissionEnergy = 0f;
    }

    /// <summary>The switch's box (on the interact layer only), centred on the switch.</summary>
    public StaticBody3D AddBody(System.Numerics.Vector3 size)
    {
        var body = new StaticBody3D { Name = "Body", CollisionLayer = NeonLayers.Interact, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        AddChild(body);
        return body;
    }

    /// <summary>Turns the lights and fixtures on or off.</summary>
    public void SetOn(bool on)
    {
        IsOn = on;
        foreach (var light in Lights)
            light.Visible = on;
        for (var i = 0; i < _fixtures.Count; i++)
            _fixtures[i].EmissionEnergy = on ? _energies[i] : 0f;
    }

    protected override void OnReady() => SetOn(IsOn);
}
