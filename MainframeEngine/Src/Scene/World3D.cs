namespace MainframeEngine;

/// <summary>
/// The render scenario of a <see cref="SceneViewport"/> (Godot's <c>World3D</c>): the visuals, lights and
/// environment registered by nodes inside it. The <see cref="RenderServer"/> draws it with the viewport's
/// camera. Separate viewports (the editor's scene views) get separate worlds.
/// </summary>
public sealed class World3D
{
    private readonly List<VisualInstance3D> _visuals = [];
    private readonly List<GeometryInstance3D> _geometry = [];
    private readonly List<WorldEnvironment> _environments = [];
    private readonly List<LightProbeVolume> _probeVolumes = [];

    public World3D()
    {
        DirectSpaceState = new PhysicsDirectSpaceState3D(this);
    }

    /// <summary>
    /// Raycasts, shape casts and overlap queries against this world's physics (Godot's
    /// <c>World3D.direct_space_state</c>). Finds nothing until a physics body has entered the world.
    /// </summary>
    public PhysicsDirectSpaceState3D DirectSpaceState { get; }

    /// <summary>The physics space simulating this world, created by the <see cref="PhysicsServer3D"/> for its first body.</summary>
    public PhysicsSpace3D? PhysicsSpace { get; internal set; }

    /// <summary>Water queries over this world's water bodies (<see cref="River3D"/>, …): depth, surface height, flow.</summary>
    public WaterQueries Water { get; } = new();

    /// <summary>Lights registered by light nodes, packed into the lights UBO by the renderer.</summary>
    public LightEnvironment Lights { get; } = new();

    /// <summary>Visual nodes, sorted by <see cref="VisualInstance3D.RenderPriority"/> then tree-entry order.</summary>
    public IReadOnlyList<VisualInstance3D> Visuals => _visuals;

    internal List<VisualInstance3D> VisualList => _visuals;

    /// <summary>The <see cref="GeometryInstance3D"/>s among <see cref="Visuals"/> (batched by the mesh renderer), in entry order.</summary>
    public IReadOnlyList<GeometryInstance3D> Geometry => _geometry;

    internal List<GeometryInstance3D> GeometryList => _geometry;

    /// <summary>The first <see cref="WorldEnvironment"/> in the world (sky and ambient light), or null.</summary>
    public WorldEnvironment? Environment => _environments.Count > 0 ? _environments[0] : null;

    /// <summary>
    /// The baked light probes this world's lit surfaces sample (ADR 0170): the first visible <see cref="LightProbeVolume"/>
    /// with data, or null.
    /// </summary>
    public LightProbeVolume? ProbeVolume
    {
        get
        {
            foreach (var volume in _probeVolumes)
                if (volume.RenderData is not null && volume.IsVisibleInTree())
                    return volume;
            return null;
        }
    }

    internal void AddProbeVolume(LightProbeVolume volume)
    {
        _probeVolumes.Add(volume);
        if (_probeVolumes.Count == 2)
            Log.Warning($"World has more than one LightProbeVolume; the first visible one with data ('{_probeVolumes[0].Name}') is used.");
    }

    internal void RemoveProbeVolume(LightProbeVolume volume) => _probeVolumes.Remove(volume);

    internal void AddVisual(VisualInstance3D visual)
    {
        // Stable insert: after every visual with a lower or equal priority.
        var index = _visuals.Count;
        while (index > 0 && _visuals[index - 1].RenderPriority > visual.RenderPriority)
            index--;
        _visuals.Insert(index, visual);
        if (visual is GeometryInstance3D geometry)
            _geometry.Add(geometry);
    }

    internal void RemoveVisual(VisualInstance3D visual)
    {
        var index = _visuals.LastIndexOf(visual);
        if (index >= 0)
            _visuals.RemoveAt(index);
        if (visual is GeometryInstance3D geometry)
        {
            // Swap-remove: the batcher sorts every frame, so order does not matter (O(1) for large worlds).
            var g = _geometry.LastIndexOf(geometry);
            if (g >= 0)
            {
                _geometry[g] = _geometry[^1];
                _geometry.RemoveAt(_geometry.Count - 1);
            }
        }
    }

    internal void ResortVisual(VisualInstance3D visual)
    {
        var index = _visuals.LastIndexOf(visual);
        if (index < 0)
            return;
        _visuals.RemoveAt(index);
        index = _visuals.Count;
        while (index > 0 && _visuals[index - 1].RenderPriority > visual.RenderPriority)
            index--;
        _visuals.Insert(index, visual);
    }

    internal void AddEnvironment(WorldEnvironment environment)
    {
        _environments.Add(environment);
        if (_environments.Count > 1)
            Log.Warning($"World has more than one WorldEnvironment; '{_environments[0].Name}' is used.");
        Lights.AmbientColor = Environment!.AmbientColor;
    }

    internal void RemoveEnvironment(WorldEnvironment environment)
    {
        _environments.Remove(environment);
        if (Environment is { } current)
            Lights.AmbientColor = current.AmbientColor;
    }
}

/// <summary>
/// The 2D world of a <see cref="SceneViewport"/>: its 2D physics space and queries. 2D rendering arrives with sprites
/// and the UI (M8).
/// </summary>
public sealed class World2D
{
    public World2D()
    {
        DirectSpaceState = new PhysicsDirectSpaceState2D(this);
    }

    /// <summary>Raycasts, shape casts and overlap queries against this world's 2D physics (pixels).</summary>
    public PhysicsDirectSpaceState2D DirectSpaceState { get; }

    /// <summary>The physics space simulating this world, created by the <see cref="PhysicsServer2D"/> for its first body.</summary>
    public PhysicsSpace2D? PhysicsSpace { get; internal set; }
}
