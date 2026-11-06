using System.Globalization;
using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>
/// The Navigation Mesh 2D scene's controls: carve the pond or not, the navigation debug draw (server-global: restored when
/// the scene is left), the chasers' avoidance, Bake again (with the polygon count and the bake time), ferry crossings and
/// arrivals.
/// </summary>
public sealed class NavigationMesh2DPanel : UiDocument
{
    private RmlDataModel? _model;
    private NavigationMeshDemo2D? _demo;
    private NavigationServer2D? _navigation;
    private bool _enterDebugDraw;
    private int _arrived = -1;
    private int _crossings = -1;
    private int _polygons = -1;

    public NavigationMesh2DPanel()
    {
        Source = "Assets/Navigation/navmesh_panel.rml";
        AutoFocus = false;
    }

    public bool PondCarved
    {
        get => _demo?.PondCarved ?? false;
        set
        {
            if (_demo is { } demo)
                demo.PondCarved = value;
            _model?.Dirty("bake");
        }
    }

    /// <summary>The navigation debug draw (polygons, links, paths).</summary>
    public bool ShowNavigation
    {
        get => _navigation?.DebugDrawEnabled ?? false;
        set
        {
            if (_navigation is { } navigation)
                navigation.DebugDrawEnabled = value;
        }
    }

    public bool Avoidance
    {
        get => _demo?.Avoidance ?? false;
        set
        {
            if (_demo is { } demo)
                demo.Avoidance = value;
        }
    }

    /// <summary>The last bake's time ("" before the first in this scene).</summary>
    public string BakeText => _demo is { LastBakeMilliseconds: > 0 } demo
        ? string.Create(CultureInfo.InvariantCulture, $"{demo.LastBakeMilliseconds:0.0} ms")
        : "";

    private int Polygons => (_demo?.Hall?.NavigationPolygon?.PolygonCount ?? 0) + (_demo?.Bank?.NavigationPolygon?.PolygonCount ?? 0);

    protected override void OnReady()
    {
        _demo = Parent!.Parent!.GetNodeOrNull<NavigationMeshDemo2D>("Controller");
        _navigation = Tree?.Servers.Get<NavigationServer2D>();
        _enterDebugDraw = _navigation?.DebugDrawEnabled ?? false;
        ShowNavigation = true;
        _model = CreateDataModel("navmesh2d")
            .Bind("pond", this, static d => d.PondCarved, static (d, v) => d.PondCarved = v)
            .Bind("debug", this, static d => d.ShowNavigation, static (d, v) => d.ShowNavigation = v)
            .Bind("avoidance", this, static d => d.Avoidance, static (d, v) => d.Avoidance = v)
            .Bind("polygons", this, static d => Math.Max(0, d._polygons))
            .Bind("bake", this, static d => d.BakeText)
            .Bind("crossings", this, static d => Math.Max(0, d._crossings))
            .Bind("arrived", this, static d => Math.Max(0, d._arrived))
            .Bind("total", this, static d => d._demo?.Chasers.Count ?? 0)
            .Event("rebake", () =>
            {
                _demo?.Rebake();
                _model?.Dirty("bake");
            });
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        Refresh(ref _arrived, _demo?.Arrived ?? 0, "arrived");
        Refresh(ref _crossings, _demo?.Crossings ?? 0, "crossings");
        Refresh(ref _polygons, Polygons, "polygons");
    }

    private void Refresh(ref int field, int value, string name)
    {
        if (value == field)
            return;
        field = value;
        _model?.Dirty(name);
    }

    protected override void OnExitTree()
    {
        if (_navigation is { } navigation)
            navigation.DebugDrawEnabled = _enterDebugDraw;
        _navigation = null;
        _demo = null;
        base.OnExitTree();
    }
}
