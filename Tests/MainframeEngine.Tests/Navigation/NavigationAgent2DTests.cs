using System.Numerics;
using MainframeEngine.Tests.Physics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.4: <see cref="NavigationAgent2D"/> — path following, signal order, the repath rules, unreachable targets.</summary>
[Collection(nameof(SerialBox2D))]
public sealed class NavigationAgent2DTests
{
    /// <summary>Godot's pattern: a character body asks its agent where to go and slides there.</summary>
    private sealed class Chaser : CharacterBody2D
    {
        public float Speed { get; set; } = 160f;

        public NavigationAgent2D Agent { get; private set; } = null!;

        protected override void OnReady() => Agent = GetNode<NavigationAgent2D>("Agent");

        protected override void OnPhysicsProcess(float delta)
        {
            if (Agent.IsNavigationFinished())
                return;
            Velocity = GlobalPosition.DirectionTo(Agent.GetNextPathPosition()) * Speed;
            MoveAndSlide();
        }
    }

    // A 20 × 12 grid of 20 px cells with a wall down the middle (a gap at the bottom), baked from a static body.
    private static (NavigationHarness Harness, NavigationGrid2D Grid) WalledLevel()
    {
        var h = new NavigationHarness();
        var grid = h.AddGrid(20, 12, 20f);
        h.AddWall(new Vector2(200, 100), new Vector2(20, 200)); // x 190..210, y 0..200 (cells x 9..10, y 0..9)
        h.Run(1);
        grid.Bake();
        h.Run(1);
        return (h, grid);
    }

    private static Chaser AddChaser(NavigationHarness h, Vector2 at)
    {
        var chaser = new Chaser { Name = "Chaser", Position = at };
        chaser.AddChild(new CollisionShape2D { Shape = new CircleShape2D { Radius = 6 } });
        chaser.AddChild(new NavigationAgent2D { Name = "Agent", PathDesiredDistance = 8f, TargetDesiredDistance = 6f });
        return h.Add(chaser);
    }

    [Fact]
    public void ACharacterBodyWalksAroundTheWallToTheTargetWithSignalsInOrder()
    {
        var (h, _) = WalledLevel();
        using var _h = h;
        var chaser = AddChaser(h, new Vector2(60, 40));
        h.Run(1);
        var events = new List<string>();
        var agent = chaser.Agent;
        agent.PathChanged += () => events.Add("path");
        agent.WaypointReached += w => events.Add($"waypoint {w.Index}");
        agent.TargetReached += () => events.Add("target");
        agent.NavigationFinished += () => events.Add("finished");
        agent.TargetPosition = new Vector2(340, 40);

        for (var i = 0; i < 600 && !agent.IsNavigationFinished(); i++)
            h.Run(1);

        Assert.True(agent.IsNavigationFinished());
        Assert.True(agent.IsTargetReached());
        Assert.True(agent.IsTargetReachable());
        Assert.True(Vector2.Distance(chaser.GlobalPosition, new Vector2(340, 40)) < 6f, $"at {chaser.GlobalPosition}");
        Assert.Equal("path", events[0]);
        Assert.Equal("waypoint 0", events[1]);
        Assert.Equal(["target", "finished"], events[^2..]);
        Assert.Single(events, e => e == "finished");
        var waypoints = events.Where(e => e.StartsWith("waypoint", StringComparison.Ordinal)).ToList();
        Assert.True(waypoints.Count >= 3, "it went round the wall's end"); // start, the gap's corners, ...
        Assert.Equal(waypoints, waypoints.Distinct().ToList());

        // Once finished nothing updates, even when the map changes.
        var count = events.Count;
        h.Map.Regions[0].SetCellSolid(new Vector2I(0, 0), true);
        h.Run(30);
        Assert.Equal(count, events.Count);
    }

    [Fact]
    public void AWallAddedOnThePathMakesTheAgentRepath()
    {
        var (h, grid) = WalledLevel();
        using var _h = h;
        var walker = h.AddWalker(new Vector2(30, 230));
        h.Run(1);
        var agent = walker.Agent;
        var changes = 0;
        agent.PathChanged += () => changes++;
        agent.TargetPosition = new Vector2(370, 230); // straight along the bottom row
        h.Run(10);
        Assert.Equal(1, changes);
        Assert.All(agent.GetCurrentNavigationPath().ToArray(), p => Assert.True(p.Y > 200));

        // Block the bottom row ahead: the next sync bumps the map, the agent repaths over row 10.
        grid.SetCellSolid(new Vector2I(14, 11), true);
        h.Run(2);
        Assert.Equal(2, changes);
        Assert.DoesNotContain(new Vector2I(14, 11), agent.CurrentPath.Cells.ToArray());
        for (var i = 0; i < 400 && !agent.IsNavigationFinished(); i++)
            h.Run(1);
        Assert.True(agent.IsTargetReached());
    }

