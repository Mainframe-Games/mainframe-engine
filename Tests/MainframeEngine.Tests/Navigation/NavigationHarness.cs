using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>A headless tree with the 2D physics and navigation servers, stepped at a fixed 60 Hz.</summary>
internal sealed class NavigationHarness : IDisposable
{
    public const float Step = 1f / 60f;

    public NavigationHarness(bool physics = true)
    {
        var servers = new ServerRegistry();
        if (physics)
        {
            Physics = new PhysicsServer2D();
            servers.Register(Physics);
        }

        Navigation = new NavigationServer2D();
        servers.Register(Navigation);
        Tree = new SceneTree(servers);
        Scene = new Node2D { Name = "Scene" };
        Tree.ChangeScene(Scene);
    }

    public SceneTree Tree { get; }
    public PhysicsServer2D? Physics { get; }
    public NavigationServer2D Navigation { get; }
    public Node2D Scene { get; }

    public NavigationMap2D Map => Navigation.GetMap(Tree.Root.World2D);

    public void Run(int frames, float delta = Step)
    {
        var time = new GameTime { DeltaTime = delta };
        for (var i = 0; i < frames; i++)
            Tree.Tick(time);
    }

    public T Add<T>(T node) where T : Node
    {
        Scene.AddChild(node);
        return node;
    }

    public StaticBody2D AddWall(Vector2 center, Vector2 size, string name = "Wall")
    {
        var wall = new StaticBody2D { Name = name, Position = center };
        wall.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = size } });
        return Add(wall);
    }

    /// <summary>A grid of <paramref name="width"/> × <paramref name="height"/> cells of 32 px at the origin.</summary>
    public NavigationGrid2D AddGrid(int width, int height, float cell = 32f, string name = "Grid")
    {
        var grid = new NavigationGrid2D { Name = name, Region = new Rect2I(0, 0, width, height), CellSize = new Vector2(cell) };
        return Add(grid);
    }

    public void Dispose()
    {
        Tree.Shutdown();
        Tree.Servers.Dispose();
    }

    /// <summary>A walker with an agent child at <paramref name="position"/>.</summary>
    public Walker2D AddWalker(Vector2 position, string name = "Walker")
    {
        var walker = new Walker2D { Name = name, Position = position };
        walker.AddChild(new NavigationAgent2D { Name = "Agent" });
        return Add(walker);
    }
}

/// <summary>A body without physics that walks to its agent's next path position (the allocation gate's chaser).</summary>
internal sealed class Walker2D : Node2D
{
    public float Speed { get; set; } = 240f;

    private NavigationAgent2D? _agent;

    public NavigationAgent2D Agent => _agent ??= GetNode<NavigationAgent2D>("Agent");

    protected override void OnPhysicsProcess(float delta)
    {
        if (Agent.IsNavigationFinished())
            return;
        var next = Agent.GetNextPathPosition();
        var to = next - GlobalPosition;
        var step = Speed * delta;
        GlobalPosition = to.Length() <= step ? next : GlobalPosition + Vector2.Normalize(to) * step;
    }
}
