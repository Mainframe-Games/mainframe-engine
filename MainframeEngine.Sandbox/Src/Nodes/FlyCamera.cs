using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Sandbox;

/// <summary>
/// Free-fly camera: hold the right mouse button to look around, WASD to move, Q/E down/up, Shift for double
/// speed. Driven by tree input events (<see cref="Node.OnInput"/>) and <see cref="Node.OnProcess"/>.
/// </summary>
public sealed class FlyCamera : Camera3D
{
    private readonly HashSet<Key> _keysDown = [];
    private bool _looking;
    private Vector2? _lastMouse;
    private float _yaw = -90f;
    private float _pitch;

    /// <summary>Units per second (doubled with Shift).</summary>
    [Export(Range = "0,1000,0.1")]
    public float Speed { get; set; } = 10f;

    /// <summary>Degrees per pixel of mouse movement.</summary>
    [Export(Range = "0.001,1,0.001")]
    public float LookSensitivity { get; set; } = 0.1f;

    protected override void OnReady()
    {
        base.OnReady();
        // Start yaw/pitch from the scene's orientation.
        var forward = GlobalForward;
        _pitch = float.RadiansToDegrees(MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)));
        _yaw = float.RadiansToDegrees(MathF.Atan2(forward.Z, forward.X));
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        switch (inputEvent)
        {
            case InputEventKey key:
                if (key.Pressed)
                    _keysDown.Add(key.Key);
                else
                    _keysDown.Remove(key.Key);
                break;
            case InputEventMouseButton { Button: MouseButton.Right } button:
                _looking = button.Pressed;
                _lastMouse = null;
                break;
            case InputEventMouseMotion motion when _looking:
                if (_lastMouse is { } last)
                    Look((motion.Position.X - last.X) * LookSensitivity, (motion.Position.Y - last.Y) * LookSensitivity);
                _lastMouse = motion.Position;
                break;
        }
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!_looking)
            return;

        var speed = (_keysDown.Contains(Key.ShiftLeft) ? Speed * 2 : Speed) * gameTime.DeltaTime;
        var forward = GlobalForward;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var move = Vector3.Zero;
        if (_keysDown.Contains(Key.W)) move += forward;
        if (_keysDown.Contains(Key.S)) move -= forward;
        if (_keysDown.Contains(Key.D)) move += right;
        if (_keysDown.Contains(Key.A)) move -= right;
        if (_keysDown.Contains(Key.E)) move += Vector3.UnitY;
        if (_keysDown.Contains(Key.Q)) move -= Vector3.UnitY;
        if (move != Vector3.Zero)
            GlobalPosition += move * speed;
    }

    private void Look(float yawDelta, float pitchDelta)
    {
        _yaw += yawDelta;
        _pitch = Math.Clamp(_pitch - pitchDelta, -89f, 89f);
        var (sinYaw, cosYaw) = MathF.SinCos(float.DegreesToRadians(_yaw));
        var (sinPitch, cosPitch) = MathF.SinCos(float.DegreesToRadians(_pitch));
        LookAt(GlobalPosition + new Vector3(cosYaw * cosPitch, sinPitch, sinYaw * cosPitch));
    }
}
