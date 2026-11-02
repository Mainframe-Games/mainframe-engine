using MainframeEngine;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace Demo;

/// <summary>
/// The Tiles 2D scene's controls (G2b, G3.6): show the tiles' collision shapes and the navigation grid, y-sort the objects
/// layer on or off, the hero's walk speed and autopilot, the shadow's chase; it shows the surface under the hero (custom
/// data) and the cell counts. A click on the map (anywhere the panel is not) adds a wall cell, or removes one: the chunk
/// redraws, its static body rebuilds and the navigation grid re-reads the layer, so the shadow repaths.
/// </summary>
public sealed class Tiles2DPanel : UiDocument
{
    private RmlDataModel? _model;
    private TileMapLayer? _ground, _walls, _objects;
    private TileHero2D? _hero;
    private TileShadow2D? _shadow;
    private NavigationServer2D? _navigation;
    private bool _enterDebugDraw;
    private bool _collision;
    private string _surface = "";
    private int _wallCount;

    public Tiles2DPanel()
    {
        Source = "Assets/Tiles2D/panel.rml";
        AutoFocus = false;
    }

    /// <summary>Draws every layer's collision shapes (<see cref="DebugVisibilityMode.ForceShow"/>).</summary>
    public bool ShowCollision
    {
        get => _collision;
        set
        {
            _collision = value;
            foreach (var layer in (ReadOnlySpan<TileMapLayer?>)[_ground, _walls, _objects])
                if (layer is not null)
                    layer.CollisionVisibilityMode = value ? DebugVisibilityMode.ForceShow : DebugVisibilityMode.Default;
        }
    }

    /// <summary>Y-sorts the objects layer (trees' rows and the hero by their y).</summary>
    public bool YSort
    {
        get => _objects?.YSortEnabled ?? false;
        set
        {
            if (_objects is { } objects)
                objects.YSortEnabled = value;
        }
    }

    public float Speed
    {
        get => _hero?.Speed ?? 0f;
        set
        {
            if (_hero is { } hero)
                hero.Speed = Math.Clamp(value, 0f, 200f);
        }
    }

    public bool Autopilot
    {
        get => _hero?.Autopilot ?? false;
        set
        {
            if (_hero is { } hero)
                hero.Autopilot = value;
        }
    }

    /// <summary>The navigation debug draw (the grid's solid cells, the shadow's path).</summary>
    public bool ShowNavigation
    {
        get => _navigation?.DebugDrawEnabled ?? false;
        set
        {
            if (_navigation is { } navigation)
                navigation.DebugDrawEnabled = value;
        }
    }

    public bool Chase
    {
        get => _shadow?.Chasing ?? false;
        set
        {
            if (_shadow is { } shadow)
                shadow.Chasing = value;
        }
    }

    public string Surface => _surface;

    public int WallCells => _wallCount;

    protected override void OnReady()
    {
        var scene = Parent!.Parent!;
        _ground = scene.GetNode<TileMapLayer>("Ground");
        _walls = scene.GetNode<TileMapLayer>("Walls");
        _objects = scene.GetNode<TileMapLayer>("Objects");
        _hero = scene.GetNodeOrNull<TileHero2D>("Objects/Hero");
        _shadow = scene.GetNodeOrNull<TileShadow2D>("Objects/Shadow");
        // The debug draw is server-global: remember it so leaving this scene restores the other scenes' look.
        _navigation = Tree?.Servers.Get<NavigationServer2D>();
        _enterDebugDraw = _navigation?.DebugDrawEnabled ?? false;
        _wallCount = _walls.UsedCellCount;
        _walls.Changed += OnWallsChanged;
        _model = CreateDataModel("tiles2d")
            .Bind("collision", this, static d => d._collision, static (d, v) => d.ShowCollision = v)
            .Bind("ysort", this, static d => d.YSort, static (d, v) => d.YSort = v)
            .Bind("speed", this, static d => d.Speed, static (d, v) => d.Speed = v)
            .Bind("autopilot", this, static d => d.Autopilot, static (d, v) => d.Autopilot = v)
            .Bind("navigation", this, static d => d.ShowNavigation, static (d, v) => d.ShowNavigation = v)
            .Bind("chase", this, static d => d.Chase, static (d, v) => d.Chase = v)
            .Bind("surface", this, static d => d._surface)
            .Bind("walls", this, static d => d._wallCount);
    }

    protected override void OnExitTree()
    {
        if (_walls is { } walls)
            walls.Changed -= OnWallsChanged;
        if (_navigation is { } navigation)
            navigation.DebugDrawEnabled = _enterDebugDraw;
        _ground = _walls = _objects = null;
        _hero = null;
        _shadow = null;
        _navigation = null;
        base.OnExitTree();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_hero is { } hero && !ReferenceEquals(hero.Surface, _surface))
        {
            _surface = hero.Surface;
            _model?.Dirty("surface");
        }
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { Button: MouseButton.Left, Pressed: true } click && _walls is { } walls)
            ToggleWall(walls.LocalToMap(walls.ToLocal(CanvasInput.WindowToCanvas(this, click.Position))));
    }

    /// <summary>Adds a stone wall at <paramref name="cell"/>, or removes the wall or water there (not under the hero or the shadow).</summary>
    public void ToggleWall(Vector2I cell)
    {
        if (_walls is not { } walls)
            return;
        if (!walls.GetCell(cell).IsEmpty)
            walls.EraseCell(cell);
        else if (!IsUnder(_hero, walls, cell) && !IsUnder(_shadow, walls, cell))
            walls.SetCell(cell, 0, TilesArt.Stone);
    }

    private static bool IsUnder(Node2D? node, TileMapLayer layer, Vector2I cell) =>
        node is not null && layer.LocalToMap(layer.ToLocal(node.GlobalPosition)) == cell;

    private void OnWallsChanged()
    {
        _wallCount = _walls?.UsedCellCount ?? 0;
        _model?.Dirty("walls");
    }
}
