using System.Diagnostics;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.11: a grid navigation region in 3D — an <see cref="AStarGrid2D"/> on the XZ plane at the node's height, for top-down
/// 3D games, tower defence and strategy. Cell (x, z) covers <c>GlobalPosition.XZ + (x, z) · CellSize</c> to
/// <c>+ CellSize</c> at <c>GlobalPosition.Y</c>; global rotation and scale are ignored (a warning, once). Registers with the
/// <see cref="NavigationMap3D"/> of its viewport's <see cref="World3D"/> while in the tree. Its cells come from
/// <see cref="Data"/> (<see cref="Bake"/>: one overlap box per cell against 3D static bodies, between the floor's step
/// height and the agent's height) plus runtime edits (<see cref="SetCellSolid"/>, <see cref="SetCellWeight"/>), which apply
/// at the map's next sync and make agents repath.
/// </summary>
[EditorIcon("grid-3x3", Family = EditorIconFamily.Space3D)]
public class NavigationGrid3D : Node3D
{
    private readonly AStarGrid2D _grid = new();
    private Rect2I _region = new(0, 0, 32, 32);
    private Vector2 _cellSize = new(1f, 1f);
    private GridDiagonalMode _diagonalMode = GridDiagonalMode.OnlyIfNoObstacles;
    private GridHeuristic _heuristic = GridHeuristic.Octile;
    private bool _jumping;
    private uint _layers = 1;
    private NavigationGridData? _data;
    private bool _layoutDirty = true;
    private bool _settingsDirty = true;
    private List<(Vector2I Cell, float Value, bool IsWeight)>? _edits;
    private NavigationMap3D? _map;
    private bool _warnedTransform;
    private bool _warnedData;

    /// <summary>The cells, in cell coordinates (x, z; cell (0, 0) starts at the node's position).</summary>
    [Export]
    public Rect2I Region
    {
        get => _region;
        set
        {
            if (value.Size.X < 0 || value.Size.Y < 0 || (long)value.Size.X * value.Size.Y > AStarGrid2D.MaxCells)
                throw new ArgumentOutOfRangeException(nameof(value), $"A grid region holds 0 to {AStarGrid2D.MaxCells} cells.");
            if (value == _region)
                return;
            _region = value;
            MarkLayout();
        }
    }

    /// <summary>A cell's size in metres (x, z).</summary>
    [Export]
    public Vector2 CellSize
    {
        get => _cellSize;
        set
        {
            if (!(value.X > 0f) || !(value.Y > 0f))
                throw new ArgumentOutOfRangeException(nameof(value), "The cell size must be positive.");
            if (value == _cellSize)
                return;
            _cellSize = value;
            MarkLayout();
        }
    }

    /// <summary>Default <see cref="GridDiagonalMode.OnlyIfNoObstacles"/>: no corner cutting.</summary>
    [Export]
    public GridDiagonalMode DiagonalMode
    {
        get => _diagonalMode;
        set => SetSetting(ref _diagonalMode, value);
    }

    /// <summary>The step cost and estimate (default <see cref="GridHeuristic.Octile"/>).</summary>
    [Export]
    public GridHeuristic Heuristic
    {
        get => _heuristic;
        set => SetSetting(ref _heuristic, value);
    }

    /// <summary>Jump point search (faster on open grids; ignores weights).</summary>
    [Export]
    public bool JumpingEnabled
    {
        get => _jumping;
        set => SetSetting(ref _jumping, value);
    }

    /// <summary>The navigation layers this region is on.</summary>
    [Export(Flags = true)]
    public uint NavigationLayers
    {
        get => _layers;
        set => SetSetting(ref _layers, value);
    }

    /// <summary>The physics layers whose static bodies <see cref="Bake"/> reads.</summary>
    [ExportGroup("Bake")]
    [Export(Flags = true)]
    public uint BakeCollisionMask { get; set; } = CollisionLayers.Default;

    /// <summary>Each cell's overlap box grows by this much on every side (metres): agents keep this far from walls.</summary>
    [Export(Range = "0,16,0.01")]
    public float AgentRadius { get; set; }

    /// <summary>The overlap box's top above the grid (metres; Godot's agent height 1.5): obstacles above it do not block.</summary>
    [Export(Range = "0.01,100,0.01")]
    public float AgentHeight { get; set; } = 1.5f;

