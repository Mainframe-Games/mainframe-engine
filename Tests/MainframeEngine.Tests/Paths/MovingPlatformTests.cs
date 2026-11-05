using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Paths;

/// <summary>G13.5 in 3D: <see cref="AnimatableBody3D"/> platforms on paths and by code carry <see cref="CharacterBody3D"/>s.</summary>
public sealed class MovingPlatformTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>A body under gravity that jumps once when told to (its sideways velocity is left alone).</summary>
    private sealed class Rider : CharacterBody3D
    {
        public float JumpSpeed { get; set; }

        protected override void OnPhysicsProcess(float delta)
        {
            var velocity = Velocity;
            velocity.Y -= 9.81f * delta;
            if (JumpSpeed > 0 && IsOnFloor())
            {
                velocity.Y = JumpSpeed;
                JumpSpeed = 0;
            }

            Velocity = velocity;
            MoveAndSlide();
        }
    }

    /// <summary>Spins its node about Y by <see cref="DegreesPerSecond"/> on the physics tick.</summary>
    private sealed class Spinner : Node
    {
        public Node3D? Target { get; set; }
        public float DegreesPerSecond { get; set; }

        protected override void OnPhysicsProcess(float delta)
        {
            if (Target is { } target)
                target.RotationDegrees += new Vector3(0, DegreesPerSecond * delta, 0);
        }
    }

    // A platform (3 × 0.5 × 3 m box) on a follower along a straight path from `from` to `to`.
    private AnimatableBody3D PlatformOnPath(Vector3 from, Vector3 to, float speed, bool pingPong)
    {
        var curve = new Curve3D { BakeInterval = 0.05f };
        curve.AddPoint(from);
        curve.AddPoint(to);
        var path = _h.Add(new Path3D { Name = "Path", Curve = curve });
        var follow = new PathFollow3D
        {
            Name = "Follow",
            Speed = speed,
            Loop = false,
            PingPong = pingPong,
            RotationMode = PathRotationMode.None,
            ProcessCallback = PathProcessCallback.Physics,
        };
        path.AddChild(follow);
        var platform = new AnimatableBody3D { Name = "Platform" };
        platform.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3, 0.5f, 3) } });
        follow.AddChild(platform);
        return platform;
    }

    private Rider AddRider(Vector3 position)
    {
        var rider = _h.Add(new Rider { Name = "Rider", Position = position });
        rider.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.6f } });
        return rider;
    }

    [Fact]
    public void ARiderStaysOnALiftThatGoesUpAndDown()
    {
        _h.AddFloor(-5f);
        var lift = PlatformOnPath(Vector3.Zero, new Vector3(0, 4, 0), speed: 1.5f, pingPong: true);
        var rider = AddRider(new Vector3(0.4f, 1.1f, -0.2f));
        _h.RunSeconds(0.5f);
        var offset = rider.GlobalPosition - lift.GlobalPosition;
        for (var i = 0; i < 600; i++) // 10 s: three turns at the ends
        {
            _h.Run(1);
            var now = rider.GlobalPosition - lift.GlobalPosition;
            Assert.True(Vector3.Distance(now, offset) < 0.01f, $"frame {i}: offset {now} drifted from {offset}");
        }

        Assert.True(rider.IsOnFloor());
    }

    [Fact]
    public void ARiderIsCarriedSidewaysAndReportsThePlatformVelocity()
    {
        _h.AddFloor(-5f);
        var platform = PlatformOnPath(new Vector3(-6, 0, 0), new Vector3(6, 0, 0), speed: 2f, pingPong: true);
        var rider = AddRider(new Vector3(-5.5f, 1.1f, 0));
        _h.RunSeconds(0.5f);
        var offset = rider.GlobalPosition - platform.GlobalPosition;
        _h.RunSeconds(3f);
        Assert.True(Vector3.Distance(rider.GlobalPosition - platform.GlobalPosition, offset) < 0.01f);
        Assert.Equal(2f, rider.GetPlatformVelocity().X, 2);
        Assert.Equal(2f, platform.GetStepVelocity().X, 2);
    }

    [Fact]
    public void ARiderTurnsWithATurntableWithoutDriftingOutward()
    {
        var table = _h.Add(new AnimatableBody3D { Name = "Table" });
        table.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 4f, Height = 0.5f } });
        _h.Add(new Spinner { Name = "Spin", Target = table, DegreesPerSecond = 90f });
        var rider = AddRider(new Vector3(2f, 1.1f, 0));
        _h.RunSeconds(0.5f);
        var radius = new Vector2(rider.GlobalPosition.X, rider.GlobalPosition.Z).Length();
        var local = table.GlobalTransform.AffineInverse().TransformPoint(rider.GlobalPosition);
        _h.RunSeconds(10f);
        var now = new Vector2(rider.GlobalPosition.X, rider.GlobalPosition.Z);
        Assert.True(MathF.Abs(now.Length() - radius) < 0.01f, $"radius {now.Length()} vs {radius}");
        // The table turned another 900°; the rider stayed on the same spot of it.
        var expected = table.GlobalTransform.TransformPoint(local);
        Assert.True(Vector2.Distance(now, new Vector2(expected.X, expected.Z)) < 0.02f, $"{now} vs {expected}");
    }

    [Theory]
    [InlineData(PlatformOnLeave.AddVelocity, 2f, 2f)]
    [InlineData(PlatformOnLeave.AddUpwardVelocity, 2f, 0f)]
    [InlineData(PlatformOnLeave.DoNothing, 0f, 0f)]
    public void JumpingOffKeepsThePlatformsVelocityAsAsked(PlatformOnLeave mode, float addedUp, float addedSide)
    {
        _h.AddFloor(-10f);
        // A lift moving up and to the side at 2 m/s each, long enough not to turn.
        var platform = PlatformOnPath(Vector3.Zero, new Vector3(40, 40, 0), speed: 2f * MathF.Sqrt(2), pingPong: false);
        var rider = AddRider(new Vector3(0, 1.1f, 0));
        rider.PlatformOnLeave = mode;
        _h.RunSeconds(0.5f);
        Assert.True(rider.IsOnFloor());
        rider.JumpSpeed = 5f;
        _h.Run(2); // the jump frame, then the frame that leaves the platform
        Assert.False(rider.IsOnFloor());
        var g = 9.81f * PhysicsHarness3D.Step;
        Assert.Equal(5f + addedUp - g, rider.Velocity.Y, 1);
        Assert.Equal(addedSide, rider.Velocity.X, 1);
        Assert.NotNull(platform);
    }

    [Fact]
    public void FloorsOutsidePlatformFloorLayersDoNotCarry()
    {
        _h.AddFloor(-5f);
        var platform = PlatformOnPath(new Vector3(-6, 0, 0), new Vector3(6, 0, 0), speed: 2f, pingPong: false);
        platform.CollisionLayer = 2;
        var rider = AddRider(new Vector3(-6, 1.1f, 0));
        rider.CollisionMask = 3;
        rider.PlatformFloorLayers = 1;
        _h.RunSeconds(0.5f);
        var x = rider.GlobalPosition.X;
        _h.RunSeconds(0.5f);
        Assert.True(MathF.Abs(rider.GlobalPosition.X - x) < 0.05f, "the platform slid under the rider");
        Assert.Equal(Vector3.Zero, rider.GetPlatformVelocity());
    }

    [Fact]
    public void APlatformPushesADynamicBox()
    {
        _h.AddFloor();
        var platform = PlatformOnPath(new Vector3(-6, 0.3f, 0), new Vector3(6, 0.3f, 0), speed: 2f, pingPong: false);
        var box = _h.AddBox(new Vector3(0, 0.5f, 0));
        _h.RunSeconds(4f);
        Assert.True(box.GlobalPosition.X > 1f, $"box at {box.GlobalPosition}");
        Assert.NotNull(platform);
    }

    [Fact]
    public void AKinematicRigidBodyMovedByItsNodeReportsItsVelocity()
    {
        var body = _h.Add(new RigidBody3D { Name = "Kinematic", Mode = RigidBodyMode.Kinematic });
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
        _h.Run(1);
        for (var i = 0; i < 5; i++)
        {
            body.Position += new Vector3(0.05f, 0, 0); // 3 m/s at 60 Hz
            _h.Run(1);
        }

        Assert.Equal(3f, body.LinearVelocity.X, 2);
        body.LinearVelocity = new Vector3(0, 0, 1); // setting it back takes over
        Assert.Equal(new Vector3(0, 0, 1), body.LinearVelocity);
    }
}

