namespace MainframeEngine;

/// <summary>
/// G3.11: the 3D navigation server (Godot's <c>NavigationServer3D</c>): one <see cref="NavigationMap3D"/> per
/// <see cref="World3D"/>, with the 2D server's timing — maps sync after every fixed step (registered after
/// <see cref="PhysicsServer3D"/>), or in <see cref="Process"/> on frames without one, which also debug-draws.
/// Navigation never calls Jitter2: bakes read collision through <see cref="PhysicsDirectSpaceState3D"/>.
/// </summary>
public sealed class NavigationServer3D : IFixedStepServer, IFrameServer
{
    private readonly List<NavigationMap3D> _maps = [];
    private bool _syncedThisFrame;
    private bool _disposed;

    /// <summary>Draws every grid (outline, solid and weighted cells) and every agent's path and target into the viewports' debug lines.</summary>
    public bool DebugDrawEnabled { get; set; }

    public IReadOnlyList<NavigationMap3D> Maps => _maps;

    /// <summary>The tree's 3D navigation server, registering a default one if there is none.</summary>
    public static NavigationServer3D For(SceneTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (tree.Servers.Get<NavigationServer3D>() is { } server)
            return server;
        server = new NavigationServer3D();
        tree.Servers.Register(server);
        return server;
    }

    /// <summary>The map of <paramref name="world"/>, created on first use.</summary>
    public NavigationMap3D GetMap(World3D world)
    {
        ArgumentNullException.ThrowIfNull(world);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (world.NavigationMap is { } existing)
            return existing;
        var map = new NavigationMap3D(world);
        world.NavigationMap = map;
        _maps.Add(map);
        return map;
    }

    internal NavigationMap3D MapOf(Node node)
    {
        var viewport = node.GetViewport() ?? throw new InvalidOperationException($"'{node.Name}' is not inside a viewport.");
        var map = GetMap(viewport.World3D);
        if (map.Viewport is null || !map.Viewport.IsInsideTree)
            map.Viewport = viewport;
        return map;
    }

    public void FixedStep(float delta)
    {
        for (var i = 0; i < _maps.Count; i++)
            _maps[i].Sync();
        _syncedThisFrame = true;
    }

    public void Process(in GameTime gameTime)
    {
        if (!_syncedThisFrame)
            for (var i = 0; i < _maps.Count; i++)
                _maps[i].Sync();
        _syncedThisFrame = false;

        for (var i = _maps.Count - 1; i >= 0; i--)
        {
            var map = _maps[i];
            if (!map.IsEmpty || map.Viewport is { IsInsideTree: true })
                continue;
            _maps.RemoveAt(i);
            if (ReferenceEquals(map.World.NavigationMap, map))
                map.World.NavigationMap = null;
        }

        for (var i = 0; i < _maps.Count; i++)
        {
            var map = _maps[i];
            if (map.Viewport is not { IsInsideTree: true } viewport)
                continue;
            if (DebugDrawEnabled || NavigationDebugDraw3D.HasDebugAgents(map))
            {
                using var scope = NavigationMarkers.DebugDraw.Auto();
                NavigationDebugDraw3D.Draw(map, viewport.DebugLines, DebugDrawEnabled);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var map in _maps)
            if (ReferenceEquals(map.World.NavigationMap, map))
                map.World.NavigationMap = null;
        _maps.Clear();
    }
}
