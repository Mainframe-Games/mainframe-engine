using System.Numerics;
using static MainframeEngine.Tests.Navigation.NavigationMeshQueryTests;
using static MainframeEngine.Tests.Navigation.NavigationPolygonBuilderTests;

namespace MainframeEngine.Tests.Navigation;

/// <summary>
/// G3.9: agents on navigation meshes — following funnel paths, links with <see cref="NavigationAgent2D.LinkReached"/>,
/// repaths when the mesh changes, the debug draw — and the allocation gate's navmesh case: <b>0 bytes</b> over 300 ticks.
/// </summary>
public sealed class NavigationMeshAgentTests
{
    // A body that walks along its agent's path and teleports across links.
    private sealed class Jumper : Node2D
    {
        public readonly List<NavigationLinkReached2D> Links = [];
        public int Finished;
        public NavigationAgent2D Agent => GetNode<NavigationAgent2D>("Agent");

        protected override void OnReady()
        {
            Agent.LinkReached += link =>
            {
                Links.Add(link);
                GlobalPosition = link.ExitPosition;
            };
            Agent.NavigationFinished += () => Finished++;
        }

        protected override void OnPhysicsProcess(float delta)
        {
            if (Agent.IsNavigationFinished())
                return;
            var next = Agent.GetNextPathPosition();
            var to = next - GlobalPosition;
            var step = 240f * delta;
            GlobalPosition = to.Length() <= step ? next : GlobalPosition + Vector2.Normalize(to) * step;
        }
    }

    private static Jumper AddJumper(NavigationHarness h, Vector2 position)
    {
        var body = new Jumper { Name = "Jumper", Position = position };
        body.AddChild(new NavigationAgent2D { Name = "Agent" });
        return h.Add(body);
    }

