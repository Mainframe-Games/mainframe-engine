using System.Numerics;
using MainframeEngine;

namespace Forest.Tests;

public sealed class FirstPersonControllerTests
{
    private static float HorizontalSpeed(FirstPersonController player)
    {
        var v = player.GetRealVelocity();
        return MathF.Sqrt(v.X * v.X + v.Z * v.Z);
    }

    [Theory]
    [InlineData(null, 2.5f)]
    [InlineData("sprint", 5f)]
    [InlineData("crouch", 1.3f)]
    public void WalkSprintAndCrouchSpeeds(string? modifier, float expected)
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        h.Run(10);
        h.Hold("move_forward");
        if (modifier is not null)
            h.Hold(modifier);
        h.RunSeconds(1.5f);
        Assert.Equal(expected, HorizontalSpeed(player), 1);
        Assert.True(player.GlobalPosition.Z < -1f, "yaw 0 walks along −Z");
        Assert.Equal(0f, player.GlobalPosition.X, 2);
        Assert.Equal(modifier == "crouch", player.IsCrouching);
        Assert.Equal(modifier == "sprint", player.IsSprinting);
    }

    [Fact]
    public void AcceleratesAtAccelerationOnTheFloor()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        h.Run(10);
        h.Hold("move_forward");
        h.Run(6); // 0.1 s at 14 m/s²
        Assert.InRange(HorizontalSpeed(player), 1.2f, 1.6f);
    }

    [Fact]
    public void MovesInTheHeadsYawFrame()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer(yawDegrees: 90f); // turned left: forward is −X
        h.Run(10);
        h.Hold("move_forward");
        h.RunSeconds(1f);
        Assert.True(player.GlobalPosition.X < -1f);
        Assert.Equal(0f, player.GlobalPosition.Z, 1);
        h.Release("move_forward");
        h.Hold("move_right"); // right of −X is −Z
        h.RunSeconds(1f);
        Assert.True(player.GlobalPosition.Z < -0.5f);
    }

    [Fact]
    public void JumpReachesVSquaredOverTwoG()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        h.Run(20);
        var ground = player.GlobalPosition.Y;
        h.Hold("jump");
        h.Run(1);
        h.Release("jump");
        var top = ground;
        for (var i = 0; i < 90; i++)
        {
            h.Run(1);
            top = MathF.Max(top, player.GlobalPosition.Y);
        }

        var expected = player.JumpVelocity * player.JumpVelocity / (2 * player.Gravity); // ≈ 0.9 m
        // The take-off step has no gravity yet, so the discrete arc lands within a few centimetres above v² / 2g.
        Assert.InRange(top - ground, expected - 0.05f, expected + 0.05f);
        Assert.True(player.IsOnFloor());
    }

    [Fact]
    public void CoyoteTimeAllowsAJumpJustAfterAnEdge()
    {
        foreach (var (delayFrames, jumps) in new[] { (3, true), (15, false) })
        {
            using var h = new ControllerHarness(floor: false);
            h.AddBox(new Vector3(0, -0.5f, 0), new Vector3(4, 1, 4), name: "Ledge");
            var player = h.AddPlayer(new Vector3(0, 0, 1.5f));
            h.Run(10);
            h.Hold("move_forward"); // yaw 0 walks −Z: off the ledge at z = −2
            var frames = 0;
            while (player.IsOnFloor() && frames++ < 240)
                h.Run(1);
            Assert.False(player.IsOnFloor());
            h.Run(delayFrames);
            h.Hold("jump");
            h.Run(1);
            h.Release("jump");
            Assert.Equal(jumps, player.Velocity.Y > 3f);
        }
    }

    [Fact]
    public void JumpPressedJustBeforeLandingIsBuffered()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer(new Vector3(0, 1.5f, 0));
        // Falls ≈ 1.5 m: about 33 frames. Press jump 3 frames before touching down.
        var frames = 0;
        while (player.GlobalPosition.Y > 0.12f && frames++ < 120)
            h.Run(1);
        h.Hold("jump");
        h.Run(1);
        h.Release("jump");
        var rose = false;
        for (var i = 0; i < 12; i++)
        {
            h.Run(1);
            rose |= player.Velocity.Y > 3f;
        }

        Assert.True(rose);
    }

    [Theory]
    [InlineData(30f, true)]
    [InlineData(55f, false)]
    public void SlopesSteeperThanFloorMaxAngleStopThePlayer(float degrees, bool climbs)
    {
        using var h = new ControllerHarness();
        h.AddRamp(degrees);
        var player = h.AddPlayer();
        h.Run(10);
        h.Hold("move_forward");
        h.RunSeconds(4f);
        if (climbs)
            Assert.True(player.GlobalPosition.Y > 1.5f, $"climbed only {player.GlobalPosition.Y:0.00} m (at z = {player.GlobalPosition.Z:0.00})");
        else
            Assert.True(player.GlobalPosition.Y < 0.6f, $"climbed {player.GlobalPosition.Y:0.00} m up a {degrees}° slope");
    }

    [Fact]
    public void StandingUpWaitsForHeadroom()
    {
        using var h = new ControllerHarness();
        h.AddBox(new Vector3(0, 1.5f, -3), new Vector3(4, 0.4f, 2), name: "Beam"); // underside at 1.3 m
        var player = h.AddPlayer();
        h.Run(10);
        h.Hold("crouch");
        h.Hold("move_forward");
        h.RunSeconds(2.4f); // 1.3 m/s: under the beam's middle
        h.Release("move_forward");
        h.Run(10);
        Assert.InRange(player.GlobalPosition.Z, -3.9f, -2.1f);
        h.Release("crouch");
        h.Run(10);
        Assert.True(player.IsCrouching, "stood up under the beam");
        h.Hold("move_forward");
        h.RunSeconds(2f);
        h.Release("move_forward");
        h.Run(5);
        Assert.False(player.IsCrouching);
    }

    [Fact]
    public void CrouchToggleLatches()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        h.Run(5);
        h.Hold("crouch_toggle");
        h.Run(1);
        h.Release("crouch_toggle");
        h.Run(30);
        Assert.True(player.IsCrouching);
        Assert.InRange(player.Head!.Position.Y, player.CrouchHeight - 0.2f, player.CrouchHeight);
        h.Hold("crouch_toggle");
        h.Run(1);
        h.Release("crouch_toggle");
        h.Run(30);
        Assert.False(player.IsCrouching);
        Assert.Equal(player.StandingHeight - FirstPersonController.EyeBelowTop, player.Head!.Position.Y, 3);
    }

    [Fact]
    public void WadingSlowsTheWalk()
    {
        using var h = new ControllerHarness();
        h.Water.Register(new Pool(new Vector2(-50, -50), new Vector2(50, 50), surface: 0.75f, bed: 0f));
        var player = h.AddPlayer();
        h.Run(10);
        h.Hold("move_forward");
        h.RunSeconds(1.5f);
        var expected = player.WalkSpeed * WaterQueries.WadeSpeedScale(0.75f); // 0.7 × 2.5
        Assert.Equal(expected, HorizontalSpeed(player), 1);
        Assert.InRange(player.Immersion, 0.72f, 0.75f); // the feet rest a safe margin above the bed
        Assert.Equal("water", player.LastFootstepSurface);
    }

    [Fact]
    public void DeepWaterIsASoftWall()
    {
        using var h = new ControllerHarness();
        h.Water.Register(new Pool(new Vector2(-50, -50), new Vector2(50, -5), surface: 1.6f, bed: 0f)); // z < −5
        var player = h.AddPlayer();
        h.Run(10);
        h.Hold("move_forward");
        h.RunSeconds(5f);
        Assert.InRange(player.GlobalPosition.Z, -5.2f, -4f);
    }

    [Fact]
    public void FlowPushesTheWader()
    {
        using var h = new ControllerHarness();
        var curve = new Curve3D();
        curve.AddPoint(new Vector3(0, 0.5f, 20), @out: new Vector3(0, -0.5f, -13));
        curve.AddPoint(new Vector3(0, -1f, -20), new Vector3(0, 0.5f, 13));
        for (var i = 0; i < 2; i++)
        {
            curve.SetPointWidth(i, 6f);
            curve.SetPointDepth(i, 0.5f);
        }

        h.Scene.AddChild(new River3D { Name = "River", Curve = curve });
        var player = h.AddPlayer(new Vector3(0, 0, 0));
        h.RunSeconds(2f);
        var flow = h.Water.FlowAt(player.GlobalPosition);
        Assert.True(flow.Z < -0.5f);
        Assert.Equal(flow.Z * player.FlowPush, player.GetRealVelocity().Z, 1);
    }

    [Fact]
    public void MouseMotionTurnsOnlyWhileCaptured()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        h.Run(2);
        h.Tree.PushInput(new InputEventMouseMotion { Relative = new Vector2(100, 50) });
        Assert.Equal(0f, player.YawDegrees, 3);

        h.Input.MouseMode = MouseMode.Captured;
        h.Tree.PushInput(new InputEventMouseMotion { Relative = new Vector2(100, 50) });
        Assert.Equal(-10f, player.YawDegrees, 3);  // 0.1° per count, right turns negative yaw
        Assert.Equal(-5f, player.PitchDegrees, 3); // mouse down looks down

        player.InvertY = true;
        h.Tree.PushInput(new InputEventMouseMotion { Relative = new Vector2(0, 50) });
        Assert.Equal(0f, player.PitchDegrees, 3);

        h.Tree.PushInput(new InputEventMouseMotion { Relative = new Vector2(0, 100000) });
        Assert.Equal(89f, player.PitchDegrees, 3);
        player.InvertY = false;
        h.Tree.PushInput(new InputEventMouseMotion { Relative = new Vector2(0, 100000) });
        Assert.Equal(-89f, player.PitchDegrees, 3);
    }

    [Fact]
    public void PauseReleasesTheMouseAndAClickRecapturesIt()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer(captureMouse: true);
        Assert.Equal(MouseMode.Captured, h.Input.MouseMode);
        h.Tree.PushInput(new InputEventKey { Key = Silk.NET.Input.Key.Escape, Pressed = true });
        h.Run(1);
        h.Tree.PushInput(new InputEventKey { Key = Silk.NET.Input.Key.Escape, Pressed = false });
        Assert.Equal(MouseMode.Visible, h.Input.MouseMode);
        h.Tree.PushInput(new InputEventMouseButton { Button = Silk.NET.Input.MouseButton.Left, Pressed = true });
        Assert.Equal(MouseMode.Captured, h.Input.MouseMode);
        Assert.NotNull(player.Camera);
    }

    [Fact]
    public void GamepadLookHasARadialDeadzoneAndASquaredCurve()
    {
        Assert.Equal(Vector2.Zero, FirstPersonController.LookCurve(new Vector2(0.1f, 0.1f)));
        Assert.Equal(new Vector2(1, 0), FirstPersonController.LookCurve(new Vector2(1, 0)));
        var half = FirstPersonController.LookCurve(new Vector2(0, 0.575f)); // halfway past the deadzone
        Assert.Equal(0.25f, half.Y, 3);
        Assert.Equal(0f, half.X);

        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        h.Run(2);
        h.Tree.PushInput(new InputEventGamepadAxis { Device = 0, Axis = GamepadAxis.RightStick, Value = new Vector2(1, 0) });
        h.RunSeconds(0.5f);
        Assert.Equal(-player.GamepadLookSpeed * 0.5f, player.YawDegrees, 0);
        h.Tree.PushInput(new InputEventGamepadAxis { Device = 0, Axis = GamepadAxis.RightStick, Value = new Vector2(0, -1) });
        h.RunSeconds(0.25f);
        Assert.Equal(player.GamepadLookSpeed * 0.25f, player.PitchDegrees, 0); // stick up looks up
    }

    [Theory]
    [InlineData(90f, 16f / 9f, 58.7155f)]
    [InlineData(90f, 1f, 90f)]
    [InlineData(110f, 16f / 9f, 77.5521f)]
    public void HorizontalFovConvertsToTheCamerasVerticalFov(float horizontal, float aspect, float vertical) =>
        Assert.Equal(vertical, FirstPersonController.VerticalFov(horizontal, aspect), 2);

    [Fact]
    public void CameraFovFollowsHorizontalFov()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        h.Run(1);
        var size = h.Tree.Root.Size;
        var aspect = size.X > 0 && size.Y > 0 ? size.X / size.Y : 16f / 9f;
        Assert.Equal(FirstPersonController.VerticalFov(90f, aspect), player.Camera!.Fov, 2);
        player.HorizontalFov = 75f;
        h.Run(1);
        Assert.Equal(FirstPersonController.VerticalFov(75f, aspect), player.Camera!.Fov, 2);
    }

    [Fact]
    public void HeadBobFollowsTheWalkAndCanBeSwitchedOff()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        h.Run(10);
        h.Hold("move_forward");
        h.RunSeconds(0.5f);
        var maxY = 0f;
        var maxX = 0f;
        for (var i = 0; i < 120; i++)
        {
            h.Run(1);
            maxY = MathF.Max(maxY, MathF.Abs(player.HeadBobOffset.Y));
            maxX = MathF.Max(maxX, MathF.Abs(player.HeadBobOffset.X));
        }

        Assert.InRange(maxY, player.HeadBobAmount * 0.6f, player.HeadBobAmount * 1.05f);
        Assert.InRange(maxX, player.HeadBobAmount * 0.25f, player.HeadBobAmount * 0.55f);

        player.HeadBob = false;
        h.Run(2);
        Assert.Equal(Vector3.Zero, player.HeadBobOffset);
    }

    [Fact]
    public void OneFootstepPerStrideWithTheSurfaceUnderfoot()
    {
        using var h = new ControllerHarness(floor: false);
        h.AddBox(new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200), surface: "wood", name: "Deck");
        var player = h.AddPlayer();
        var heard = new List<string>();
        player.Footstep += heard.Add;
        h.Run(10);
        var landing = heard.Count; // touching down is a step
        Assert.Equal(1, landing);
        h.Hold("move_forward");
        h.RunSeconds(3f);
        var strides = (int)(player.DistanceWalked / player.StrideLength);
        Assert.InRange(heard.Count - landing, strides - 1, strides);
        Assert.InRange(strides, 8, 10); // ≈ 7.3 m at 2.5 m/s after accelerating
        Assert.All(heard, s => Assert.Equal("wood", s));
    }

    [Fact]
    public void SurfaceFallsBackToTheResolverThenTheDefault()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        h.Run(10);
        Assert.Equal("default", player.ResolveSurface());
        player.SurfaceResolver = p => p.X < 1 ? "moss" : null;
        Assert.Equal("moss", player.ResolveSurface());
        player.Position = new Vector3(5, 0.02f, 0);
        h.Run(5);
        Assert.Equal("default", player.ResolveSurface());
    }

    [Fact]
    public void SixHundredTicksOfWalkingAllocateNothing()
    {
        using var h = new ControllerHarness();
        h.Water.Register(new Pool(new Vector2(-50, -50), new Vector2(50, 0), surface: 0.5f, bed: 0f));
        h.AddBox(new Vector3(4, 0.5f, -3), new Vector3(1, 1, 1), surface: "wood");
        var player = h.AddPlayer();
        var steps = 0;
        player.Footstep += _ => steps++;
        h.Hold("move_forward");
        h.Hold("sprint", 1f);
        void Window()
        {
            for (var i = 0; i < 200; i++)
            {
                h.Run(1);
                player.AddLook(0.8f, 0);
                if (i == 50)
                    h.Hold("jump");
                if (i == 52)
                    h.Release("jump");
            }
        }

        Window(); // warm up: JIT, physics pools
        var smallest = long.MaxValue;
        for (var attempt = 0; attempt < 3 && smallest != 0; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var w = 0; w < 3; w++)
                Window();
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, smallest);
        Assert.True(steps > 10);
    }

    [Fact]
    public void SettingsApplyAndRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"forest-settings-{Guid.NewGuid():N}.json");
        try
        {
            new ForestSettings { HorizontalFov = 130f, MouseSensitivity = 0.2f, InvertY = true, GamepadLookSpeed = 90f, HeadBob = false }.Save(path);
            var loaded = ForestSettings.LoadOrDefault(path);
            Assert.True(loaded.InvertY);
            var player = new FirstPersonController();
            player.ApplySettings(loaded);
            Assert.Equal(110f, player.HorizontalFov); // clamped to 60–110
            Assert.Equal(0.2f, player.MouseSensitivity);
            Assert.Equal(90f, player.GamepadLookSpeed);
            Assert.False(player.HeadBob);
            player.Free();
            Assert.Equal(90f, ForestSettings.LoadOrDefault(path + ".missing").HorizontalFov);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
