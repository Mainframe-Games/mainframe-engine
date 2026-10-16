using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// The 3D physics server: one <see cref="PhysicsSpace3D"/> per <see cref="World3D"/>, created when the first collision
/// object enters that world, each over a <see cref="PhysicsSettings3D.Backend"/> library (Jitter2; Jolt from B3). Ticked
/// by the scene tree's fixed step (after <see cref="Node.OnPhysicsProcess"/>; frozen while paused); interpolates moving
/// bodies for rendering; draws collision shapes into the viewport's <see cref="DebugLines"/> when <see cref="DebugDrawEnabled"/>.
/// </summary>
/// <remarks>
/// <see cref="Engine"/> registers one with <see cref="EngineOptions.Physics3D"/>. Trees without one (tools, tests) get a
/// default server registered by <see cref="For"/> when the first body enters. Library calls stay inside each backend's
/// folder (<c>Src/Physics/3D/Jitter</c>; the packages are pinned).
/// </remarks>
public sealed class PhysicsServer3D : IFixedStepServer, IFrameServer
{
    private readonly Dictionary<World3D, PhysicsSpace3D> _spaces = [];
    private readonly List<PhysicsSpace3D> _spaceList = [];
    private bool _disposed;
    private bool _warnedJolt;

    public PhysicsServer3D(PhysicsSettings3D? settings = null)
    {
        Settings = settings ?? new PhysicsSettings3D();
    }

    /// <summary>Settings used for new spaces.</summary>
    public PhysicsSettings3D Settings { get; }

    /// <summary>Draw every collision shape each frame (Godot's "Visible Collision Shapes").</summary>
    public bool DebugDrawEnabled { get; set; }

    /// <summary>The spaces created so far (one per world with physics objects).</summary>
    public IReadOnlyList<PhysicsSpace3D> Spaces => _spaceList;

    /// <summary>The tree's 3D physics server, registering a default one if there is none.</summary>
    public static PhysicsServer3D For(SceneTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (tree.Servers.Get<PhysicsServer3D>() is { } server)
            return server;
        server = new PhysicsServer3D();
        tree.Servers.Register(server);
        return server;
    }

    /// <summary>The space simulating <paramref name="world"/>, if one exists.</summary>
    public PhysicsSpace3D? FindSpace(World3D world) => _spaces.GetValueOrDefault(world);

    /// <summary>The backend new spaces get: the setting, or Jitter2 (with one warning per server) where it is not available.</summary>
    internal PhysicsBackendKind ResolveBackend()
    {
        if (Settings.Backend != PhysicsBackendKind.Jolt)
            return Settings.Backend;
        if (!_warnedJolt)
        {
            _warnedJolt = true;
            Log.Warning($"[Physics] Jolt is not available on {RuntimeInformation.RuntimeIdentifier} (the Jolt backend arrives in B3); using Jitter2.");
        }

        return PhysicsBackendKind.Jitter2;
    }

    internal void AddObject(CollisionObject3D node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var viewport = node.GetViewport() ?? throw new InvalidOperationException($"'{node.Name}' is not inside a viewport.");
        if (!_spaces.TryGetValue(viewport.World3D, out var space))
        {
            space = new PhysicsSpace3D(viewport, Settings, ResolveBackend());
            _spaces.Add(viewport.World3D, space);
            _spaceList.Add(space);
        }

        space.AddObject(node);
    }

    // Index loops: a signal handler may add the first body of another viewport (a new space) mid-step.

    public void BeforeFixedSteps()
    {
        for (var i = 0; i < _spaceList.Count; i++)
            _spaceList[i].BeforeFixedSteps();
    }

    public void FixedStep(float delta)
    {
        // Several worlds step one at a time.
        for (var i = 0; i < _spaceList.Count; i++)
            _spaceList[i].Step(delta);
    }

    public void AfterFixedSteps(float interpolationFraction)
    {
        for (var i = 0; i < _spaceList.Count; i++)
            _spaceList[i].AfterFixedSteps(interpolationFraction);
    }

    /// <summary>Per frame (after transform sync): releases spaces of viewports that left the tree; debug drawing.</summary>
    public void Process(in GameTime gameTime)
    {
        for (var i = _spaceList.Count - 1; i >= 0; i--)
        {
            var space = _spaceList[i];
            if (space.ObjectCount > 0 || space.Viewport.IsInsideTree)
                continue;
            _spaces.Remove(space.World3D);
            _spaceList.RemoveAt(i);
            space.Dispose();
        }

        if (!DebugDrawEnabled)
            return;
        for (var i = 0; i < _spaceList.Count; i++)
            _spaceList[i].DrawDebug(_spaceList[i].Viewport.DebugLines);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var space in _spaceList)
            space.Dispose();
        _spaceList.Clear();
        _spaces.Clear();
    }
}