    [Fact]
    public void AnAgentWalksRoundTheWallAndAcrossALink()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Mesh(Box(0, 0, 300, 200), Box(100, 0, 200, 150)));
        AddRegion(h, Mesh(Box(0, 0, 200, 200)), new Vector2(500, 0), "Island");
        var link = h.Add(new NavigationLink2D { Name = "Teleporter", StartPosition = new Vector2(290, 100), EndPosition = new Vector2(510, 100) });
        var body = AddJumper(h, new Vector2(50, 50));
        h.Run(1);
        body.Agent.TargetPosition = new Vector2(650, 50);
        body.Agent.GetNextPathPosition();
        Assert.Equal([new(50, 50), new(100, 150), new(200, 150), new(290, 100), new(510, 100), new(650, 50)],
            body.Agent.GetCurrentNavigationPath().ToArray());
        var lowest = 0f;
        for (var i = 0; i < 600 && body.Finished == 0; i++)
        {
            h.Run(1);
            if (body.GlobalPosition.X is > 100f and < 200f)
                lowest = MathF.Max(lowest, body.GlobalPosition.Y);
        }

        Assert.Equal(1, body.Finished);
        Assert.True(body.Agent.IsTargetReached());
        Assert.True(lowest > 140f, "went under the wall (cutting corners by PathDesiredDistance)");
        var reached = Assert.Single(body.Links);
        Assert.Same(link, reached.Link);
        Assert.Equal(new Vector2(290, 100), reached.EntryPosition);
        Assert.Equal(new Vector2(510, 100), reached.ExitPosition);
        Assert.Same(link, body.Agent.CurrentPath.Owners[reached.Index]);
    }

    [Fact]
    public void AgentsRepathWhenARegionTurnsOff()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Mesh(Box(0, 0, 100, 300)), name: "West");
        AddRegion(h, Mesh(Box(300, 0, 400, 300)), name: "East");
        var bridge = AddRegion(h, Mesh(Box(100, 0, 300, 100)), name: "Bridge");
        AddRegion(h, Mesh(Box(100, 200, 300, 300)), name: "Detour");
        var body = AddJumper(h, new Vector2(50, 50));
        h.Run(1);
        body.Agent.TargetPosition = new Vector2(350, 50);
        h.Run(2);
        var changes = 0;
        body.Agent.PathChanged += () => changes++;
        Assert.DoesNotContain(body.Agent.GetCurrentNavigationPath().ToArray(), p => p.Y > 150f);
        bridge.Enabled = false;
        h.Run(2);
        Assert.True(changes >= 1);
        Assert.Contains(body.Agent.GetCurrentNavigationPath().ToArray(), p => p.Y >= 200f);
    }

    [Fact]
    public void TheDebugDrawShowsPolygonsBordersConnectionsAndLinks()
    {
        using var h = new NavigationHarness(physics: false);
        AddRegion(h, Mesh(Box(0, 0, 100, 100), Box(40, 40, 60, 60)));
        AddRegion(h, Mesh(Box(0, 0, 100, 100)), new Vector2(100.75f, 0), "Right");
        h.Add(new NavigationLink2D { Name = "Link", StartPosition = new Vector2(50, 90), EndPosition = new Vector2(150, 90) });
        h.Navigation.DebugDrawEnabled = true;
        h.Run(1);
        var lines = h.Tree.Root.DebugLines;
        lines.Clear();
        h.Run(1);
        Assert.Single(h.Map.Mesh.EdgeConnections);
        Assert.Equal(4 + 4 - 1 + 4 - 1, h.Map.Mesh.BorderEdges.Count); // both rings and the hole, less the two connected edges
        Assert.True(lines.LineCount > 30);
        h.Navigation.DebugDrawEnabled = false;
        lines.Clear();
        h.Run(1);
        Assert.Equal(0, lines.LineCount);
    }

    [Fact]
    public void SixtyFourAgentsOnANavmeshWithADoorAndALinkAllocateNothing()
    {
        using var h = new NavigationHarness(physics: false);
        h.Navigation.DebugDrawEnabled = true;
        var outlines = new List<Vector2[]> { Box(0, 0, 2048, 2048) };
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                outlines.Add(Box(100 + x * 240 + y * 9, 100 + y * 240, 200 + x * 240, 220 + y * 240 - x * 4));
        AddRegion(h, Mesh([.. outlines]));
        var door = AddRegion(h, Mesh(Box(0, 0, 200, 200)), new Vector2(2048, 900), "Door");
        AddRegion(h, Mesh(Box(0, 0, 400, 400)), new Vector2(2248, 800), "Annex");
        h.Add(new NavigationLink2D { Name = "Jump", StartPosition = new Vector2(2040, 100), EndPosition = new Vector2(2300, 850) });
        var walkers = new Walker2D[64];
        for (var i = 0; i < walkers.Length; i++)
        {
            walkers[i] = h.AddWalker(new Vector2(40 + i % 8 * 250, 40 + i / 8 * 250), $"Walker{i}");
            walkers[i].Speed = 300f;
            walkers[i].Agent.TargetRepathDistance = 64f;
        }

        h.Run(1);
        var tick = 0;
        void Tick()
        {
            for (var i = 0; i < walkers.Length; i++)
            {
                var angle = (tick % 300) / 300f * MathF.Tau + i;
                walkers[i].Agent.TargetPosition = new Vector2(1300, 1024) + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (500f + i * 9f);
            }

            if (tick % 30 == 0)
                door.Enabled = tick / 30 % 2 == 0; // the mesh rebuilds, every agent repaths
            h.Run(1);
            h.Tree.Root.DebugLines.Clear();
            tick++;
        }

        for (var i = 0; i < 600; i++)
            Tick();
        Assert.Equal(0, h.Tree.Root.DebugLines.DroppedLines);

        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 300; i++)
                Tick();
        });
        Assert.Equal(0, allocated);
        Assert.Contains(walkers, w => w.Agent.CurrentPath.Count > 2);
    }
}
