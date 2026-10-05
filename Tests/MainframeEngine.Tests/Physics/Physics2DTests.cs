using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>The Box2D-backed 2D server: same behaviours as 3D, in pixels.</summary>
[Collection(nameof(SerialBox2D))]
public sealed class Physics2DTests : IDisposable
{
    private readonly PhysicsHarness2D _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void FreeFallFollowsGravityInPixels()
    {
        var box = _h.AddBox(new Vector2(0, -10000));
        box.LinearDamp = 0;
        box.PhysicsInterpolation = false;
        _h.RunSeconds(1f);
        Assert.InRange(box.LinearVelocity.Y, 980 * 0.98f, 980 * 1.02f); // Y-down: falling is +Y
        Assert.InRange(box.Position.Y, -10000 + 470, -10000 + 500); // ≈ g t² / 2 = 490 px
    }

    [Fact]
    public void BoxFallsAndRestsOnTheGroundThenSleeps()
    {
        _h.AddGround();
        var box = _h.AddBox(new Vector2(0, -300));
        var sleepChanges = 0;
        box.SleepingStateChanged += () => sleepChanges++;
        _h.RunSeconds(4f);
        Assert.InRange(box.Position.Y, -26, -24); // half of 50 px above the ground's top at y = 0
        Assert.True(box.Sleeping);
        Assert.Equal(1, sleepChanges);
        box.ApplyImpulse(new Vector2(0, -300));
        _h.Run(2);
        Assert.False(box.Sleeping);
    }

    [Fact]
    public void LayersAndMasksUseOrSemantics()
    {
        _h.AddGround(layer: CollisionLayers.Layer(1), mask: CollisionLayers.Layer(1));
        var ghost = _h.AddBox(new Vector2(-300, -200), name: "Ghost");
        ghost.CollisionLayer = CollisionLayers.Layer(2);
        ghost.CollisionMask = 0;
        var oneWay = _h.AddBox(new Vector2(300, -200), name: "OneWay");
        oneWay.CollisionMask = 0;
        _h.RunSeconds(2f);
        Assert.True(ghost.Position.Y > 500, $"ghost at {ghost.Position}");
        Assert.InRange(oneWay.Position.Y, -26, -24);

        // Runtime change: the resting box loses its collision and falls.
        oneWay.CollisionLayer = CollisionLayers.Layer(3);
        _h.RunSeconds(1f);
        Assert.True(oneWay.Position.Y > 100);
    }

    [Fact]
    public void ContactMonitorReportsEnteredAndExited()
    {
        var ground = _h.AddGround();
        var box = _h.AddBox(new Vector2(0, -150));
        box.ContactMonitor = true;
        var log = new List<string>();
        box.BodyEntered += n => log.Add("enter " + n.Name);
        box.BodyExited += n => log.Add("exit " + n.Name);
        _h.RunSeconds(1.5f);
        Assert.Equal(["enter Ground"], log);

        box.Teleport(Transform2D.FromTrs(new Vector2(0, -1000), 0, Vector2.One));
        _h.Run(2);
        Assert.Equal(["enter Ground", "exit Ground"], log);
        Assert.NotNull(ground);
    }

    [Fact]
    public void AreaReportsBodiesEnteringAndLeavingAndDetectsStaticBodies()
    {
        var area = _h.AddArea(new Vector2(0, -500), new Vector2(400, 200));
        var box = _h.AddBox(new Vector2(0, -900));
        var wall = _h.AddStaticBox(new Vector2(150, -500), new Vector2(20, 20));
        var log = new List<string>();
        area.BodyEntered += n => log.Add("enter " + n.Name);
        area.BodyExited += n => log.Add("exit " + n.Name);

        var sawBox = false;
        var overlapping = new List<Node2D>();
        for (var i = 0; i < 120; i++)
        {
            _h.Run(1);
            overlapping.Clear();
            area.GetOverlappingBodies(overlapping);
            sawBox |= overlapping.Contains(box);
        }

        Assert.True(sawBox);
        Assert.Equal(["enter Wall", "enter Box", "exit Box"], log);
        Assert.True(area.OverlapsBody(wall));
    }

    [Fact]
    public void AreaMaskFiltersAndMonitoringOffDropsOverlaps()
    {
        var area = _h.AddArea(Vector2.Zero, new Vector2(400, 400));
        area.CollisionMask = CollisionLayers.Layer(2);
        var ignored = _h.AddStaticBox(new Vector2(-50, 0), new Vector2(20, 20), name: "Ignored");
        var seen = _h.AddStaticBox(new Vector2(50, 0), new Vector2(20, 20), name: "Seen");
        seen.CollisionLayer = CollisionLayers.Layer(2);
        var entered = new List<Node2D>();
        var exited = new List<Node2D>();
        area.BodyEntered += entered.Add;
        area.BodyExited += exited.Add;
        _h.Run(2);
        Assert.Equal([seen], entered);
        Assert.NotNull(ignored);

        area.Monitoring = false;
        _h.Run(1);
        Assert.Equal([seen], exited);
    }

