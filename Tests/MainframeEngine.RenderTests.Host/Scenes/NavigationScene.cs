using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The navigation debug draw (G3a) through a 2D camera: a 20 × 15 grid of 16 px cells baked from three static walls (the
/// collision-shape debug draw on too), a strip of mud (weight 3, dashed), the region outline and cell lines, and three
/// walkers whose agents follow paths round the walls (path, next position, target). Fixed delta: frame 30 always shows
/// the walkers at the same place.
/// </summary>
public sealed class NavigationScene(HostOptions host) : RenderTestGame(host)
{
    protected override void LoadScene()
    {
        // A fixed logical size: the camera shows the same 320 × 240 px grid at 640 × 480 (HiDPI) and 320 × 240 (lavapipe).
        Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        Root.ContentScaleAspect = ContentScaleAspect.Keep;
        Root.ContentScaleSize = new Vector2(640, 480);
        var root = new Node2D { Name = "Navigation" };
        root.AddChild(new Camera2D { Name = "Camera", Current = true, Position = new Vector2(160, 120), Zoom = new Vector2(2) });
        var grid = new NavigationGrid2D { Name = "Grid", Region = new Rect2I(0, 0, 20, 15), CellSize = new Vector2(16) };
        root.AddChild(grid);
        AddWall(root, new Vector2(104, 72), new Vector2(16, 112));  // a column from the top
        AddWall(root, new Vector2(216, 168), new Vector2(16, 112)); // one from the bottom
        AddWall(root, new Vector2(272, 56), new Vector2(64, 16));
        for (var i = 0; i < 3; i++)
        {
            var walker = new NavWalker { Name = $"Walker{i}", Position = new Vector2(24, 40 + i * 80) };
            walker.AddChild(new NavigationAgent2D { Name = "Agent", TargetPosition = new Vector2(296, 200 - i * 70) });
            root.AddChild(walker);
        }

        Tree.ChangeScene(root);
        grid.Bake();
        grid.FillMud();
        Servers.Get<NavigationServer2D>()!.DebugDrawEnabled = true;
        Servers.Get<PhysicsServer2D>()!.DebugDrawEnabled = true;
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }

    private static void AddWall(Node parent, Vector2 center, Vector2 size)
    {
        var wall = new StaticBody2D { Name = $"Wall{parent.ChildCount}", Position = center };
        wall.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = size } });
        parent.AddChild(wall);
    }

    /// <summary>Walks its agent's path at 90 px/s without physics.</summary>
    private sealed class NavWalker : Node2D
    {
        private NavigationAgent2D? _agent;

        protected override void OnReady() => _agent = GetNode<NavigationAgent2D>("Agent");

        protected override void OnPhysicsProcess(float delta)
        {
            if (_agent is not { } agent || agent.IsNavigationFinished())
                return;
            var to = agent.GetNextPathPosition() - GlobalPosition;
            var step = 90f * delta;
            GlobalPosition += to.Length() <= step ? to : Vector2.Normalize(to) * step;
        }
    }
}

internal static class NavigationSceneGrid
{
    /// <summary>A strip of mud (weight 3) across the middle columns of the bottom rows.</summary>
    public static void FillMud(this NavigationGrid2D grid)
    {
        for (var x = 8; x < 12; x++)
            for (var y = 11; y < 14; y++)
                grid.SetCellWeight(new Vector2I(x, y), 3f);
    }
}
