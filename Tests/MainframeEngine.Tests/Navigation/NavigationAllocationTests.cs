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
    public void GridQueriesAllocateNothing()
    {
        var grid = AStarGridOptimalityTests.Maze();
        Span<Vector2I> ids = stackalloc Vector2I[512];
        Span<Vector2> points = stackalloc Vector2[512];
        grid.GetIdPath(new Vector2I(1, 1), new Vector2I(29, 19), ids, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            grid.JumpingEnabled = i % 2 == 0;
            grid.GetIdPath(new Vector2I(1, 1), new Vector2I(29, 19), ids, out _);
            grid.GetPointPath(new Vector2I(29, 1), new Vector2I(1, 19), points, out _, allowPartialPath: true);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