    [Fact]
    public void TheTargetRepathDistanceKeepsThePathWhileTheTargetWiggles()
    {
        using var h = new NavigationHarness(physics: false);
        h.AddGrid(30, 10, 20f);
        var walker = h.AddWalker(new Vector2(30, 30));
        walker.Speed = 60f;
        h.Run(1);
        var agent = walker.Agent;
        agent.TargetRepathDistance = 30f;
        var changes = 0;
        agent.PathChanged += () => changes++;
        agent.TargetPosition = new Vector2(500, 100);
        h.Run(2);
        Assert.Equal(1, changes);
        for (var i = 0; i < 10; i++)
        {
            agent.TargetPosition = new Vector2(500 + i, 100 + i); // within 30 px of the path's target
            h.Run(1);
        }

        Assert.Equal(1, changes);
        agent.TargetPosition = new Vector2(560, 100);
        h.Run(1);
        Assert.Equal(2, changes);

        // 0 (Godot's behaviour): every change repaths.
        agent.TargetRepathDistance = 0f;
        agent.TargetPosition = new Vector2(561, 100);
        h.Run(1);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void BeingPushedFartherThanPathMaxDistanceRepaths()
    {
        using var h = new NavigationHarness(physics: false);
        h.AddGrid(30, 30, 20f);
        var walker = h.AddWalker(new Vector2(30, 30));
        walker.Speed = 40f;
        h.Run(1);
        var agent = walker.Agent;
        agent.PathPostprocessing = NavigationPathPostprocessing.None;
        agent.PathMaxDistance = 50f;
        var changes = 0;
        agent.PathChanged += () => changes++;
        agent.TargetPosition = new Vector2(570, 30);
        h.Run(20);
        Assert.Equal(1, changes);
        walker.GlobalPosition += new Vector2(0, 200); // knocked down the map
        h.Run(1);
        Assert.Equal(2, changes);
        Assert.True(agent.GetCurrentNavigationPath()[0].Y > 200);
    }

    [Fact]
    public void AnUnreachableTargetGivesAPartialPathAndFinishesAtItsEnd()
    {
        var (h, grid) = WalledLevel();
        using var _h = h;
        grid.SetCellSolid(new Vector2I(9, 10), true); // close the gap
        grid.SetCellSolid(new Vector2I(10, 10), true);
        grid.SetCellSolid(new Vector2I(9, 11), true);
        grid.SetCellSolid(new Vector2I(10, 11), true);
        var walker = h.AddWalker(new Vector2(60, 40));
        h.Run(1);
        var agent = walker.Agent;
        var finished = 0;
        var reached = 0;
        agent.NavigationFinished += () => finished++;
        agent.TargetReached += () => reached++;
        agent.TargetPosition = new Vector2(340, 40);
        h.Run(1);
        Assert.Equal(GridPathStatus.Partial, agent.CurrentPath.Status);
        Assert.False(agent.IsTargetReachable());
        for (var i = 0; i < 300 && !agent.IsNavigationFinished(); i++)
            h.Run(1);
        Assert.Equal(1, finished);
        Assert.Equal(0, reached);
        Assert.True(walker.GlobalPosition.X < 190f); // stopped on its side of the wall
        Assert.True(agent.DistanceToTarget() > 100f);
    }

    [Fact]
    public void WithoutATargetOrAMapTheAgentStaysPut()
    {
        using var h = new NavigationHarness(physics: false);
        var walker = h.AddWalker(new Vector2(30, 30));
        h.Run(1);
        var agent = walker.Agent;
        Assert.True(agent.IsNavigationFinished());
        Assert.Equal(new Vector2(30, 30), agent.GetNextPathPosition());
        agent.TargetPosition = new Vector2(100, 30); // no region anywhere
        Assert.Equal(new Vector2(30, 30), agent.GetNextPathPosition());
        Assert.True(agent.IsNavigationFinished());
        Assert.Equal(0, agent.GetCurrentNavigationPath().Length);
        Assert.Equal(new Vector2(30, 30), agent.GetFinalPosition());

        Vector2? computed = null;
        agent.VelocityComputed += v => computed = v;
        agent.SetVelocity(new Vector2(3, 4));
        Assert.Equal(new Vector2(3, 4), computed); // a pass-through until avoidance
    }

    [Fact]
    public void AgentsDrawTheirPathsWhenDebugIsOn()
    {
        using var h = new NavigationHarness(physics: false);
        h.AddGrid(10, 10, 20f);
        var walker = h.AddWalker(new Vector2(30, 30));
        h.Run(1);
        walker.Agent.DebugEnabled = true;
        walker.Agent.TargetPosition = new Vector2(170, 170);
        h.Run(1);
        var lines = h.Tree.Root.DebugLines;
        Assert.True(lines.LineCount > 0); // only this agent: no grid lines with the server's draw off
        Assert.True(lines.LineCount < 64);
    }
}
