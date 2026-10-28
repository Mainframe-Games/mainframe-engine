namespace MainframeEngine;

/// <summary>
/// The 2D navigation server (Godot's <c>NavigationServer2D</c>): one <see cref="NavigationMap2D"/> per
/// <see cref="World2D"/>. Registered by the engine right after <see cref="PhysicsServer2D"/> (so its fixed step runs after
/// the physics step) and by <see cref="HeadlessHost"/>; unit tests get one from <see cref="For"/>. Maps sync after every
/// fixed step, or in <see cref="Process"/> on frames without one (edit mode, a paused tree); <see cref="Process"/> also
/// debug-draws. Navigation never calls Box2D: bakes read collision through <see cref="PhysicsDirectSpaceState2D"/>.
/// </summary>
public sealed class NavigationServer2D : IFixedStepServer, IFrameServer
{
    private readonly Dictionary<World2D, NavigationMap2D> _maps = [];
    private readonly List<NavigationMap2D> _mapList = [];
    private bool _syncedThisFrame;
    private bool _disposed;

    /// <summary>
    /// Draw every grid (outline, cells, solid and weighted cells) and every agent's path, next position and target into
    /// the viewports' <see cref="SceneViewport.DebugLines"/>, culled to the view. <see cref="EngineOptions.DebugNavigation"/>
    /// starts it on; the dev overlay's Physics panel and the editor's Visible Navigation toggle switch it.
    /// </summary>
    public bool DebugDrawEnabled { get; set; }

    public IReadOnlyList<NavigationMap2D> Maps => _mapList;

    /// <summary>The tree's navigation server, registering a default one if there is none.</summary>
    public static NavigationServer2D For(SceneTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (tree.Servers.Get<NavigationServer2D>() is { } server)
            return server;
        server = new NavigationServer2D();
        tree.Servers.Register(server);
        return server;
    }

    /// <summary>The map of <paramref name="world"/>, created on first use.</summary>
    public NavigationMap2D GetMap(World2D world)
    {
        ArgumentNullException.ThrowIfNull(world);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (world.NavigationMap is { } existing)
            return existing;
        var map = new NavigationMap2D(world);
        world.NavigationMap = map;
        _maps.Add(world, map);
        _mapList.Add(map);
        return map;
    }

    /// <summary>The map of <paramref name="node"/>'s viewport (which also becomes the map's debug-draw viewport).</summary>
    internal NavigationMap2D MapOf(Node node)
    {
        var viewport = node.GetViewport() ?? throw new InvalidOperationException($"'{node.Name}' is not inside a viewport.");
        var map = GetMap(viewport.World2D);
        if (map.Viewport is null || !map.Viewport.IsInsideTree)
            map.Viewport = viewport;
        return map;
    }

    public void FixedStep(float delta)
    {
        for (var i = 0; i < _mapList.Count; i++)
            _mapList[i].Sync();
        _syncedThisFrame = true;
    }

    /// <summary>Per frame: syncs maps when no fixed step did; releases maps of worlds that left the tree; debug draws.</summary>
    public void Process(in GameTime gameTime)
    {
        if (!_syncedThisFrame)
            for (var i = 0; i < _mapList.Count; i++)
                _mapList[i].Sync();
        _syncedThisFrame = false;

        for (var i = _mapList.Count - 1; i >= 0; i--)
        {
            var map = _mapList[i];
            if (!map.IsEmpty || map.Viewport is { IsInsideTree: true })
                continue;
            _maps.Remove(map.World);
            _mapList.RemoveAt(i);
            if (ReferenceEquals(map.World.NavigationMap, map))
                map.World.NavigationMap = null;
        }

        for (var i = 0; i < _mapList.Count; i++)
        {
            var map = _mapList[i];
            if (map.Viewport is not { IsInsideTree: true } viewport)
                continue;
            if (DebugDrawEnabled || NavigationDebugDraw.HasDebugAgents(map))
            {
                using var scope = NavigationMarkers.DebugDraw.Auto();
                NavigationDebugDraw.Draw(map, viewport, DebugDrawEnabled);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var map in _mapList)
            if (ReferenceEquals(map.World.NavigationMap, map))
                map.World.NavigationMap = null;
        _mapList.Clear();
        _maps.Clear();
    }
}

/// <summary>The navigation profiler scopes (G10).</summary>
internal static class NavigationMarkers
{
    public static readonly ProfilerMarker Sync = new("Navigation sync", ProfilerCategory.Engine, ProfilerMarkerKind.Cpu, "", 0);
    public static readonly ProfilerMarker Query = new("Navigation query", ProfilerCategory.Engine, ProfilerMarkerKind.Cpu, "", 0);
    public static readonly ProfilerMarker Bake = new("Navigation bake", ProfilerCategory.Loading, ProfilerMarkerKind.Cpu, "", 0);
    public static readonly ProfilerMarker DebugDraw = new("Navigation debug draw", ProfilerCategory.Render, ProfilerMarkerKind.Cpu, "", 0);
}
