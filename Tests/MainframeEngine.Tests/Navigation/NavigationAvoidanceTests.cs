using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>
/// G3.7 local avoidance (ORCA, a port of RVO2): head-on pairs pass without touching, a crowd squeezes through a gap,
/// static and moving obstacles (radius and polygon) are never entered, results are bit-identical run to run, and
/// layers, masks and priorities pick who avoids whom.
/// </summary>
public sealed class NavigationAvoidanceTests
{
    private const float Tolerance = 0.5f;      // pixels of overlap the discrete step may leave
    private const float MoverTolerance = 1.5f; // ... when an obstacle moves into the agent at its own top speed

    private static float MinPairDistance(List<Avoider2D> bodies)
    {
        var min = float.PositiveInfinity;
        for (var i = 0; i < bodies.Count; i++)
            for (var j = i + 1; j < bodies.Count; j++)
                min = MathF.Min(min, Vector2.Distance(bodies[i].GlobalPosition, bodies[j].GlobalPosition));
        return min;
    }

    // The distance from p to the axis-aligned box min–max (0 inside).
    private static float BoxDistance(Vector2 p, Vector2 min, Vector2 max) =>
        Vector2.Distance(p, Vector2.Clamp(p, min, max));

    [Fact]
    public void AHeadOnPairPassesWithoutTouching()
    {
        using var h = new NavigationHarness(physics: false);
        var a = h.AddAvoider(new Vector2(0, 0), new Vector2(400, 0), name: "A");
        var b = h.AddAvoider(new Vector2(400, 0), new Vector2(0, 0), name: "B");
        var closest = float.PositiveInfinity;
        var swerve = 0f;
        for (var i = 0; i < 600; i++)
        {
            h.Run(1);
            closest = MathF.Min(closest, Vector2.Distance(a.GlobalPosition, b.GlobalPosition));
            swerve = MathF.Max(swerve, MathF.Abs(a.GlobalPosition.Y));
        }

        Assert.True(closest >= 20f - Tolerance, $"closest {closest}");
        Assert.True(swerve > 1f, "they passed by swerving");
        Assert.True(Vector2.Distance(a.GlobalPosition, a.Goal) < 1f, $"A at {a.GlobalPosition}");
        Assert.True(Vector2.Distance(b.GlobalPosition, b.Goal) < 1f, $"B at {b.GlobalPosition}");
        Assert.True(a.Computed > 0 && b.Computed > 0);
    }

    [Fact]
    public void WithoutAvoidanceTheyWalkThroughEachOtherAndTheVelocityComesBackAtOnce()
    {
        using var h = new NavigationHarness(physics: false);
        var a = h.AddAvoider(new Vector2(0, 0), new Vector2(400, 0), name: "A");
        var b = h.AddAvoider(new Vector2(400, 0), new Vector2(0, 0), name: "B");
        a.Agent.AvoidanceEnabled = b.Agent.AvoidanceEnabled = false;
        var closest = float.PositiveInfinity;
        for (var i = 0; i < 300; i++)
        {
            h.Run(1);
            closest = MathF.Min(closest, Vector2.Distance(a.GlobalPosition, b.GlobalPosition));
        }

        Assert.True(closest < 2f);
        var computed = a.Computed;
        a.Agent.SetVelocity(Vector2.One);
        Assert.Equal(computed + 1, a.Computed); // pass-through: raised inside SetVelocity
    }

    [Fact]
    public void TheSafeVelocityArrivesAfterThePhysicsStepAndRespectsMaxSpeed()
    {
        using var h = new NavigationHarness(physics: false);
        var body = h.Add(new Node2D { Name = "Body" });
        var agent = new NavigationAgent2D { Name = "Agent", AvoidanceEnabled = true, MaxSpeed = 50f };
        body.AddChild(agent);
        var received = new List<Vector2>();
        agent.VelocityComputed += received.Add;
        h.Run(1);

        agent.SetVelocity(new Vector2(500, 0));
        Assert.Empty(received); // submitted, not raised
        Assert.Equal(new Vector2(500, 0), agent.SubmittedVelocity);
        h.Run(1);
        Assert.Single(received);
        Assert.Equal(50f, received[0].Length(), 3);
        Assert.Equal(received[0], agent.SafeVelocity);

        h.Run(1); // nothing submitted: no signal, and it stands still for the others
        Assert.Single(received);
        Assert.Equal(Vector2.Zero, agent.SafeVelocity);
    }

