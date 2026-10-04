using System.Numerics;
using Box2D.NET;
using static Box2D.NET.B2MathFunction;

namespace MainframeEngine;

/// <summary>A ray hit from <see cref="PhysicsDirectSpaceState2D.RayCast"/> (pixels).</summary>
public readonly record struct RayHit2D(CollisionObject2D Collider, CollisionShape2D? Shape, Vector2 Position, Vector2 Normal, float Fraction);

/// <summary>The first contact of a swept shape from <see cref="PhysicsDirectSpaceState2D.ShapeCast"/> (pixels).</summary>
public readonly record struct ShapeCastHit2D(CollisionObject2D Collider, CollisionShape2D? Shape, Vector2 Position, Vector2 Normal, float Fraction);

/// <summary>
/// Immediate queries against a <see cref="World2D"/>'s physics (Godot's <c>PhysicsDirectSpaceState2D</c>), in pixels:
/// raycasts, shape casts and overlap tests. Same rules as <see cref="PhysicsDirectSpaceState3D"/>.
/// </summary>
public sealed class PhysicsDirectSpaceState2D
{
    private readonly World2D _world;

    internal PhysicsDirectSpaceState2D(World2D world)
    {
        _world = world;
    }

    /// <summary>Casts a ray from <paramref name="from"/> to <paramref name="to"/>; returns the closest body hit.</summary>
    public bool RayCast(Vector2 from, Vector2 to, out RayHit2D hit, uint collisionMask = CollisionLayers.All, CollisionObject2D? exclude = null)
    {
        hit = default;
        return _world.PhysicsSpace is { } space && space.RayCast(from, to, out hit, collisionMask, exclude);
    }

    /// <summary>Sweeps <paramref name="shape"/> (convex) placed by <paramref name="from"/> along <paramref name="motion"/>.</summary>
    public bool ShapeCast(Shape2D shape, in Transform2D from, Vector2 motion, out ShapeCastHit2D hit,
        uint collisionMask = CollisionLayers.All, CollisionObject2D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        hit = default;
        if (_world.PhysicsSpace is not { } space)
            return false;
        return space.ShapeCast(MakeProxy(space, shape, from), motion, out hit, collisionMask, exclude);
    }

    /// <summary>Adds the bodies overlapping <paramref name="shape"/> at <paramref name="transform"/> to <paramref name="results"/>.</summary>
    public int IntersectShape(Shape2D shape, in Transform2D transform, List<CollisionObject2D> results,
        uint collisionMask = CollisionLayers.All, CollisionObject2D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(results);
        if (_world.PhysicsSpace is not { } space)
            return 0;
        return space.IntersectShape(MakeProxy(space, shape, transform), results, collisionMask, exclude);
    }

    /// <summary>Adds the bodies containing <paramref name="point"/> to <paramref name="results"/>.</summary>
    public int IntersectPoint(Vector2 point, List<CollisionObject2D> results, uint collisionMask = CollisionLayers.All, CollisionObject2D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        return _world.PhysicsSpace is { } space ? space.IntersectPoint(point, results, collisionMask, exclude) : 0;
    }

    [ThreadStatic]
    private static List<ShapeGeometry2D>? t_geometry;

    private static B2ShapeProxy MakeProxy(PhysicsSpace2D space, Shape2D shape, in Transform2D transform)
    {
        if (shape.IsConcave && shape is not SegmentShape2D)
            throw new NotSupportedException($"{shape.GetType().Name} cannot be used as a query shape.");
        var geometry = t_geometry ??= new List<ShapeGeometry2D>(4);
        geometry.Clear();
        shape.CreateGeometry(geometry, transform, space.PixelsPerMeter);
        if (geometry.Count == 0)
            throw new ArgumentException($"{shape.GetType().Name} produced no geometry.", nameof(shape));
        var proxy = geometry[0].MakeProxy(b2Vec2_zero, b2Rot_identity);
        geometry.Clear();
        return proxy;
    }
}

/// <summary>
/// The 2D physics server (Box2D.NET 3.1, a C# port of Box2D v3): one <see cref="PhysicsSpace2D"/> per
/// <see cref="World2D"/>. Nodes work in pixels; the server converts with <see cref="PhysicsSettings2D.PixelsPerMeter"/>.
/// Same lifecycle as <see cref="PhysicsServer3D"/>. Box2D steps single-threaded.
/// </summary>
public sealed class PhysicsServer2D : IFixedStepServer, IFrameServer
{
    private readonly Dictionary<World2D, PhysicsSpace2D> _spaces = [];
    private readonly List<PhysicsSpace2D> _spaceList = [];
    private bool _disposed;

    public PhysicsServer2D(PhysicsSettings2D? settings = null)
    {
        Settings = settings ?? new PhysicsSettings2D();
    }

    public PhysicsSettings2D Settings { get; }

    /// <summary>Draw every 2D collision shape each frame into the viewport's <see cref="DebugLines"/> (z = 0 plane, pixels).</summary>
    public bool DebugDrawEnabled { get; set; }

    public IReadOnlyList<PhysicsSpace2D> Spaces => _spaceList;

    /// <summary>The tree's 2D physics server, registering a default one if there is none.</summary>
    public static PhysicsServer2D For(SceneTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (tree.Servers.Get<PhysicsServer2D>() is { } server)
            return server;
        server = new PhysicsServer2D();
        tree.Servers.Register(server);
        return server;
    }

    public PhysicsSpace2D? FindSpace(World2D world) => _spaces.GetValueOrDefault(world);

    internal void AddObject(CollisionObject2D node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var viewport = node.GetViewport() ?? throw new InvalidOperationException($"'{node.Name}' is not inside a viewport.");
        if (!_spaces.TryGetValue(viewport.World2D, out var space))
        {
            space = new PhysicsSpace2D(viewport, Settings);
            _spaces.Add(viewport.World2D, space);
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
            _spaces.Remove(space.World2D);
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
