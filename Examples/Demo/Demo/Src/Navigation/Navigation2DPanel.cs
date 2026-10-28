using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>
/// The Navigation 2D scene's controls: the grid's diagonal mode and heuristic, jump point search, the navigation debug
/// draw (server-global: restored when the scene is left), "Click adds walls", and how many chasers arrived.
/// </summary>
public sealed class Navigation2DPanel : UiDocument
{
    private RmlDataModel? _model;
    private NavigationDemo2D? _demo;
    private NavigationServer2D? _navigation;
    private bool _enterDebugDraw;
    private int _arrived = -1;

    public Navigation2DPanel()
    {
        Source = "Assets/Navigation/panel.rml";
        AutoFocus = false;
    }

    public NavigationGrid2D? Grid => _demo?.Grid;

    /// <summary>The grid's diagonal mode by name (the select's values).</summary>
    public string Diagonal
    {
        get => (Grid?.DiagonalMode ?? GridDiagonalMode.OnlyIfNoObstacles) switch
        {
            GridDiagonalMode.Always => "always",
            GridDiagonalMode.Never => "never",
            GridDiagonalMode.AtLeastOneWalkable => "one",
            _ => "none",
        };
        set
        {
            if (Grid is { } grid)
                grid.DiagonalMode = value switch
                {
                    "always" => GridDiagonalMode.Always,
                    "never" => GridDiagonalMode.Never,
                    "one" => GridDiagonalMode.AtLeastOneWalkable,
                    _ => GridDiagonalMode.OnlyIfNoObstacles,
                };
        }
    }

    /// <summary>The grid's heuristic by name.</summary>
    public string Heuristic
    {
        get => (Grid?.Heuristic ?? GridHeuristic.Octile) switch
        {
            GridHeuristic.Euclidean => "euclidean",
            GridHeuristic.Manhattan => "manhattan",
            GridHeuristic.Chebyshev => "chebyshev",
            _ => "octile",
        };
        set
        {
            if (Grid is { } grid)
                grid.Heuristic = value switch
                {
                    "euclidean" => GridHeuristic.Euclidean,
                    "manhattan" => GridHeuristic.Manhattan,
                    "chebyshev" => GridHeuristic.Chebyshev,
                    _ => GridHeuristic.Octile,
                };
        }
    }

    public bool Jumping
    {
        get => Grid?.JumpingEnabled ?? false;
        set
        {
            if (Grid is { } grid)
                grid.JumpingEnabled = value;
        }
    }

    /// <summary>The navigation debug draw (grids, paths, targets).</summary>
    public bool ShowNavigation
    {
        get => _navigation?.DebugDrawEnabled ?? false;
        set
        {
            if (_navigation is { } navigation)
                navigation.DebugDrawEnabled = value;
        }
    }

    public bool AddWalls
    {
        get => _demo?.AddWalls ?? false;
        set
        {
            if (_demo is { } demo)
                demo.AddWalls = value;
        }
    }

    protected override void OnReady()
    {
        _demo = Parent!.Parent!.GetNodeOrNull<NavigationDemo2D>("Controller");
        // The debug draw is server-global: remember it so leaving this scene restores the other scenes' look.
        _navigation = Tree?.Servers.Get<NavigationServer2D>();
        _enterDebugDraw = _navigation?.DebugDrawEnabled ?? false;
        ShowNavigation = true;
        _model = CreateDataModel("navigation2d")
            .Bind("diagonal", this, static d => d.Diagonal, static (d, v) => d.Diagonal = v)
            .Bind("heuristic", this, static d => d.Heuristic, static (d, v) => d.Heuristic = v)
            .Bind("jumping", this, static d => d.Jumping, static (d, v) => d.Jumping = v)
            .Bind("debug", this, static d => d.ShowNavigation, static (d, v) => d.ShowNavigation = v)
            .Bind("walls", this, static d => d.AddWalls, static (d, v) => d.AddWalls = v)
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