    private static (NavigationHarness Harness, List<Avoider2D> Bodies) Crowd()
    {
        // A wall across a 512 × 256 field with a 64 px gap in the middle; 16 agents go from a block on the left to the
        // mirrored block on the right along grid paths, the wall both solid cells and two polygon obstacles.
        var h = new NavigationHarness(physics: false);
        var grid = h.AddGrid(32, 16, 16f);
        for (var y = 0; y < 16; y++)
        {
            if (y is >= 6 and < 10)
                continue;
            grid.SetCellSolid(new Vector2I(15, y), true);
            grid.SetCellSolid(new Vector2I(16, y), true);
        }

        h.AddBox(new Vector2(240, 0), new Vector2(272, 96), "Upper");
        h.AddBox(new Vector2(240, 160), new Vector2(272, 256), "Lower");
        var bodies = new List<Avoider2D>();
        for (var i = 0; i < 16; i++)
        {
            var start = new Vector2(48 + i % 4 * 24, 92 + i / 4 * 24);
            var goal = new Vector2(512 - start.X, start.Y);
            var body = h.AddAvoider(start, goal, radius: 7f, name: $"Agent{i}");
            body.FollowPath = true;
            body.Speed = 90f;
            body.Agent.MaxSpeed = 90f;
            body.Agent.TargetDesiredDistance = 4f;
            body.Agent.PathDesiredDistance = 12f;
            bodies.Add(body);
        }

        h.Run(1);
        foreach (var body in bodies)
            body.Agent.TargetPosition = body.Goal;
        return (h, bodies);
    }

    [Fact]
    public void ACrowdThroughAGapResolvesWithoutOverlapOrWallContact()
    {
        var (h, bodies) = Crowd();
        using (h)
        {
            var closest = float.PositiveInfinity;
            var wall = float.PositiveInfinity;
            var ticks = 0;
            for (; ticks < 2400 && !bodies.TrueForAll(b => b.Agent.IsTargetReached()); ticks++)
            {
                h.Run(1);
                closest = MathF.Min(closest, MinPairDistance(bodies));
                foreach (var body in bodies)
                {
                    var p = body.GlobalPosition;
                    wall = MathF.Min(wall, MathF.Min(BoxDistance(p, new Vector2(240, 0), new Vector2(272, 96)),
                        BoxDistance(p, new Vector2(240, 160), new Vector2(272, 256))));
                }
            }

            Assert.True(bodies.TrueForAll(b => b.Agent.IsTargetReached()), $"{bodies.Count(b => b.Agent.IsTargetReached())} of 16 arrived");
            Assert.True(closest >= 14f - Tolerance, $"closest pair {closest}");
            Assert.True(wall >= 7f - Tolerance, $"closest to a wall {wall}");
        }
    }