/// <summary>G13.5 in 2D: <see cref="AnimatableBody2D"/> platforms carry <see cref="CharacterBody2D"/>s.</summary>
[Collection(nameof(SerialBox2D))]
public sealed class MovingPlatform2DTests : IDisposable
{
    private readonly PhysicsHarness2D _h = new();

    public void Dispose() => _h.Dispose();

    private sealed class Rider : CharacterBody2D
    {
        public float JumpSpeed { get; set; }

        protected override void OnPhysicsProcess(float delta)
        {
            var velocity = Velocity;
            velocity.Y += 980f * delta;
            if (JumpSpeed > 0 && IsOnFloor())
            {
                velocity.Y = -JumpSpeed;
                JumpSpeed = 0;
            }

            Velocity = velocity;
            MoveAndSlide();
        }
    }

    private AnimatableBody2D PlatformOnPath(Vector2 from, Vector2 to, float speed, bool pingPong)
    {
        var curve = new Curve2D { BakeInterval = 1f };
        curve.AddPoint(from);
        curve.AddPoint(to);
        var path = _h.Add(new Path2D { Name = "Path", Curve = curve });
        var follow = new PathFollow2D
        {
            Name = "Follow",
            Speed = speed,
            Loop = false,
            PingPong = pingPong,
            Rotates = false,
            ProcessCallback = PathProcessCallback.Physics,
        };
        path.AddChild(follow);
        var platform = new AnimatableBody2D { Name = "Platform" };
        platform.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(200, 20) } });
        follow.AddChild(platform);
        return platform;
    }

    private Rider AddRider(Vector2 position)
    {
        var rider = _h.Add(new Rider { Name = "Rider", Position = position });
        rider.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(30, 60) } });
        return rider;
    }

    [Fact]
    public void ARiderStaysOnALiftThatGoesUpAndDown()
    {
        _h.AddGround(600f);
        var lift = PlatformOnPath(Vector2.Zero, new Vector2(0, -300), speed: 120f, pingPong: true);
        var rider = AddRider(new Vector2(20, -41));
        _h.RunSeconds(0.5f);
        var offset = rider.GlobalPosition - lift.GlobalPosition;
        for (var i = 0; i < 600; i++)
        {
            _h.Run(1);
            var now = rider.GlobalPosition - lift.GlobalPosition;
            Assert.True(Vector2.Distance(now, offset) < 1f, $"frame {i}: offset {now} drifted from {offset}"); // 1 px = 1 cm
        }

        Assert.True(rider.IsOnFloor());
    }

    [Fact]
    public void ARiderIsCarriedSidewaysAndReportsThePlatformVelocity()
    {
        _h.AddGround(600f);
        var platform = PlatformOnPath(new Vector2(-500, 0), new Vector2(500, 0), speed: 150f, pingPong: true);
        var rider = AddRider(new Vector2(-480, -41));
        _h.RunSeconds(0.5f);
        var offset = rider.GlobalPosition - platform.GlobalPosition;
        _h.RunSeconds(3f);
        Assert.True(Vector2.Distance(rider.GlobalPosition - platform.GlobalPosition, offset) < 1f);
        Assert.Equal(150f, rider.GetPlatformVelocity().X, 0);
        Assert.Equal(150f, platform.GetStepVelocity().X, 0);
    }

    [Theory]
    [InlineData(PlatformOnLeave.AddVelocity, 150f)]
    [InlineData(PlatformOnLeave.DoNothing, 0f)]
    public void JumpingOffKeepsThePlatformsVelocityAsAsked(PlatformOnLeave mode, float addedSide)
    {
        _h.AddGround(2000f);
        PlatformOnPath(new Vector2(-1000, 0), new Vector2(3000, 0), speed: 150f, pingPong: false);
        var rider = AddRider(new Vector2(-1000, -41));
        rider.PlatformOnLeave = mode;
        _h.RunSeconds(0.5f);
        rider.JumpSpeed = 400f;
        _h.Run(4);
        Assert.False(rider.IsOnFloor());
        Assert.Equal(addedSide, rider.Velocity.X, 0);
    }

    [Fact]
    public void AKinematicRigidBodyMovedByItsNodeReportsItsVelocity()
    {
        var body = _h.Add(new RigidBody2D { Name = "Kinematic", Mode = RigidBodyMode.Kinematic });
        body.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(50, 50) } });
        _h.Run(1);
        for (var i = 0; i < 5; i++)
        {
            body.Position += new Vector2(5, 0); // 300 px/s at 60 Hz
            _h.Run(1);
        }

        Assert.Equal(300f, body.LinearVelocity.X, 0);
    }
}