    /// <summary>The overlap box's bottom above the grid (metres; Godot's max climb 0.25): the floor and low steps do not block.</summary>
    [Export(Range = "0,10,0.01")]
    public float AgentMaxClimb { get; set; } = 0.25f;

    /// <summary>Bake once in a deferred call when ready in a running game (procedural levels).</summary>
    [Export]
    public bool BakeOnReady { get; set; }

    /// <summary>The baked cells (null: every cell walkable).</summary>
    [Export]
    public NavigationGridData? Data
    {
        get => _data;
        set
        {
            if (ReferenceEquals(value, _data))
                return;
            _data = value;
            _warnedData = false;
            MarkLayout();
        }
    }

    /// <summary>The live grid queries search (brought up to date when read outside a map).</summary>
    public AStarGrid2D Grid
    {
        get
        {
            if (_map is null)
                ApplyPending();
            return _grid;
        }
    }

    /// <summary>How long the last bake took (milliseconds).</summary>
    public double LastBakeMilliseconds { get; private set; }

    /// <summary>The map the grid is on (null outside the tree).</summary>
    public NavigationMap3D? Map => _map;

    /// <summary>The grid's plane height (its global Y).</summary>
    public float Height => GlobalPosition.Y;

    internal AStarGrid2D LiveGrid => _grid;

    /// <summary>Makes a cell solid or walkable at the next map sync (doors, destroyed walls).</summary>
    public void SetCellSolid(Vector2I cell, bool solid) => Edit(cell, solid ? 1f : 0f, isWeight: false);

    /// <summary>Sets a cell's weight scale at the next map sync.</summary>
    public void SetCellWeight(Vector2I cell, float weightScale)
    {
        if (!(weightScale >= 0f) || !float.IsFinite(weightScale))
            throw new ArgumentOutOfRangeException(nameof(weightScale), "A weight scale must be finite and not negative.");
        Edit(cell, weightScale, isWeight: true);
    }

    /// <summary>The cell (x, z) under a global position.</summary>
    public Vector2I CellAt(Vector3 globalPosition)
    {
        var origin = GlobalPosition;
        return new Vector2I((int)MathF.Floor((globalPosition.X - origin.X) / _cellSize.X), (int)MathF.Floor((globalPosition.Z - origin.Z) / _cellSize.Y));
    }

    /// <summary>A cell's centre on the grid's plane (global).</summary>
    public Vector3 CellCenter(Vector2I cell)
    {
        var origin = GlobalPosition;
        return new Vector3(origin.X + (cell.X + 0.5f) * _cellSize.X, origin.Y, origin.Z + (cell.Y + 0.5f) * _cellSize.Y);
    }

    /// <summary>Bakes the solid cells from the static bodies into <see cref="Data"/> (main thread, in the tree).</summary>
    public bool Bake()
    {
        var data = BakeData(out var error);
        if (error is not null)
        {
            Log.Warning($"[Navigation] '{Name}' was not baked: {error}.");
            return false;
        }

        Data = data;
        return true;
    }

    /// <summary>The data a bake would produce, without assigning it (the editor's undoable bake).</summary>
    public NavigationGridData? BakeData(out string? error)
    {
        error = null;
        if (!IsInsideTree || GetViewport() is not { } viewport)
        {
            error = "the grid is not in the tree";
            return null;
        }

        using var scope = NavigationMarkers.Bake.Auto();
        var started = Stopwatch.GetTimestamp();
        var origin = GlobalPosition;
        var cells = new AStarGrid2D { Region = _region, CellSize = _cellSize, Offset = new Vector2(origin.X, origin.Z) };
        cells.Update();
        GridPhysicsBaker3D.Bake(this, viewport.World3D, cells);
        var data = NavigationGridData.FromGrid(cells, NavigationGridSource.Physics);
        data.SourceHash = GridPhysicsBaker3D.SourceHash(this, viewport.World3D);
        LastBakeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return data;
    }