    [Fact]
    public void ResultsAreBitIdenticalRunToRun()
    {
        static uint[] Run()
        {
            var (h, bodies) = Crowd();
            using (h)
            {
                h.Run(400);
                return [.. bodies.SelectMany(b => new[] { BitConverter.SingleToUInt32Bits(b.GlobalPosition.X), BitConverter.SingleToUInt32Bits(b.GlobalPosition.Y) })];
            }
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void AStaticPolygonIsGoneRoundNotThrough()
    {
        using var h = new NavigationHarness(physics: false);
        // The box overlaps the straight line by 8 px of the agent's radius: it must slide round the top corner. (Dead on
        // into a flat face ORCA only stops: going round a wall is the path's job, as in the crowd test.)
        h.AddBox(new Vector2(170, 2), new Vector2(230, 60));
        var body = h.AddAvoider(new Vector2(0, 0), new Vector2(400, 0));
        var closest = float.PositiveInfinity;
        for (var i = 0; i < 600; i++)
        {
            h.Run(1);
            closest = MathF.Min(closest, BoxDistance(body.GlobalPosition, new Vector2(170, 2), new Vector2(230, 60)));
        }

        Assert.True(closest >= 10f - Tolerance, $"closest {closest}");
        Assert.True(Vector2.Distance(body.GlobalPosition, body.Goal) < 1f, $"at {body.GlobalPosition}");
    }

    [Fact]
    public void AMovingRadiusObstacleIsAvoided()
    {
        using var h = new NavigationHarness(physics: false);
        var mover = h.Add(new Mover2D { Name = "Mover", Position = new Vector2(200, -200), Radius = 15f, Velocity = new Vector2(0, 100) });
        var body = h.AddAvoider(new Vector2(0, 0), new Vector2(400, 0));
        var closest = float.PositiveInfinity;
        for (var i = 0; i < 600; i++)
        {
            h.Run(1);
            closest = MathF.Min(closest, Vector2.Distance(body.GlobalPosition, mover.GlobalPosition));
        }

        Assert.True(closest >= 25f - MoverTolerance, $"closest {closest}");
        Assert.True(Vector2.Distance(body.GlobalPosition, body.Goal) < 1f);
        Assert.True(mover.GlobalPosition.Y > 300f, "the obstacle kept moving: agents make way for it alone");
    }

    [Fact]
    public void AMovingPolygonIsPlacedEveryStep()
    {
        using var h = new NavigationHarness(physics: false);
        var box = h.Add(new Mover2D
        {
            Name = "Box",
            Position = new Vector2(200, -150),
            Velocity = new Vector2(0, 40),
            Vertices = [new(-20, -20), new(20, -20), new(20, 20), new(-20, 20)],
        });
        var body = h.AddAvoider(new Vector2(0, 0), new Vector2(400, 0));
        var closest = float.PositiveInfinity;
        for (var i = 0; i < 900; i++)
        {
            h.Run(1);
            var c = box.GlobalPosition;
            closest = MathF.Min(closest, BoxDistance(body.GlobalPosition, c - new Vector2(20), c + new Vector2(20)));
        }

        Assert.True(closest >= 10f - MoverTolerance, $"closest {closest}"); // a polygon moves without prediction
        Assert.True(Vector2.Distance(body.GlobalPosition, body.Goal) < 1f, $"at {body.GlobalPosition}");
        Assert.Equal(4, h.Map.Avoidance!.VertexCount);
    }

    [Fact]
    public void AMaskThatMissesTheOtherLayerLeavesTheAvoidingToTheOther()
    {
        using var h = new NavigationHarness(physics: false);
        var a = h.AddAvoider(new Vector2(0, 0), new Vector2(400, 0), name: "A");
        var b = h.AddAvoider(new Vector2(400, 0), new Vector2(0, 0), name: "B");
        a.Agent.AvoidanceMask = 2; // A sees nothing on layer 1; B (mask 1) sees A (layer 1) and makes way alone
        AssertOnlyBSwerves(h, a, b);
    }

    [Fact]
    public void AHigherPriorityAgentKeepsItsLine()
    {
        using var h = new NavigationHarness(physics: false);
        var a = h.AddAvoider(new Vector2(0, 0), new Vector2(400, 0), name: "A");
        var b = h.AddAvoider(new Vector2(400, 0), new Vector2(0, 0), name: "B");
        b.Agent.AvoidancePriority = 0.5f; // A ignores B; B makes way alone
        AssertOnlyBSwerves(h, a, b);
    }

    private static void AssertOnlyBSwerves(NavigationHarness h, Avoider2D a, Avoider2D b)
    {
        var closest = float.PositiveInfinity;
        var aSwerve = 0f;
        var bSwerve = 0f;
        for (var i = 0; i < 600; i++)
        {
            h.Run(1);
            closest = MathF.Min(closest, Vector2.Distance(a.GlobalPosition, b.GlobalPosition));
            aSwerve = MathF.Max(aSwerve, MathF.Abs(a.GlobalPosition.Y));
            bSwerve = MathF.Max(bSwerve, MathF.Abs(b.GlobalPosition.Y));
        }

        Assert.Equal(0f, aSwerve);
        Assert.True(bSwerve > 5f);
        Assert.True(closest >= 20f - Tolerance, $"closest {closest}");
    }

    [Fact]
    public void AnAgentThatSubmitsNothingIsAStandingObstacle()
    {
        using var h = new NavigationHarness(physics: false);
        var standing = h.Add(new Node2D { Name = "Standing", Position = new Vector2(200, 3) });
        var still = new NavigationAgent2D { Name = "Agent", AvoidanceEnabled = true };
        standing.AddChild(still);
        var raised = 0;
        still.VelocityComputed += _ => raised++;
        var body = h.AddAvoider(new Vector2(0, 0), new Vector2(400, 0));
        var closest = float.PositiveInfinity;
        for (var i = 0; i < 600; i++)
        {
            h.Run(1);
            closest = MathF.Min(closest, Vector2.Distance(body.GlobalPosition, standing.GlobalPosition));
        }

        Assert.Equal(0, raised);
        Assert.True(closest >= 20f - Tolerance, $"closest {closest}");
        Assert.True(Vector2.Distance(body.GlobalPosition, body.Goal) < 1f);
        Assert.Equal(2, h.Map.Avoidance!.EntityCount);
    }

    [Fact]
    public void ObstaclesRegisterWithTheMapWhileInTheTree()
    {
        using var h = new NavigationHarness(physics: false);
        var obstacle = h.AddBox(Vector2.Zero, new Vector2(10));
        Assert.Same(h.Map, obstacle.Map);
        Assert.Contains(obstacle, h.Map.Obstacles);
        obstacle.Parent!.RemoveChild(obstacle);
        Assert.Null(obstacle.Map);
        Assert.DoesNotContain(obstacle, h.Map.Obstacles);
    }
}

/// <summary>An obstacle that moves by its own <see cref="NavigationObstacle2D.Velocity"/> every physics tick.</summary>
internal sealed class Mover2D : NavigationObstacle2D
{
    protected override void OnPhysicsProcess(float delta) => GlobalPosition += Velocity * delta;
}
