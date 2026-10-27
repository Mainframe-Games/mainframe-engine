using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// The computer room's chair: <c>interact</c> sits the player down facing the monitors (the camera eases to
/// <see cref="EyeHeight"/> over the seat, the controller stops moving the player) and <c>interact</c> or <c>pause</c>
/// stands them back up where they were. While seated <see cref="IsSeated"/> is true and <see cref="Seated"/> /
/// <see cref="Stood"/> tell the terminal to take or give back the keyboard. This node is the seat: its −Z faces the
/// screens.
/// </summary>
public sealed class ComputerSeat : Node3D, IInteractable
{
    private FirstPersonController? _player;
    private Vector3 _standPosition;
    private float _standYaw;
    private float _standPitch;
    private float _blend;
    private bool _sitting;
    private bool _pressedThisFrame;

    /// <summary>The seated eye height over the seat's floor, metres.</summary>
    [Export(Range = "0.5,2,0.01")] public float EyeHeight { get; set; } = 1.18f;

    /// <summary>Seconds to sit down or stand up.</summary>
    [Export(Range = "0.05,3,0.05")] public float Seconds { get; set; } = 0.6f;

    public bool IsSeated => _sitting;

    /// <summary>The player sat down.</summary>
    [Signal] public event Action? Seated;

    /// <summary>The player stood up.</summary>
    [Signal] public event Action? Stood;

    public string Verb => Verbs.Sit;

    public bool CanInteract => !_sitting;

    /// <summary>Whether the terminal should hold on to the keys this frame (<c>interact</c>/<c>pause</c> stand up).</summary>
    public bool StandRequested { get; set; }

    public void Interact()
    {
        if (_sitting || FindPlayer() is not { } player)
            return;
        _player = player;
        _standPosition = player.Position;
        _standYaw = player.YawDegrees;
        _standPitch = player.PitchDegrees;
        _sitting = true;
        _pressedThisFrame = true;
        player.MovementLocked = true;
        Seated?.Invoke();
    }

    /// <summary>Stands the player up (as <c>interact</c> or <c>pause</c> do while seated).</summary>
    public void StandUp()
    {
        if (!_sitting)
            return;
        _sitting = false;
        Stood?.Invoke();
    }

    /// <summary>The interact box round the chair.</summary>
    public StaticBody3D AddBody(Vector3 size, Vector3 offset)
    {
        var body = new StaticBody3D { Name = "Body", CollisionLayer = NeonLayers.Interact, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = offset });
        AddChild(body);
        return body;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_player is not { } player || Tree is not { } tree)
            return;
        if (_sitting && !_pressedThisFrame &&
            (StandRequested || tree.Input.IsActionJustPressed("interact") || tree.Input.IsActionJustPressed("pause")))
            StandUp();
        _pressedThisFrame = false;
        StandRequested = false;

        var target = _sitting ? 1f : 0f;
        if (_blend == target)
            return;
        var step = gameTime.DeltaTime / MathF.Max(Seconds, 1e-3f);
        _blend = target > _blend ? MathF.Min(_blend + step, 1f) : MathF.Max(_blend - step, 0f);
        var e = DoorBase.SmoothStep(_blend);

        // The feet move to the seat; the eye drops to the seated height; the view turns to the screens.
        var seat = GlobalPosition;
        var seatYaw = SeatYawDegrees();
        player.Position = Vector3.Lerp(_standPosition, new Vector3(seat.X, _standPosition.Y, seat.Z), e);
        player.EyeHeightOverride = _blend > 0f ? float.Lerp(player.StandingHeight - FirstPersonController.EyeBelowTop, EyeHeight, e) : null;
        player.SetLook(LerpAngle(_standYaw, seatYaw, e), float.Lerp(_standPitch, -8f, e));
        if (_blend == 0f)
            player.MovementLocked = false;
    }

    private float SeatYawDegrees()
    {
        var forward = -GlobalTransform.Basis.Z;
        return float.RadiansToDegrees(MathF.Atan2(-forward.X, -forward.Z));
    }

    private static float LerpAngle(float a, float b, float t)
    {
        var d = MathF.IEEERemainder(b - a, 360f);
        return a + d * t;
    }

    private FirstPersonController? FindPlayer()
    {
        if (Tree?.CurrentScene is not { } scene)
            return null;
        return scene.FindChildren<FirstPersonController>(owned: false).FirstOrDefault();
    }
}
