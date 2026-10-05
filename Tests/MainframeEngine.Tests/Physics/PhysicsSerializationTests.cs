using System.Numerics;
using System.Text;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Physics;

/// <summary>Physics nodes and shape resources round-trip through <c>.mscene</c> JSON with their exported values.</summary>
[Collection(nameof(SerialBox2D))]
public sealed class PhysicsSerializationTests
{
    private static Node Own(Node root, Node child, Node? parent = null)
    {
        (parent ?? root).AddChild(child);
        child.Owner = root;
        return child;
    }

    [Fact]
    public void ThreeDimensionalBodiesAndShapesRoundTrip()
    {
        var shared = new PhysicsMaterial { Friction = 0.9f, Bounce = 0.25f };
        var box = new BoxShape3D { Size = new Vector3(2, 1, 3) };
        var root = new Node3D { Name = "Level" };
        var floor = (StaticBody3D)Own(root, new StaticBody3D { Name = "Floor", PhysicsMaterial = shared, CollisionLayer = 5, CollisionMask = 3 });
        Own(root, new CollisionShape3D { Name = "Shape", Shape = box, Position = new Vector3(0, -1, 0) }, floor);
        Own(root, new CollisionShape3D { Name = "Terrain", Shape = new HeightMapShape3D { MapWidth = 3, MapDepth = 2, MapData = [0, 1, 2, 3, 4, 5] } }, floor);
        Own(root, new CollisionShape3D { Name = "Mesh", Disabled = true, Shape = new ConcavePolygonShape3D { Faces = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] } }, floor);

