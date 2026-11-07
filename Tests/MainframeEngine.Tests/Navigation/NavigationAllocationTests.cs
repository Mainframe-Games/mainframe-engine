using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>
/// G3.4's gate: <b>0 bytes</b> over 300 ticks after warm-up with 64 agents chasing moving targets on a 128 × 128 grid,
/// runtime cell edits every 30 ticks (every agent repaths), and the debug draw on. Also 0 bytes for grid queries with and
/// without jump point search.
/// </summary>
public sealed class NavigationAllocationTests
{
    private const int Size = 128;

    private static NavigationGrid2D Level(NavigationHarness h)
    {
        var grid = h.AddGrid(Size, Size, 16f);
        // Walls every 16 columns with two gaps each, and a few rooms: paths of 100–300 cells.
        for (var x = 16; x < Size; x += 16)
            for (var y = 0; y < Size; y++)
                if (y % 64 is not (8 or 9 or 40 or 41))
                    grid.SetCellSolid(new Vector2I(x, (y + x) % Size), true);
        for (var i = 0; i < 12; i++)
            for (var x = 0; x < 6; x++)
                grid.SetCellWeight(new Vector2I(4 + i * 10, 60 + x), 3f);
        return grid;
    }

    [Fact]
    public void SixtyFourChasersWithEditsAndDebugDrawAllocateNothing()
    {
        using var h = new NavigationHarness(physics: false);
        h.Navigation.DebugDrawEnabled = true;
        var grid = Level(h);
        var walkers = new Walker2D[64];
        for (var i = 0; i < walkers.Length; i++)
        {
            walkers[i] = h.AddWalker(new Vector2(8 + i % 8 * 16 * 15 % (Size * 16), 8 + i / 8 * 250), $"Walker{i}");
            walkers[i].Speed = 300f;
        }

        h.Run(1);
        foreach (var walker in walkers)
            walker.Agent.TargetRepathDistance = 64f;

        var tick = 0;
        void Tick()
        {
            // Targets orbit (period 300 ticks); a door of four cells opens and closes every 30 ticks.
            for (var i = 0; i < walkers.Length; i++)
            {
                var angle = (tick % 300) / 300f * MathF.Tau + i;
                walkers[i].Agent.TargetPosition = new Vector2(1024, 1024) + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (300f + i * 9f);
            }

            if (tick % 30 == 0)
            {
                var closed = tick / 30 % 2 == 0;
                for (var y = 0; y < 4; y++)
                    grid.SetCellSolid(new Vector2I(64, 30 + y), closed);
            }

            h.Run(1);
            h.Tree.Root.DebugLines.Clear(); // the renderer's job
            tick++;
        }

        for (var i = 0; i < 300; i++)
            Tick(); // warm-up: path buffers grow to the longest paths of the cycle
        Assert.True(h.Tree.Root.DebugLines.DroppedLines == 0);

        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 300; i++)
                Tick();
        });
        Assert.Equal(0, allocated);
        Assert.Contains(walkers, w => w.Agent.CurrentPath.Count > 2);
    }

    [Fact]
    public void SixtyFourAvoidingAgentsWithMovingObstaclesAllocateNothing()
    {
        // G3.7: the same level with every agent avoiding (paths, ORCA, VelocityComputed), four radius obstacles orbiting,
        // a polygon obstacle sliding to and fro, and the debug draw on.
        using var h = new NavigationHarness(physics: false);
        h.Navigation.DebugDrawEnabled = true;
        Level(h);
        var bodies = new Avoider2D[64];
        for (var i = 0; i < bodies.Length; i++)
        {
            bodies[i] = h.AddAvoider(new Vector2(8 + i % 8 * 16 * 15 % (Size * 16), 8 + i / 8 * 250), default, radius: 6f, name: $"Avoider{i}");
            bodies[i].FollowPath = true;
            bodies[i].Speed = bodies[i].Agent.MaxSpeed = 300f;
        }

        var movers = new Mover2D[5];
        for (var i = 0; i < 4; i++)
            movers[i] = h.Add(new Mover2D { Name = $"Mover{i}", Radius = 12f });
        movers[4] = h.Add(new Mover2D { Name = "Slider", Vertices = [new(-30, -10), new(30, -10), new(30, 10), new(-30, 10)] });

        h.Run(1);
        foreach (var body in bodies)
            body.Agent.TargetRepathDistance = 160f;

        var tick = 0;
        void Tick()
        {
            for (var i = 0; i < bodies.Length; i++)
            {
                var angle = (tick % 300) / 300f * MathF.Tau + i;
                bodies[i].Agent.TargetPosition = new Vector2(1024, 1024) + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (300f + i * 9f);
            }

            for (var i = 0; i < 4; i++)
            {
                var angle = tick / 120f * MathF.Tau + i * 1.5f;
                movers[i].Position = new Vector2(1024, 1024) + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 400f;
                movers[i].Velocity = new Vector2(-MathF.Sin(angle), MathF.Cos(angle)) * 400f * MathF.Tau / 2f;
            }

            movers[4].Velocity = new Vector2(tick % 200 < 100 ? 120f : -120f, 0f);
            h.Run(1);
            h.Tree.Root.DebugLines.Clear(); // the renderer's job
            tick++;
        }

        for (var i = 0; i < 600; i++)
            Tick(); // warm-up: avoidance moves the bodies off the 300-tick cycle, so paths keep growing for longer
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 300; i++)
                Tick();
        });
        Assert.Equal(0, allocated);
        Assert.True(h.Map.Avoidance!.EntityCount == 68, $"{h.Map.Avoidance.EntityCount} entities");
        Assert.Contains(bodies, b => b.Agent.SafeVelocity != Vector2.Zero);
    }

    [Fact]
    public void GridQueriesAllocateNothing()
    {
        var grid = AStarGridOptimalityTests.Maze();
        Span<Vector2I> ids = stackalloc Vector2I[512];
        Span<Vector2> points = stackalloc Vector2[512];
        void Queries(Span<Vector2I> ids, Span<Vector2> points)
        {
            for (var i = 0; i < 20; i++)
            {
                grid.JumpingEnabled = i % 2 == 0;
                grid.GetIdPath(new Vector2I(1, 1), new Vector2I(29, 19), ids, out _);
                grid.GetPointPath(new Vector2I(29, 1), new Vector2I(1, 19), points, out _, allowPartialPath: true);
            }
        }

        Queries(ids, points); // warm-up: every path (A*, JPS, points) runs once before the gate
        var idArray = new Vector2I[512];
        var pointArray = new Vector2[512];
        Assert.Equal(0, AllocationGate.SmallestWindow(() => Queries(idArray, pointArray)));
    }
}
