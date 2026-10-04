namespace MainframeEngine;

/// <summary>
/// The render scenario of a <see cref="SceneViewport"/> (Godot's <c>World3D</c>): the visuals, lights and
/// environment registered by nodes inside it. The <see cref="RenderServer"/> draws it with the viewport's
/// camera. Separate viewports (the editor's scene views) get separate worlds.
/// </summary>
public sealed class World3D
{
    private readonly List<VisualInstance3D> _visuals = [];
    private readonly List<WorldEnvironment> _environments = [];

    /// <summary>Lights registered by light nodes, packed into the lights UBO by the renderer.</summary>
    public LightEnvironment Lights { get; } = new();

    /// <summary>Visual nodes, sorted by <see cref="VisualInstance3D.RenderPriority"/> then tree-entry order.</summary>
    public IReadOnlyList<VisualInstance3D> Visuals => _visuals;

    internal List<VisualInstance3D> VisualList => _visuals;

    /// <summary>The first <see cref="WorldEnvironment"/> in the world (sky and ambient light), or null.</summary>
    public WorldEnvironment? Environment => _environments.Count > 0 ? _environments[0] : null;

    internal void AddVisual(VisualInstance3D visual)
    {
        // Stable insert: after every visual with a lower or equal priority.
        var index = _visuals.Count;
        while (index > 0 && _visuals[index - 1].RenderPriority > visual.RenderPriority)
            index--;
        _visuals.Insert(index, visual);
    }

    internal void RemoveVisual(VisualInstance3D visual)
    {
        var index = _visuals.LastIndexOf(visual);
        if (index >= 0)
            _visuals.RemoveAt(index);
    }

    internal void ResortVisual(VisualInstance3D visual)
    {
        RemoveVisual(visual);
        AddVisual(visual);
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

/// <summary>The 2D world of a <see cref="SceneViewport"/>. 2D rendering arrives with sprites and the UI (M8).</summary>
public sealed class World2D;