    [Fact]
    public void FreeingABodyInsideAnAreaReportsExitOnce()
    {
        var area = _h.AddArea(Vector2.Zero, new Vector2(400, 400));
        var box = _h.AddBox(Vector2.Zero);
        box.GravityScale = 0;
        var log = new List<string>();
        area.BodyEntered += n => log.Add("enter " + n.Name);
        area.BodyExited += n => log.Add("exit " + n.Name);
        _h.Run(2);
        box.QueueFree();
        _h.Run(3);
        Assert.Equal(["enter Box", "exit Box"], log);
        Assert.Equal(1, _h.Space.ObjectCount);
    }

    [Fact]
    public void RayCastHitsTheClosestBodyInPixels()
    {
        var ground = _h.AddGround();
        var box = _h.AddStaticBox(new Vector2(0, -100), new Vector2(100, 100), name: "Box");
        _h.AddArea(new Vector2(0, -300), new Vector2(100, 100));

        Assert.True(_h.Query.RayCast(new Vector2(0, -500), new Vector2(0, 500), out var hit));
        Assert.Same(box, hit.Collider); // the area above it is ignored
        Assert.Same(box.GetChild(0), hit.Shape);
        Assert.Equal(-150, hit.Position.Y, 1);
        Assert.Equal(-1, hit.Normal.Y, 3);
        Assert.Equal(0.35f, hit.Fraction, 3);

        Assert.True(_h.Query.RayCast(new Vector2(0, -500), new Vector2(0, 500), out hit, exclude: box));
        Assert.Same(ground, hit.Collider);
        Assert.Equal(0, hit.Position.Y, 1);
        box.CollisionLayer = CollisionLayers.Layer(4);
        Assert.True(_h.Query.RayCast(new Vector2(0, -500), new Vector2(0, 500), out hit, CollisionLayers.Layer(1)));
        Assert.Same(ground, hit.Collider);
        Assert.False(_h.Query.RayCast(new Vector2(0, -500), new Vector2(0, -1000), out _));
        Assert.False(_h.Query.RayCast(new Vector2(0, -500), new Vector2(0, 500), out _, CollisionLayers.Layer(9)));
    }

    [Fact]
    public void ShapeCastAndOverlapQueries()
    {
        var ground = _h.AddGround();
        var a = _h.AddStaticBox(new Vector2(-100, -100), new Vector2(50, 50), name: "A");
        var b = _h.AddStaticBox(new Vector2(100, -100), new Vector2(50, 50), name: "B");
        var circle = new CircleShape2D { Radius = 20 };

        Assert.True(_h.Query.ShapeCast(circle, Transform2D.FromTrs(new Vector2(0, -300), 0, Vector2.One), new Vector2(0, 1000), out var hit));
        Assert.Same(ground, hit.Collider);
        Assert.Equal(0.28f, hit.Fraction, 2); // 280 px of 1000
        Assert.Equal(-1, hit.Normal.Y, 3);

        var results = new List<CollisionObject2D>();
        var probe = new RectangleShape2D { Size = new Vector2(300, 20) };
        Assert.Equal(2, _h.Query.IntersectShape(probe, Transform2D.FromTrs(new Vector2(0, -100), 0, Vector2.One), results));
        Assert.Contains(a, results);
        Assert.Contains(b, results);
        results.Clear();
        Assert.Equal(1, _h.Query.IntersectPoint(new Vector2(110, -90), results));
        Assert.Same(b, results[0]);
    }

    [Fact]
    public void AllShapeTypesCollide()
    {
        Shape2D[] shapes =
        [
            new RectangleShape2D(), new CircleShape2D(), new CapsuleShape2D(),
            new ConvexPolygonShape2D { Points = [new(-10, 10), new(10, 10), new(0, -10)] },
            new SegmentShape2D { A = new Vector2(-10, -5), B = new Vector2(10, -5) },
            new ConcavePolygonShape2D { Segments = [new(-10, -8), new(10, -8)] },
        ];
        for (var i = 0; i < shapes.Length; i++)
        {
            var body = new StaticBody2D { Position = new Vector2(i * 100, 0), Scale = new Vector2(2) };
            body.AddChild(new CollisionShape2D { Shape = shapes[i] });
            _h.Add(body);
            Assert.True(_h.Query.RayCast(new Vector2(i * 100, -200), new Vector2(i * 100, 200), out var hit), shapes[i].GetType().Name);
            Assert.Same(body, hit.Collider);
            Assert.True(hit.Position.Y < -9, $"{shapes[i].GetType().Name} top at {hit.Position.Y} (scale 2)");
        }
    }

