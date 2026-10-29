using MainframeEngine;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace Demo;

/// <summary>
/// The Tiles 2D scene's controls (G2b): show the tiles' collision shapes, y-sort the objects layer on or off, the hero's
/// walk speed and autopilot; it shows the surface under the hero (custom data) and the cell counts. A click on the map
/// (anywhere the panel is not) adds a wall cell, or removes one: the chunk redraws and its static body rebuilds.
/// </summary>
public sealed class Tiles2DPanel : UiDocument
{
    private RmlDataModel? _model;
    private TileMapLayer? _ground, _walls, _objects;
    private TileHero2D? _hero;
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

    public string Surface => _surface;

    public int WallCells => _wallCount;

    protected override void OnReady()
    {
        var scene = Parent!.Parent!;
        _ground = scene.GetNode<TileMapLayer>("Ground");
        _walls = scene.GetNode<TileMapLayer>("Walls");
        _objects = scene.GetNode<TileMapLayer>("Objects");
        _hero = scene.GetNodeOrNull<TileHero2D>("Objects/Hero");
        _wallCount = _walls.UsedCellCount;
        _walls.Changed += OnWallsChanged;
        _model = CreateDataModel("tiles2d")
            .Bind("collision", this, static d => d._collision, static (d, v) => d.ShowCollision = v)
            .Bind("ysort", this, static d => d.YSort, static (d, v) => d.YSort = v)
            .Bind("speed", this, static d => d.Speed, static (d, v) => d.Speed = v)
            .Bind("autopilot", this, static d => d.Autopilot, static (d, v) => d.Autopilot = v)
            .Bind("surface", this, static d => d._surface)
            .Bind("walls", this, static d => d._wallCount);
    }

    protected override void OnExitTree()
    {
        if (_walls is { } walls)
            walls.Changed -= OnWallsChanged;
        _ground = _walls = _objects = null;
        _hero = null;
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

    /// <summary>Adds a stone wall at <paramref name="cell"/>, or removes the one there (not under the hero).</summary>
    public void ToggleWall(Vector2I cell)
    {
        if (_walls is not { } walls)
            return;
        if (!walls.GetCell(cell).IsEmpty)
            walls.EraseCell(cell);
        else if (_hero is null || walls.LocalToMap(walls.ToLocal(_hero.GlobalPosition)) != cell)
            walls.SetCell(cell, 0, TilesArt.Stone);
    }

    private void OnWallsChanged()
    {
        _wallCount = _walls?.UsedCellCount ?? 0;
        _model?.Dirty("walls");
    }
}
