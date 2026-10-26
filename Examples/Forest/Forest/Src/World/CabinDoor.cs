using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The cabin's plank door on its hinge: this node stands on the hinge axis and turns about Y; the door's mesh and its
/// collision box are its children, so they swing with it. In the game, the <c>interact</c> action (E, gamepad X) opens or
/// shuts it when the player stands within <see cref="Reach"/> and looks at it, and <see cref="InteractPrompt"/> shows
/// what the action would do. The swing is eased over <see cref="SwingSeconds"/>; nothing allocates per frame.
/// </summary>
public sealed class CabinDoor : Node3D
{
    /// <summary>The hinge's turn when open, in degrees (its sign picks the way the door swings).</summary>
    [Export(Range = "-180,180,1")] public float OpenDegrees { get; set; } = 100f;

    /// <summary>How long a full swing takes.</summary>
    [Export(Range = "0.05,3,0.05")] public float SwingSeconds { get; set; } = 0.7f;

    /// <summary>How near the player's eyes must be to the door's middle to use it, metres.</summary>
    [Export(Range = "0.5,5,0.05")] public float Reach { get; set; } = 2.4f;

    /// <summary>The door's middle in the hinge's space (where the player must look).</summary>
    public Vector3 Middle { get; set; } = new(0.45f, 1f, 0f);

    /// <summary>True when the door is (or is swinging) open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The swing, 0 (shut) to 1 (open).</summary>
    public float Openness { get; private set; }

    /// <summary>Run the interaction (the game only; the editor and the asset gallery just show the door).</summary>
    public bool Interactive { get; set; }

    private FirstPersonController? _player;
    private InteractPrompt? _prompt;
    private int _lookup;

    /// <summary>Opens a shut door or shuts an open one.</summary>
    public void Toggle() => IsOpen = !IsOpen;

    /// <summary>Whether a player whose eyes are at <paramref name="eye"/>, looking along <paramref name="forward"/>, can use the door.</summary>
    public bool InReach(Vector3 eye, Vector3 forward)
    {
        var middle = GlobalTransform.TransformPoint(Middle);
        var offset = middle - eye;
        var distance = offset.Length();
        return distance < Reach && distance > 1e-3f && Vector3.Dot(offset / distance, forward) > 0.7f;
    }

    protected override void OnReady()
    {
        if (!Interactive || Tree is not { } tree)
            return;
        FindPlayer();
        if (tree.Servers.Get<UiServer>() is not null) // headless runs (--bake-lighting, servers) have no UI
            _prompt = InteractPrompt.Attach(this);
    }

    /// <summary>The player, found once (and looked for again about once a second until there is one).</summary>
    private void FindPlayer()
    {
        if (Tree?.Root.FindChildren<FirstPersonController>(owned: false) is [var player, ..])
            _player = player;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Interactive && _player is null && ++_lookup % 60 == 1)
            FindPlayer();
        if (Interactive && _player is { Camera: { } camera } && Tree is { } tree)
        {
            var eye = camera.GlobalPosition;
            var forward = -camera.GlobalTransform.Basis.Z;
            var reach = _player.ProcessMode != ProcessMode.Disabled && InReach(eye, Vector3.Normalize(forward));
            if (reach && tree.Input.IsActionJustPressed("interact"))
                Toggle();
            _prompt?.Show(reach, IsOpen ? InteractPrompt.Close : InteractPrompt.Open);
        }

        var target = IsOpen ? 1f : 0f;
        if (Openness == target)
            return;
        var step = gameTime.DeltaTime / MathF.Max(SwingSeconds, 1e-3f);
        Openness = target > Openness ? MathF.Min(Openness + step, 1f) : MathF.Max(Openness - step, 0f);
        RotationDegrees = new Vector3(0f, OpenDegrees * ValleyNoise.SmoothStep(0f, 1f, Openness), 0f);
    }
}
