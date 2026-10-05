using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The lit-shapes scene with a camera that mouse-looks while the right button is held (the free-fly camera's look
/// logic). With <c>--input &lt;frame&gt;</c> the host pushes a synthetic SDL right-drag, and the scene checks that the
/// camera turned and stopped looking when the button was released: SDL → Silk <c>IMouse</c> → <c>InputRouter</c> → node.
/// </summary>
public sealed class MouseLookScene(HostOptions host) : LitShapesScene(host)
{
    private MouseLookCamera _look = null!;
    private Vector3 _forwardBefore;
    private bool _checked;

    protected override void AddNodes(Node scene)
    {
        base.AddNodes(scene);
        _look = new MouseLookCamera { Name = "LookCamera", Current = true, Position = Camera.Position };
        scene.AddChild(_look);
        _look.LookAt(new Vector3(0, 0.5f, 0));
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        base.UpdateScene(gameTime);
        var start = Host.InputAtFrame;
        if (start == 0)
            return;
        if (gameTime.FrameCount == start)
            _forwardBefore = _look.GlobalForward;

        // The button-up is pushed on the drag's last frame and handled by the next event poll.
        if (!_checked && gameTime.FrameCount >= start + InputFrames + 2)
        {
            _checked = true;
            var turned = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(_forwardBefore, _look.GlobalForward), -1f, 1f)));
            if (turned < 5f)
                Fail($"The camera turned {turned:F2} degrees after the synthetic right-drag; expected the drag to look around.");
            if (_look.Looking)
                Fail("The camera still looks after the synthetic button release.");
            if (_look.Motions < 10)
                Fail($"Only {_look.Motions} mouse-motion events reached the camera.");
        }
    }

    /// <summary>Hold the right mouse button to look around: yaw and pitch by <see cref="LookSensitivity"/> degrees per pixel.</summary>
    private sealed class MouseLookCamera : Camera3D
    {
        private const float LookSensitivity = 0.1f;
        private Vector2? _lastMouse;
        private float _yaw;
        private float _pitch;

        public bool Looking { get; private set; }
        public int Motions { get; private set; }

        protected override void OnReady()
        {
            base.OnReady();
            var forward = GlobalForward;
            _pitch = float.RadiansToDegrees(MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)));
            _yaw = float.RadiansToDegrees(MathF.Atan2(forward.Z, forward.X));
        }

        protected override void OnInput(InputEvent inputEvent)
        {
            switch (inputEvent)
            {
                case InputEventMouseButton { Button: MouseButton.Right } button:
                    Looking = button.Pressed;
                    _lastMouse = null;
                    break;
                case InputEventMouseMotion motion when Looking:
                    Motions++;
                    if (_lastMouse is { } last)
                        Look((motion.Position.X - last.X) * LookSensitivity, (motion.Position.Y - last.Y) * LookSensitivity);
                    _lastMouse = motion.Position;
                    break;
            }
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
}