        var crate = (RigidBody3D)Own(root, new RigidBody3D
        {
            Name = "Crate",
            Mass = 4,
            GravityScale = 0.5f,
            LinearDamp = 0.2f,
            AngularDamp = 0.6f,
            Ccd = true,
            CanSleep = false,
            ContactMonitor = true,
            AxisLocks = AxisLock.AngularX | AxisLock.AngularZ,
            Mode = RigidBodyMode.Kinematic,
            LinearVelocity = new Vector3(1, 2, 3),
            AngularVelocity = new Vector3(0, 1, 0),
            PhysicsMaterial = shared,
            PhysicsInterpolation = false,
        });
        Own(root, new CollisionShape3D { Name = "A", Shape = box }, crate);
        Own(root, new CollisionShape3D { Name = "B", Shape = new SphereShape3D { Radius = 0.75f } }, crate);
        Own(root, new CollisionShape3D { Name = "C", Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.5f } }, crate);
        Own(root, new CollisionShape3D { Name = "D", Shape = new CylinderShape3D { Radius = 0.2f, Height = 0.9f } }, crate);
        Own(root, new CollisionShape3D { Name = "E", Shape = new ConvexPolygonShape3D { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ] } }, crate);

        var player = (CharacterBody3D)Own(root, new CharacterBody3D
        {
            Name = "Player",
            Velocity = new Vector3(1, 0, 0),
            FloorMaxAngleDegrees = 50,
            FloorSnapLength = 0.3f,
            FloorStopOnSlope = false,
            MaxSlides = 6,
            SafeMargin = 0.02f,
            UpDirection = new Vector3(0, 0, 1),
        });
        Own(root, new CollisionShape3D { Name = "Body", Shape = new CapsuleShape3D() }, player);
        Own(root, new Area3D { Name = "Trigger", Monitoring = false, CollisionMask = 0xFF });

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();
        try
        {
            var floorCopy = copy.GetNode<StaticBody3D>("Floor");
            Assert.Equal(5u, floorCopy.CollisionLayer);
            Assert.Equal(3u, floorCopy.CollisionMask);
            Assert.Equal(0.9f, floorCopy.PhysicsMaterial!.Friction);
            Assert.Equal(new Vector3(2, 1, 3), ((BoxShape3D)floorCopy.GetNode<CollisionShape3D>("Shape").Shape!).Size);
            var terrain = (HeightMapShape3D)floorCopy.GetNode<CollisionShape3D>("Terrain").Shape!;
            Assert.Equal((3, 2), (terrain.MapWidth, terrain.MapDepth));
            Assert.Equal([0f, 1, 2, 3, 4, 5], terrain.MapData);
            Assert.True(floorCopy.GetNode<CollisionShape3D>("Mesh").Disabled);

            var crateCopy = copy.GetNode<RigidBody3D>("Crate");
            Assert.Equal(4, crateCopy.Mass);
            Assert.Equal(0.5f, crateCopy.GravityScale);
            Assert.Equal(0.2f, crateCopy.LinearDamp);
            Assert.Equal(0.6f, crateCopy.AngularDamp);
            Assert.True(crateCopy.Ccd);
            Assert.False(crateCopy.CanSleep);
            Assert.True(crateCopy.ContactMonitor);
            Assert.Equal(AxisLock.AngularX | AxisLock.AngularZ, crateCopy.AxisLocks);
            Assert.Equal(RigidBodyMode.Kinematic, crateCopy.Mode);
            Assert.Equal(new Vector3(1, 2, 3), crateCopy.LinearVelocity);
            Assert.Equal(new Vector3(0, 1, 0), crateCopy.AngularVelocity);
            Assert.False(crateCopy.PhysicsInterpolation);
            // One inline resource per distinct instance: the shared material and box stay shared.
            Assert.Same(floorCopy.PhysicsMaterial, crateCopy.PhysicsMaterial);
            Assert.Same(floorCopy.GetNode<CollisionShape3D>("Shape").Shape, crateCopy.GetNode<CollisionShape3D>("A").Shape);
            Assert.Equal(0.75f, ((SphereShape3D)crateCopy.GetNode<CollisionShape3D>("B").Shape!).Radius);
            var capsule = (CapsuleShape3D)crateCopy.GetNode<CollisionShape3D>("C").Shape!;
            Assert.Equal((0.3f, 1.5f), (capsule.Radius, capsule.Height));
            var cylinder = (CylinderShape3D)crateCopy.GetNode<CollisionShape3D>("D").Shape!;
            Assert.Equal((0.2f, 0.9f), (cylinder.Radius, cylinder.Height));
            Assert.Equal(4, ((ConvexPolygonShape3D)crateCopy.GetNode<CollisionShape3D>("E").Shape!).Points.Length);

            var playerCopy = copy.GetNode<CharacterBody3D>("Player");
            Assert.Equal(new Vector3(1, 0, 0), playerCopy.Velocity);
            Assert.Equal(50, playerCopy.FloorMaxAngleDegrees, 3);
            Assert.Equal(0.3f, playerCopy.FloorSnapLength);
            Assert.False(playerCopy.FloorStopOnSlope);
            Assert.Equal(6, playerCopy.MaxSlides);
            Assert.Equal(0.02f, playerCopy.SafeMargin);
            Assert.Equal(new Vector3(0, 0, 1), playerCopy.UpDirection);

            var trigger = copy.GetNode<Area3D>("Trigger");
            Assert.False(trigger.Monitoring);
            Assert.Equal(0xFFu, trigger.CollisionMask);
        }
        finally
        {
            copy.Free();
            root.Free();
        }
    }

    [Fact]
    public void TwoDimensionalBodiesAndShapesRoundTripAndSimulate()
    {
        var root = new Node2D { Name = "Level" };
        var ground = Own(root, new StaticBody2D { Name = "Ground", Position = new Vector2(0, 50) });
        Own(root, new CollisionShape2D { Name = "Rect", Shape = new RectangleShape2D { Size = new Vector2(1000, 100) } }, ground);
        Own(root, new CollisionShape2D { Name = "Edges", Shape = new ConcavePolygonShape2D { Segments = [new(-500, -50), new(500, -50)] } }, ground);
        Own(root, new CollisionShape2D { Name = "Edge", Shape = new SegmentShape2D { A = new Vector2(-10, 0), B = new Vector2(10, 0) } }, ground);
        var ball = (RigidBody2D)Own(root, new RigidBody2D { Name = "Ball", Position = new Vector2(0, -200), Mass = 2, ContactMonitor = true, AngularVelocity = 1 });
        Own(root, new CollisionShape2D { Name = "Circle", Shape = new CircleShape2D { Radius = 15 } }, ball);
        Own(root, new CollisionShape2D { Name = "Capsule", Shape = new CapsuleShape2D { Radius = 5, Height = 40 } }, ball);
        Own(root, new CollisionShape2D { Name = "Tri", Shape = new ConvexPolygonShape2D { Points = [new(-5, 0), new(5, 0), new(0, -8)] } }, ball);
        var player = (CharacterBody2D)Own(root, new CharacterBody2D { Name = "Player", FloorSnapLength = 4, SafeMargin = 0.5f });
        Own(root, new CollisionShape2D { Shape = new CapsuleShape2D() }, player);
        Own(root, new Area2D { Name = "Zone", Monitoring = false });

        var json = SceneSaver.ToJson(root);
        var text = Encoding.UTF8.GetString(json);
        Assert.Contains("\"RectangleShape2D\"", text, StringComparison.Ordinal);
        var copy = PackedScene.Parse(json).Instantiate();
        root.Free();

        Assert.Equal(new Vector2(1000, 100), ((RectangleShape2D)copy.GetNode<CollisionShape2D>("Ground/Rect").Shape!).Size);
        Assert.Equal(2, ((ConcavePolygonShape2D)copy.GetNode<CollisionShape2D>("Ground/Edges").Shape!).Segments.Length);
        Assert.Equal(new Vector2(10, 0), ((SegmentShape2D)copy.GetNode<CollisionShape2D>("Ground/Edge").Shape!).B);
        var ballCopy = copy.GetNode<RigidBody2D>("Ball");
        Assert.Equal(2, ballCopy.Mass);
        Assert.True(ballCopy.ContactMonitor);
        Assert.Equal(1, ballCopy.AngularVelocity);
        Assert.Equal(15, ((CircleShape2D)ballCopy.GetNode<CollisionShape2D>("Circle").Shape!).Radius);
        Assert.Equal(40, ((CapsuleShape2D)ballCopy.GetNode<CollisionShape2D>("Capsule").Shape!).Height);
        Assert.Equal(3, ((ConvexPolygonShape2D)ballCopy.GetNode<CollisionShape2D>("Tri").Shape!).Points.Length);
        Assert.Equal(4, copy.GetNode<CharacterBody2D>("Player").FloorSnapLength);
        Assert.False(copy.GetNode<Area2D>("Zone").Monitoring);

        // The loaded scene simulates: the ball lands on the ground.
        using var h = new PhysicsHarness2D();
        h.Add(copy);
        h.RunSeconds(2f);
        Assert.InRange(ballCopy.Position.Y, -30, -14);
    }

    [Fact]
    public void EveryPhysicsTypeIsRegistered()
    {
        Type[] types =
        [
            typeof(StaticBody3D), typeof(RigidBody3D), typeof(CharacterBody3D), typeof(Area3D), typeof(CollisionShape3D),
            typeof(BoxShape3D), typeof(SphereShape3D), typeof(CapsuleShape3D), typeof(CylinderShape3D), typeof(ConvexPolygonShape3D),
            typeof(ConcavePolygonShape3D), typeof(HeightMapShape3D), typeof(PhysicsMaterial),
            typeof(StaticBody2D), typeof(RigidBody2D), typeof(CharacterBody2D), typeof(Area2D), typeof(CollisionShape2D),
            typeof(RectangleShape2D), typeof(CircleShape2D), typeof(CapsuleShape2D), typeof(ConvexPolygonShape2D),
            typeof(SegmentShape2D), typeof(ConcavePolygonShape2D),
        ];
        foreach (var type in types)
        {
            var info = TypeRegistry.Get(type);
            Assert.True(info is not null, type.Name);
            Assert.NotNull(info.CreateInstance());
        }

        Assert.Contains(TypeRegistry.Get(typeof(RigidBody3D))!.Signals, s => s.Name == "BodyEntered");
        Assert.Contains(TypeRegistry.Get(typeof(Area2D))!.Signals, s => s.Name == "BodyExited");
    }
}
