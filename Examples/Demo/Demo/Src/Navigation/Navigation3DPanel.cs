using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>The Navigation 3D scene's controls: the navigation debug draw (restored when the scene is left) and arrivals.</summary>
public sealed class Navigation3DPanel : UiDocument
{
    private RmlDataModel? _model;
    private NavigationDemo3D? _demo;
    private NavigationServer3D? _navigation;
    private bool _enterDebugDraw;
    private int _arrived = -1;

    public Navigation3DPanel()
    {
        Source = "Assets/Navigation/navigation_3d_panel.rml";
        AutoFocus = false;
    }

    /// <summary>The 3D navigation debug draw (the grid, solid cells, paths).</summary>
    public bool ShowNavigation
    {
        get => _navigation?.DebugDrawEnabled ?? false;
        set
        {
            if (_navigation is { } navigation)
                navigation.DebugDrawEnabled = value;
        }
    }

    protected override void OnReady()
    {
        _demo = Parent!.Parent!.GetNodeOrNull<NavigationDemo3D>("Controller");
        _navigation = Tree?.Servers.Get<NavigationServer3D>();
        _enterDebugDraw = _navigation?.DebugDrawEnabled ?? false;
        ShowNavigation = true;
        _model = CreateDataModel("navigation3d")
            .Bind("debug", this, static d => d.ShowNavigation, static (d, v) => d.ShowNavigation = v)
            .Bind("arrived", this, static d => Math.Max(0, d._arrived))
            .Bind("total", this, static d => d._demo?.Chasers.Count ?? 0);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        var arrived = _demo?.Arrived ?? 0;
        if (arrived == _arrived)
            return;
        _arrived = arrived;
        _model?.Dirty("arrived");
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