    /// <summary>Whether the static bodies in the region changed since <see cref="Data"/> was baked (null: unknown).</summary>
    public bool? IsBakeOutOfDate()
    {
        if (_data is null || !IsInsideTree || GetViewport() is not { } viewport)
            return null;
        if (_data.Region != _region)
            return true;
        if (_data.SourceHash.Length == 0)
            return null;
        return !string.Equals(GridPhysicsBaker3D.SourceHash(this, viewport.World3D), _data.SourceHash, StringComparison.Ordinal);
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _map = NavigationServer3D.For(Tree!).MapOf(this);
        _map.AddRegion(this);
        _layoutDirty = true;
    }

    protected override void OnReady()
    {
        base.OnReady();
        if (BakeOnReady && Tree is { EditMode: false } tree)
            tree.CallDeferred(static state => ((NavigationGrid3D)state!).BakeIfInTree(), this);
    }

    protected override void OnExitTree()
    {
        _map?.RemoveRegion(this);
        base.OnExitTree();
    }

    // The map's sync: apply what changed since the last one (and the grid's position). Returns whether the grid changed.
    internal bool ApplyPending()
    {
        var changed = false;
        if (_layoutDirty)
        {
            _layoutDirty = false;
            ApplySettings();
            _grid.Region = _region;
            _grid.CellSize = _cellSize;
            _grid.Update();
            if (_data is { } data)
            {
                if (!data.TryApply(_grid, out var error) && !_warnedData)
                {
                    _warnedData = true;
                    Log.Warning($"[Navigation] '{Name}' ignores its navigation data ({error}): every cell is walkable. Bake again.");
                }
            }
            else
            {
                _grid.ClearCells();
            }

            changed = true;
        }
        else if (_settingsDirty)
        {
            ApplySettings();
            changed = true;
        }

        if (_edits is { Count: > 0 } edits)
        {
            foreach (var (cell, value, isWeight) in edits)
            {
                if (!_grid.IsInBounds(cell))
                    continue;
                if (isWeight)
                    _grid.SetPointWeightScale(cell, value);
                else
                    _grid.SetPointSolid(cell, value != 0f);
            }

            edits.Clear();
            changed = true;
        }

        if (IsInsideTree)
        {
            var origin = GlobalPosition;
            var offset = new Vector2(origin.X, origin.Z);
            if (_grid.Offset != offset || PlacedHeight != origin.Y)
            {
                _grid.Offset = offset;
                PlacedHeight = origin.Y;
                changed = true;
            }

            WarnIfTransformed();
        }

        return changed;
    }

    /// <summary>The height the map last saw (a move up or down is a change too).</summary>
    internal float PlacedHeight { get; private set; }

    internal void LeftMap(NavigationMap3D map)
    {
        if (ReferenceEquals(_map, map) && !IsInsideTree)
            _map = null;
    }

    private void Edit(Vector2I cell, float value, bool isWeight)
    {
        if (_map is null)
        {
            ApplyPending();
            if (isWeight)
                _grid.SetPointWeightScale(cell, value);
            else
                _grid.SetPointSolid(cell, value != 0f);
            return;
        }

        (_edits ??= new(16)).Add((cell, value, isWeight));
        _map.MarkDirty();
    }

    private void ApplySettings()
    {
        _settingsDirty = false;
        _grid.DiagonalMode = _diagonalMode;
        _grid.DefaultComputeHeuristic = _heuristic;
        _grid.DefaultEstimateHeuristic = _heuristic;
        _grid.JumpingEnabled = _jumping;
    }

    private void MarkLayout()
    {
        _layoutDirty = true;
        _map?.MarkDirty();
    }

    private void SetSetting<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        _settingsDirty = true;
        _map?.MarkDirty();
    }

    private void BakeIfInTree()
    {
        if (IsInsideTree)
            Bake();
    }

    private void WarnIfTransformed()
    {
        if (_warnedTransform)
            return;
        var b = GlobalTransform.Basis;
        if (Vector3.DistanceSquared(b.X, Vector3.UnitX) > 1e-8f || Vector3.DistanceSquared(b.Y, Vector3.UnitY) > 1e-8f
            || Vector3.DistanceSquared(b.Z, Vector3.UnitZ) > 1e-8f)
        {
            _warnedTransform = true;
            Log.Warning($"[Navigation] '{Name}' is rotated or scaled: navigation grids lie flat on the XZ plane and ignore both.");
        }
    }
}
