using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// The player's hand: a child of the <see cref="FirstPersonController"/> that casts one ray per frame from the camera,
/// <see cref="Reach"/> metres along the view, against <see cref="NeonLayers.World"/> (walls hide what is behind them)
/// and <see cref="NeonLayers.Interact"/>. When the hit collider or one of its ancestors is an
/// <see cref="IInteractable"/>, that is <see cref="Current"/>: the prompt (<see cref="InteractPrompt"/>) shows its verb
/// and <c>interact</c> uses it. Nothing allocates per frame.
/// </summary>
public sealed class Interactor : Node
{
    private FirstPersonController? _player;
    private InteractPrompt? _prompt;

    /// <summary>How far the player reaches from the eyes, metres.</summary>
    [Export(Range = "0.5,5,0.05")] public float Reach { get; set; } = 2.2f;

    /// <summary>The layers the ray tests.</summary>
    public uint Mask { get; set; } = NeonLayers.World | NeonLayers.Interact;

    /// <summary>What the player is looking at within reach (null when nothing usable is).</summary>
    public IInteractable? Current { get; private set; }

    /// <summary>The number of actions run (each <c>interact</c> press on something usable).</summary>
    public int Interactions { get; private set; }

    protected override void OnReady()
    {
        _player = Parent as FirstPersonController;
        if (Tree is { } tree && tree.Servers.Get<UiServer>() is not null) // headless runs and tests have no UI
            _prompt = InteractPrompt.Attach(this);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_player is not { Camera: { } camera } player || Tree is not { } tree)
            return;
        Current = player.ProcessMode == ProcessMode.Disabled ? null : Find(player, camera);
        if (Current is { CanInteract: true } target && tree.Input.IsActionJustPressed("interact"))
        {
            target.Interact();
            Interactions++;
        }

        _prompt?.Show(Current is not null, Current?.Verb ?? Verbs.Open);
    }

    private IInteractable? Find(FirstPersonController player, Camera3D camera)
    {
        if (player.GetWorld3D() is not { } world)
            return null;
        var eye = camera.GlobalPosition;
        var forward = Vector3.Normalize(-camera.GlobalTransform.Basis.Z);
        if (!world.DirectSpaceState.RayCast(eye, eye + forward * Reach, out var hit, Mask, player))
            return null;
        for (Node? n = hit.Collider; n is not null; n = n.Parent)
            if (n is IInteractable interactable)
                return interactable;
        return null;
    }
}
