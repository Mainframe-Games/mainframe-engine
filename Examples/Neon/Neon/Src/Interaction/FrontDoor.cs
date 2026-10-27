using MainframeEngine;

namespace Neon;

/// <summary>
/// The apartment's front door. It never opens: the prompt always says "Locked" and <c>interact</c> only counts the try
/// and raises <see cref="Rattled"/> (a buzz and a panel message come with the apartment's art and audio). Its body is on
/// the world and interact layers.
/// </summary>
public sealed class FrontDoor : Node3D, IInteractable
{
    /// <summary>How many times the player tried the door.</summary>
    public int Attempts { get; private set; }

    /// <summary>Raised on every try.</summary>
    [Signal] public event Action? Rattled;

    public string Verb => Verbs.Locked;

    public bool CanInteract => true;

    public void Interact()
    {
        Attempts++;
        Rattled?.Invoke();
    }
}
