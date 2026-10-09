using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The Cameras page's free camera (ADR 0180): mouse or right-stick look, <c>move_*</c> to fly in the view's frame,
/// <c>jump</c> up, <c>crouch</c> down, <c>sprint</c> four times faster. No collision. A click recaptures the mouse.
/// Allocation-free per frame.
/// </summary>
public sealed class FreeFlyCamera : Camera3D
{
    private float _yaw;
    private float _pitch;

    /// <summary>Metres per second (× 4 with <c>sprint</c>).</summary>
    public float Speed { get; set; } = 6f;

    /// <summary>Degrees per mouse count.</summary>
    public float MouseSensitivity { get; set; } = 0.1f;

    /// <summary>Degrees per second at full right-stick deflection.</summary>
    public float GamepadLookSpeed { get; set; } = 140f;

    public bool InvertY { get; set; }

    /// <summary>Puts the camera at <paramref name="position"/> looking along <paramref name="forward"/> (no roll).</summary>
    public void SetPose(Vector3 position, Vector3 forward)
    {
        Position = position;
        if (forward.LengthSquared() > 1e-8f)
        {
            forward = Vector3.Normalize(forward);
            _yaw = MathF.Atan2(-forward.X, -forward.Z);
            _pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        }

        ApplyLook();
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        if (Tree is not { } tree)
            return;
        switch (inputEvent)
        {
            case InputEventMouseMotion motion when tree.Input.MouseMode == MouseMode.Captured:
                Turn(-motion.Relative.X * MouseSensitivity, -motion.Relative.Y * MouseSensitivity * (InvertY ? -1f : 1f));
                break;
            case InputEventMouseButton { Pressed: true } when tree.Input.MouseMode != MouseMode.Captured:
                tree.Input.MouseMode = MouseMode.Captured;
                break;
        }
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Tree is not { } tree)
            return;
        var input = tree.Input;
        var delta = gameTime.DeltaTime;
        var stick = FirstPersonController.LookCurve(input.GetVector("look_left", "look_right", "look_up", "look_down"));
        if (stick != Vector2.Zero)
            Turn(-stick.X * GamepadLookSpeed * delta, -stick.Y * GamepadLookSpeed * delta * (InvertY ? -1f : 1f));

        var move = input.GetVector("move_left", "move_right", "move_forward", "move_back");
        var rise = input.GetActionStrength("jump") - input.GetActionStrength("crouch");
        var rotation = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f);
        var forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        var right = Vector3.Transform(Vector3.UnitX, rotation);
        var velocity = right * move.X - forward * move.Y + Vector3.UnitY * rise;
        if (velocity.LengthSquared() > 1f)
            velocity = Vector3.Normalize(velocity);
        var speed = Speed * (input.IsActionPressed("sprint") ? 4f : 1f);
        Position += velocity * speed * delta;
    }

    private void Turn(float yawDegrees, float pitchDegrees)
    {
        _yaw = MathF.IEEERemainder(_yaw + float.DegreesToRadians(yawDegrees), MathF.Tau);
        _pitch = Math.Clamp(_pitch + float.DegreesToRadians(pitchDegrees), float.DegreesToRadians(-89f), float.DegreesToRadians(89f));
        ApplyLook();
    }

    private void ApplyLook() => Rotation = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f);
}
