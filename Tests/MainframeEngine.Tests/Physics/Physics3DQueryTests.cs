using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>Raycasts, shape casts, overlaps, MoveAndSlide and interpolation in 3D.</summary>
public sealed class Physics3DQueryTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    // ---- queries ------------------------------------------------------------------------------------------------

    [Fact]
    public void RayCastHitsTheClosestBodyWithPositionNormalAndShape()
    {
        var floor = _h.AddFloor();
        var box = _h.AddStaticBox(new Vector3(0, 2, 0), new Vector3(2, 2, 2), name: "Box");

        Assert.True(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out var hit));
        Assert.Same(box, hit.Collider);
        Assert.Same(box.GetChild(0), hit.Shape);
        Assert.Equal(3, hit.Position.Y, 3);
        Assert.Equal(Vector3.UnitY, hit.Normal);
        Assert.Equal(0.35f, hit.Fraction, 3);

        // Excluded box, or a mask without its layer: the floor is next.
        Assert.True(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out hit, exclude: box));
        Assert.Same(floor, hit.Collider);
        Assert.Equal(0, hit.Position.Y, 3);
        box.CollisionLayer = CollisionLayers.Layer(5);
        Assert.True(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out hit, CollisionLayers.Layer(1)));
        Assert.Same(floor, hit.Collider);

        // Misses: pointing away, too short, nothing in the mask.
        Assert.False(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, 20, 0), out _));
        Assert.False(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, 5, 0), out _));
        Assert.False(_h.Query.RayCast(new Vector3(0, 10, 0), new Vector3(0, -10, 0), out _, CollisionLayers.Layer(9)));
    }

    [Fact]
    public void RayCastSeesBodiesMovedByCodeBeforeTheNextStepButNeverAreas()
    {
        var box = _h.AddStaticBox(new Vector3(0, 0, 0), Vector3.One);
        _h.AddArea(new Vector3(20, 0, 0), new Vector3(4, 4, 4));
        _h.Run(1);
        box.Position = new Vector3(10, 0, 0);
        Assert.True(_h.Query.RayCast(new Vector3(10, 5, 0), new Vector3(10, -5, 0), out var hit));
        Assert.Same(box, hit.Collider);
        Assert.False(_h.Query.RayCast(new Vector3(20, 5, 0), new Vector3(20, -5, 0), out _));
    }

    [Fact]
    public void ShapeCastReportsTheSafeFractionAndSurfaceNormal()
    {
        var floor = _h.AddFloor();
        var sphere = new SphereShape3D { Radius = 0.5f };
        var from = Transform3D.FromTrs(new Vector3(0, 5, 0), Quaternion.Identity, Vector3.One);

        Assert.True(_h.Query.ShapeCast(sphere, from, new Vector3(0, -10, 0), out var hit));
        Assert.Same(floor, hit.Collider);
        Assert.Equal(0.45f, hit.Fraction, 2); // travels 4.5 of 10
        Assert.Equal(1, hit.Normal.Y, 3);
        Assert.Equal(0, hit.Position.Y, 2);

        Assert.False(_h.Query.ShapeCast(sphere, from, new Vector3(0, -3, 0), out _));
        Assert.False(_h.Query.ShapeCast(sphere, from, new Vector3(0, -10, 0), out _, exclude: floor));
        Assert.Throws<NotSupportedException>(() => _h.Query.ShapeCast(new ConcavePolygonShape3D(), from, Vector3.UnitY, out _));
    }

    [Fact]
    public void IntersectShapeAndPointFindOverlappingBodies()
    {
        var a = _h.AddStaticBox(new Vector3(0, 0, 0), Vector3.One, name: "A");
        var b = _h.AddStaticBox(new Vector3(1.5f, 0, 0), Vector3.One, name: "B");
        _h.AddStaticBox(new Vector3(10, 0, 0), Vector3.One, name: "Far");

        var results = new List<CollisionObject3D>();
        var probe = new BoxShape3D { Size = new Vector3(2, 1, 1) };
        Assert.Equal(2, _h.Query.IntersectShape(probe, Transform3D.FromTrs(new Vector3(0.75f, 0, 0), Quaternion.Identity, Vector3.One), results));
        Assert.Contains(a, results);
        Assert.Contains(b, results);

        results.Clear();
        Assert.Equal(1, _h.Query.IntersectPoint(new Vector3(1.6f, 0.2f, 0), results));
        Assert.Same(b, results[0]);
        results.Clear();
        Assert.Equal(0, _h.Query.IntersectPoint(new Vector3(0.75f, 0, 0), results)); // the gap between A and B
    }

    [Fact]
    public void ConcaveAndHeightMapShapesCollideOnStaticBodies()
    {
        var terrain = new StaticBody3D { Name = "Terrain" };
        terrain.AddChild(new CollisionShape3D
        {
            Shape = new HeightMapShape3D { MapWidth = 3, MapDepth = 3, MapData = [0, 0, 0, 0, 1, 0, 0, 0, 0] },
            Scale = new Vector3(4, 1, 4),
        });
        _h.Add(terrain);
        var ramp = new StaticBody3D { Name = "Ramp", Position = new Vector3(20, 0, 0) };
        ramp.AddChild(new CollisionShape3D
        {
            Shape = new ConcavePolygonShape3D { Faces = [new(-5, 0, -5), new(-5, 0, 5), new(5, 0, -5), new(5, 0, -5), new(-5, 0, 5), new(5, 0, 5)] },
        });
        _h.Add(ramp);

        Assert.True(_h.Query.RayCast(new Vector3(0.2f, 5, 0.2f), new Vector3(0.2f, -5, 0.2f), out var hit));
        Assert.Same(terrain, hit.Collider);
        Assert.InRange(hit.Position.Y, 0.85f, 0.97f); // next to the raised centre sample (1 at the centre, 0 at ±4)

        var ball = new RigidBody3D { Name = "Ball", Position = new Vector3(21, 3, 1) };
        ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D() });
        _h.Add(ball);
        _h.RunSeconds(2f);
        Assert.InRange(ball.Position.Y, 0.45f, 0.56f);
    }

    [Fact]
    public void ConcaveShapesAreIgnoredOnDynamicBodies()
    {
        var body = new RigidBody3D { Name = "Bad" };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Faces = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ] } });
        _h.Add(body);
        _h.Run(2); // logs an error, does not throw
        Assert.False(_h.Query.RayCast(new Vector3(0.2f, 5, 0.2f), new Vector3(0.2f, -5, 0.2f), out _));
    }

    [Fact]
    public void AllShapeTypesProduceBodiesAndScaleIsApplied()
    {
        Shape3D[] shapes =
        [
            new BoxShape3D(), new SphereShape3D(), new CapsuleShape3D(), new CylinderShape3D(),
            new ConvexPolygonShape3D { Points = [new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0, 0.5f, -0.5f), new(0, 0, 0.5f)] },
        ];
        float[] tops = [1f, 1f, 2f, 2f, 0.5f]; // at x = z = 0, scaled x2
        for (var i = 0; i < shapes.Length; i++)
        {
            var body = new StaticBody3D { Position = new Vector3(i * 5, 0, 0), Scale = new Vector3(2) };
            body.AddChild(new CollisionShape3D { Shape = shapes[i] });
            _h.Add(body);
            Assert.True(_h.Query.RayCast(new Vector3(i * 5, 10, 0), new Vector3(i * 5, -10, 0), out var hit), shapes[i].GetType().Name);
            Assert.Same(body, hit.Collider);
            Assert.True(MathF.Abs(hit.Position.Y - tops[i]) < 0.01f, $"{shapes[i].GetType().Name} top at {hit.Position.Y}, expected {tops[i]}");
        }
    }

    // ---- MoveAndSlide -------------------------------------------------------------------------------------------

    private WalkerCharacter3D AddCharacter(Vector3 position, float radius = 0.4f, float height = 1.8f)
    {
        // Uninterpolated, so tests read the physics pose right after a tick.
        var character = new WalkerCharacter3D { Name = "Character", Position = position, PhysicsInterpolation = false };
        character.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = radius, Height = height } });
        return _h.Add(character);
    }

    [Fact]
    public void CharacterStartingExactlyOnALargeFloorWalksAway()
    {
        // A capsule whose bottom starts exactly on a big box's top face (the Driving Range player on its CSG ground):
        // recovery must see the touch, or every cast starts overlapping and the body never moves.
        _h.AddStaticBox(new Vector3(45, -5, -5.5f), new Vector3(125, 10, 130), name: "Ground");
        var character = AddCharacter(new Vector3(0, 1, 0), radius: 0.5f, height: 2f);
        character.Walk = new Vector3(0, 0, -5);
        _h.RunSeconds(1f);

        Assert.True(character.IsOnFloor(), $"at {character.Position}, velocity {character.Velocity}");
        Assert.InRange(character.Position.Z, -5.2f, -4.8f);
        Assert.InRange(character.Position.Y, 1f, 1f + 2 * character.SafeMargin);
    }

    [Fact]
    public void CharacterRestingWithGodotsMarginHoldsItsHeight()
    {
        // Godot's default safe margin (1 mm): the body settles within the margin and stays there (no creep).
        _h.AddStaticBox(new Vector3(45, -5, -5.5f), new Vector3(125, 10, 130), name: "Ground");
        var character = AddCharacter(new Vector3(0, 1.05f, 0), radius: 0.5f, height: 2f);
        character.SafeMargin = 0.001f;
        character.Walk = new Vector3(0, 0, -5);
        _h.RunSeconds(1f);
        var settled = character.Position.Y;
        _h.RunSeconds(2f);

        Assert.True(character.IsOnFloor(), $"settled {settled}, at {character.Position}, velocity {character.Velocity}");
        Assert.InRange(settled, 1f, 1.002f);
        Assert.Equal(settled, character.Position.Y, 0.0002f);
    }

    [Fact]
    public void CharacterJumpingOffTheFloorIsNotOnTheFloorAfterTheJumpStep()
    {
        // Godot sets the floor state from the motion's collisions only: a body moving away from the floor it starts on
        // is airborne after the step (ADR 0128).
        _h.AddFloor();
        var character = AddCharacter(new Vector3(0, 0.95f, 0));
        character.SafeMargin = 0.001f;
        _h.RunSeconds(0.5f);
        Assert.True(character.IsOnFloor());

        character.Gravity = 0;
        character.Velocity = new Vector3(0, 4.5f, 0);
        _h.Run(1);

        Assert.False(character.IsOnFloor());
        Assert.Equal(4.5f, character.Velocity.Y, 0.001f);
    }

    [Fact]
    public void CharacterLandsAndStaysOnTheFloor()
    {
        _h.AddFloor();
        var character = AddCharacter(new Vector3(0, 3, 0));
        _h.RunSeconds(1.5f);

        Assert.True(character.IsOnFloor());
        Assert.False(character.IsOnWall());
        Assert.True(Vector3.Distance(Vector3.UnitY, character.GetFloorNormal()) < 1e-5f, $"floor normal {character.GetFloorNormal()}"); // EPA's, float-exact
        Assert.InRange(character.Position.Y, 0.905f, 0.93f); // half height + safe margin (1 cm)
        Assert.InRange(character.Velocity.Y, -0.2f, 0f);   // gravity of one step, removed again by the floor
        Assert.True(character.GetSlideCollisionCount() >= 1);
    }

    [Fact]
    public void CharacterStopsAtWallsAndSlidesAlongThem()
    {
        _h.AddFloor();
        _h.AddStaticBox(new Vector3(3, 1, 0), new Vector3(1, 2, 10)); // wall face at x = 2.5
        var character = AddCharacter(new Vector3(0, 0.91f, 0));
        character.Walk = new Vector3(4, 0, 1);
        _h.RunSeconds(1.5f);

        Assert.True(character.IsOnWall());
        Assert.True(character.IsOnFloor());
        Assert.Equal(-1, character.GetWallNormal().X, 3);
        Assert.InRange(character.Position.X, 2.5f - 0.4f - 0.03f, 2.5f - 0.4f - 0.005f);
        Assert.True(character.Position.Z > 1.2f, $"slid along the wall to z = {character.Position.Z}");
        Assert.Equal(0, character.Velocity.X, 3);
    }

    [Fact]
    public void CharacterWalksUpWalkableSlopesAndDoesNotSlideDownWhenStanding()
    {
        _h.AddFloor();
        // A 30° ramp rising along +X from (2, 0): its top face runs from x = 2 to x ≈ 10.7.
        _h.AddStaticBox(new Vector3(6.58f, 2.067f, 0), new Vector3(10, 1, 6), new Vector3(0, 0, 30), "Ramp");
        var character = AddCharacter(new Vector3(0, 0.91f, 0));
        character.Walk = new Vector3(3, 0, 0);
        _h.RunSeconds(2f);

        Assert.True(character.IsOnFloor());
        Assert.True(character.Position.Y > 2f, $"climbed to {character.Position}");
        Assert.Equal(30, float.RadiansToDegrees(MathF.Acos(character.GetFloorNormal().Y)), 0.5);

        character.Walk = Vector3.Zero;
        _h.RunSeconds(0.25f);
        var standing = character.Position;
        _h.RunSeconds(1f);
        Assert.True(character.IsOnFloor());
        Assert.True(Vector3.Distance(standing, character.Position) < 0.02f, $"slid from {standing} to {character.Position}");
    }

    [Fact]
    public void SteepSlopesAreWallsAndTheCharacterSlidesDown()
    {
        _h.AddFloor();
        _h.AddStaticBox(new Vector3(2, 1, 0), new Vector3(1, 6, 6), new Vector3(0, 0, 30), "Steep"); // 60° face
        var character = AddCharacter(new Vector3(0.5f, 3, 0));
        character.FloorMaxAngleDegrees = 45;
        var sawWall = false;
        for (var i = 0; i < 120; i++)
        {
            _h.Run(1);
            sawWall |= character.IsOnWall() && !character.IsOnFloor();
        }

        Assert.True(sawWall);
        Assert.True(character.IsOnFloor());
        Assert.InRange(character.Position.Y, 0.905f, 0.93f); // ended up on the ground
    }

    [Fact]
    public void CeilingStopsUpwardMotion()
    {
        _h.AddFloor();
        _h.AddStaticBox(new Vector3(0, 3.5f, 0), new Vector3(10, 1, 10), name: "Ceiling"); // underside at y = 3
        var character = AddCharacter(new Vector3(0, 0.91f, 0));
        character.Gravity = 0;
        character.Velocity = new Vector3(0, 6, 0);
        var sawCeiling = false;
        for (var i = 0; i < 40 && !sawCeiling; i++)
        {
            _h.Run(1);
            sawCeiling = character.IsOnCeiling();
        }

        Assert.True(sawCeiling);
        Assert.Equal(0, character.Velocity.Y, 3);
        Assert.InRange(character.Position.Y, 3 - 0.9f - 0.03f, 3 - 0.9f - 0.005f);
    }

    [Fact]
    public void FloorSnapKeepsTheCharacterOnTheFloorStepDown()
    {
        // Two floor levels with a 5 cm step down at x = 1.
        _h.AddStaticBox(new Vector3(-5, -0.5f, 0), new Vector3(12, 1, 6), name: "Upper");
        _h.AddStaticBox(new Vector3(7, -0.55f, 0), new Vector3(12, 1, 6), name: "Lower");
        var snapping = AddCharacter(new Vector3(0, 0.91f, -1.5f));
        var free = AddCharacter(new Vector3(0, 0.91f, 1.5f));
        free.FloorSnapLength = 0;
        snapping.Walk = free.Walk = new Vector3(4, 0, 0);
        _h.RunSeconds(0.25f);

        var snappingLeftFloor = false;
        var freeLeftFloor = false;
        for (var i = 0; i < 30; i++)
        {
            _h.Run(1);
            snappingLeftFloor |= !snapping.IsOnFloor();
            freeLeftFloor |= !free.IsOnFloor();
        }

        Assert.True(snapping.Position.X > 2);
        Assert.False(snappingLeftFloor);
        Assert.True(freeLeftFloor);
        Assert.InRange(snapping.Position.Y, 0.855f, 0.88f);
    }

    [Fact]
    public void CharacterIsPushedOutOfPenetration()
    {
        _h.AddFloor();
        var character = AddCharacter(new Vector3(0, 0.5f, 0)); // 0.4 m into the floor
        character.Gravity = 0;
        _h.Run(2);
        Assert.InRange(character.Position.Y, 0.905f, 0.95f);
        Assert.True(character.IsOnFloor());
    }

    [Fact]
    public void CharacterPushesDynamicBodies()
    {
        _h.AddFloor();
        var crate = _h.AddBox(new Vector3(2, 0.5f, 0), name: "Crate");
        crate.Mass = 0.2f;
        crate.CollisionLayer = CollisionLayers.Layer(2); // not in the character's mask: it doesn't stop at the crate,
        var character = AddCharacter(new Vector3(0, 0.92f, 0)); // but its kinematic body (layer 1) hits the crate (mask 1)
        character.Walk = new Vector3(3, 0, 0);
        _h.RunSeconds(1.5f);
        Assert.True(crate.Position.X > 2.3f, $"crate at {crate.Position}");
    }

    // ---- interpolation ------------------------------------------------------------------------------------------

    [Fact]
    public void RenderingInterpolatesBetweenTheLastTwoStepsAndPhysicsProcessSeesThePhysicsPose()
    {
        var box = _h.AddBox(new Vector3(0, 100, 0));
        var probe = _h.Add(new PhysicsPoseProbe(box));
        _h.Run(5);
        Assert.Equal(5, probe.Seen.Count);

        // Half-step frames: every other frame runs a step, and frames in between show the midpoint.
        var space = _h.Space;
        _h.Run(1, PhysicsHarness3D.Step * 1.5f); // one step, accumulator left at half a step
        Assert.Equal(0.5f, _h.Tree.PhysicsInterpolationFraction, 3);
        var stepsBefore = space.StepCount;
        var rendered = box.Position.Y;
        _h.Run(1, PhysicsHarness3D.Step * 0.25f); // no step: fraction 0.75
        Assert.Equal(stepsBefore, space.StepCount);
        var later = box.Position.Y;
        Assert.True(later < rendered, "the rendered pose advances between steps");

        // The physics pose (seen by OnPhysicsProcess) is the step result, ahead of the render pose.
        _h.Run(1, PhysicsHarness3D.Step * 0.25f); // fraction 1.0 → a step runs, then fraction 0
        var physicsPose = probe.Seen[^1].Y;
        Assert.True(physicsPose < rendered);
    }

    [Fact]
    public void InterpolatedPoseIsTheExactLerpOfTheLastTwoSteps()
    {
        var box = _h.AddBox(new Vector3(0, 100, 0));
        var other = _h.AddBox(new Vector3(5, 100, 0), name: "Raw");
        other.PhysicsInterpolation = false;
        _h.Run(10);

        // Physics positions after two consecutive steps (other runs uninterpolated, same motion).
        _h.Run(1);
        var y0 = other.Position.Y;
        _h.Run(1);
        var y1 = other.Position.Y;
        Assert.Equal(y0, box.Position.Y, 4); // fraction 0: render = previous step

        _h.Run(1, PhysicsHarness3D.Step * 1.25f); // one more step (y2), fraction 0.25 → lerp(y1, y2, 0.25)
        var y2 = other.Position.Y;
        Assert.Equal(y1 + (y2 - y1) * 0.25f, box.Position.Y, 4);
    }

    [Fact]
    public void TeleportResetsInterpolation()
    {
        var box = _h.AddBox(new Vector3(0, 100, 0));
        _h.Run(10);
        box.Teleport(Transform3D.FromTrs(new Vector3(50, 0, 0), Quaternion.Identity, Vector3.One));
        Assert.Equal(new Vector3(50, 0, 0), box.Position);
        _h.Run(1, PhysicsHarness3D.Step * 1.5f);
        Assert.True(Vector3.Distance(box.Position, new Vector3(50, 0, 0)) < 0.05f, $"no smoothing across the jump: {box.Position}");
    }

    [Fact]
    public void PausedTreesFreezePhysics()
    {
        var box = _h.AddBox(new Vector3(0, 100, 0));
        _h.Run(5);
        _h.Tree.Paused = true;
        var y = box.Position.Y;
        var steps = _h.Space.StepCount;
        _h.Run(10, PhysicsHarness3D.Step * 1.37f);
        Assert.Equal(y, box.Position.Y);
        Assert.Equal(steps, _h.Space.StepCount);
    }
}