    [Fact]
    public void KinematicBodiesMoveAndPush()
    {
        _h.AddGround();
        var pusher = new RigidBody2D { Name = "Pusher", Mode = RigidBodyMode.Kinematic, Position = new Vector2(-300, -25) };
        pusher.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(50, 50) } });
        _h.Add(pusher);
        var crate = _h.AddBox(new Vector2(0, -25), name: "Crate");
        _h.RunSeconds(0.5f);
        pusher.LinearVelocity = new Vector2(200, 0);
        _h.RunSeconds(2f);
        Assert.InRange(pusher.Position.X, 90, 110);
        Assert.True(crate.Position.X > 140, $"crate at {crate.Position}");
    }

    [Fact]
    public void InterpolationLerpsBetweenSteps()
    {
        var box = _h.AddBox(new Vector2(0, -10000));
        var raw = _h.AddBox(new Vector2(500, -10000), name: "Raw");
        raw.PhysicsInterpolation = false;
        _h.Run(10);
        _h.Run(1);
        var y0 = raw.Position.Y;
        _h.Run(1);
        var y1 = raw.Position.Y;
        Assert.Equal(y0, box.Position.Y, 2);
        _h.Run(1, PhysicsHarness2D.Step * 1.5f);
        var y2 = raw.Position.Y;
        Assert.Equal(y1 + (y2 - y1) * 0.5f, box.Position.Y, 2);
    }

    // ---- MoveAndSlide -------------------------------------------------------------------------------------------

    private WalkerCharacter2D AddCharacter(Vector2 position)
    {
        var character = new WalkerCharacter2D { Name = "Character", Position = position, PhysicsInterpolation = false };
        character.AddChild(new CollisionShape2D { Shape = new CapsuleShape2D { Radius = 20, Height = 80 } });
        return _h.Add(character);
    }

    [Fact]
    public void CharacterLandsAndWalksIntoAWall()
    {
        _h.AddGround();
        _h.AddStaticBox(new Vector2(300, -100), new Vector2(50, 200)); // face at x = 275
        var character = AddCharacter(new Vector2(0, -200));
        _h.RunSeconds(1f);
        Assert.True(character.IsOnFloor());
        Assert.InRange(character.Position.Y, -42.5f, -40.5f); // half height + ~1 px margin above the ground

        character.Walk = new Vector2(300, 0);
        _h.RunSeconds(1.5f);
        Assert.True(character.IsOnWall());
        Assert.True(character.IsOnFloor());
        Assert.Equal(-1, character.GetWallNormal().X, 3);
        Assert.InRange(character.Position.X, 275 - 20 - 2.5f, 275 - 20 - 0.5f);
    }

    [Fact]
    public void CharacterClimbsSlopesStandsStillOnThemAndSlidesOffSteepOnes()
    {
        _h.AddGround();
        // 30° slope from (200, 0) up to the right (up = −Y).
        var slope = new StaticBody2D { Name = "Slope", Position = new Vector2(200, 0) };
        slope.AddChild(new CollisionShape2D
        {
            Shape = new ConvexPolygonShape2D { Points = [new(0, 0), new(600, 0), new(600, -600 * MathF.Tan(MathF.PI / 6))] },
        });
        _h.Add(slope);
        var character = AddCharacter(new Vector2(0, -42));
        character.Walk = new Vector2(250, 0);
        _h.RunSeconds(2f);
        Assert.True(character.IsOnFloor());
        Assert.True(character.Position.Y < -150, $"climbed to {character.Position}");
        Assert.Equal(30, float.RadiansToDegrees(MathF.Acos(-character.GetFloorNormal().Y)), 0.5);

        character.Walk = Vector2.Zero;
        _h.RunSeconds(0.25f);
        var standing = character.Position;
        _h.RunSeconds(1f);
        Assert.True(Vector2.Distance(standing, character.Position) < 2, $"slid from {standing} to {character.Position}");
    }

    [Fact]
    public void CeilingAndRecovery()
    {
        _h.AddGround();
        _h.AddStaticBox(new Vector2(0, -350), new Vector2(400, 100), name: "Ceiling"); // underside at y = -300
        var character = AddCharacter(new Vector2(0, -20)); // 20 px into the ground
        character.Gravity = 0;
        _h.Run(2);
        Assert.InRange(character.Position.Y, -43f, -40.5f); // pushed out
        Assert.True(character.IsOnFloor());

        character.Velocity = new Vector2(0, -600);
        var sawCeiling = false;
        for (var i = 0; i < 60 && !sawCeiling; i++)
        {
            _h.Run(1);
            sawCeiling = character.IsOnCeiling();
        }

        Assert.True(sawCeiling);
        Assert.Equal(0, character.Velocity.Y, 3);
        Assert.InRange(character.Position.Y, -300 + 40 + 0.5f, -300 + 40 + 2.5f);
    }

    [Fact]
    public void FloorSnapKeepsTheCharacterOnASmallStepDown()
    {
        _h.AddStaticBox(new Vector2(-500, 50), new Vector2(1000, 100), name: "Upper");
        _h.AddStaticBox(new Vector2(500, 55), new Vector2(1000, 100), name: "Lower"); // 5 px lower
        var character = AddCharacter(new Vector2(-100, -42));
        character.FloorSnapLength = 8;
        character.Walk = new Vector2(300, 0);
        _h.RunSeconds(0.3f);
        var left = false;
        for (var i = 0; i < 40; i++)
        {
            _h.Run(1);
            left |= !character.IsOnFloor();
        }

        Assert.True(character.Position.X > 50);
        Assert.False(left);
        Assert.InRange(character.Position.Y, -38, -35);
    }
}
