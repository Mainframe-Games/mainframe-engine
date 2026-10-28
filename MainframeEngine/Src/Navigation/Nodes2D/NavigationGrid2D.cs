using System.Diagnostics;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A grid navigation region: owns an <see cref="AStarGrid2D"/> and registers it with the <see cref="NavigationMap2D"/> of
/// its viewport's <see cref="World2D"/> while in the tree. Cell (x, y) covers <c>GlobalPosition + (x, y) · CellSize</c> to
/// <c>+ CellSize</c>; the node's global rotation and scale are ignored (grids are axis-aligned like tile maps; a warning is
/// logged once). Its cells come from <see cref="Data"/> (baked by <see cref="Bake"/> from <see cref="Source"/>) plus
/// runtime edits (<see cref="SetCellSolid"/>, <see cref="SetCellWeight"/>: doors, destroyed walls), which apply at the
/// map's next sync and make agents repath. A layout or <see cref="Data"/> change resets the runtime edits.
/// </summary>
[EditorIcon("grid-3x3", Family = EditorIconFamily.Space2D)]
public class NavigationGrid2D : Node2D
{
    private readonly AStarGrid2D _grid = new();
    private Rect2I _region = new(0, 0, 32, 32);
    private Vector2 _cellSize = new(32, 32);
    private GridDiagonalMode _diagonalMode = GridDiagonalMode.OnlyIfNoObstacles;
    private GridHeuristic _heuristic = GridHeuristic.Octile;
    private bool _jumping;
    private uint _layers = 1;
    private NavigationGridSource _source = NavigationGridSource.Physics;
    private NavigationGridData? _data;
    private bool _layoutDirty = true;   // region, cell size or data: rebuild and re-apply the data
    private bool _settingsDirty = true; // diagonal mode, heuristic, jumping: no cell changes
    private bool _sourceDirty;          // a live source (tile layer) changed: re-bake the live grid
    private List<CellEdit>? _edits;
    private NavigationMap2D? _map;
    private INavigationGridBaker? _attached;
    private bool _warnedTransform;
    private bool _warnedData;

    /// <summary>The cells, in cell coordinates (cell (0, 0) starts at the node's position).</summary>
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

    /// <summary>A cell's size in pixels.</summary>
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

    /// <summary>Default <see cref="GridDiagonalMode.OnlyIfNoObstacles"/>: a body cannot squeeze between two diagonal wall corners.</summary>
    [Export]
    public GridDiagonalMode DiagonalMode
    {
        get => _diagonalMode;
        set
        {
            if (value == _diagonalMode)
                return;
            _diagonalMode = value;
            MarkSettings();
        }
    }

    /// <summary>The step cost and estimate (default <see cref="GridHeuristic.Octile"/>: exact for 8-way grids).</summary>
    [Export]
    public GridHeuristic Heuristic
    {
        get => _heuristic;
        set
        {
            if (value == _heuristic)
                return;
            _heuristic = value;
            MarkSettings();
        }
    }

    /// <summary>Jump point search (faster on open grids; ignores weights, as in Godot).</summary>
    [Export]
    public bool JumpingEnabled
    {
        get => _jumping;
        set
        {
            if (value == _jumping)
                return;
            _jumping = value;
            MarkSettings();
        }
    }

    /// <summary>The navigation layers this region is on (a query uses it when its layers intersect these).</summary>
    [Export(Flags = true)]
    public uint NavigationLayers
    {
        get => _layers;
        set
        {
            if (value == _layers)
                return;
            _layers = value;
            MarkSettings(); // which queries use the region changes: agents repath
        }
    }

    /// <summary>Where <see cref="Bake"/> reads the solid cells from.</summary>
    [ExportGroup("Bake")]
    [Export]
    public NavigationGridSource Source
    {
        get => _source;
        set
        {
            if (value == _source)
                return;
            DetachSource();
            _source = value;
            if (IsInsideTree)
                AttachSource();
        }
    }

    /// <summary>Physics source: the collision layers of the static bodies that make cells solid.</summary>
    [Export(Flags = true)]
    public uint BakeCollisionMask { get; set; } = CollisionLayers.Default;

    /// <summary>Physics source: grows each cell's overlap test by this many pixels on every side (clearance for a body's radius).</summary>
    [Export(Range = "0,256,0.5")]
    public float AgentRadius { get; set; }

    /// <summary>TileMapLayer source (G3.6): the layer to read.</summary>
    [Export(NodeType = typeof(Node2D))]
    public NodePath TileMapLayerPath { get; set; }

    /// <summary>TileMapLayer source (G3.6): tiles with collision on this physics layer are solid.</summary>
    [Export]
    public int TilePhysicsLayer { get; set; }

    /// <summary>TileMapLayer source (G3.6): tiles whose bool custom data layer of this name is true are solid.</summary>
    [Export]
    public string SolidDataLayer { get; set; } = "solid";

    /// <summary>TileMapLayer source (G3.6): a float custom data layer with each tile's weight scale ("" = all 1).</summary>
    [Export]
    public string WeightDataLayer { get; set; } = "";

    /// <summary>Bake once when the scene is ready in a running game (procedural levels), after its bodies entered the tree.</summary>
    [Export]
    public bool BakeOnReady { get; set; }

    /// <summary>The baked cells (inline in the scene, or a <c>.mres</c>). Null: every cell walkable.</summary>
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

    /// <summary>
    /// The live grid that queries search. Outside a map (not in the tree) it is brought up to date when read; in a map,
    /// changes reach it at the map's sync (<see cref="NavigationMap2D.ForceSync"/> applies them now).
    /// </summary>
    public AStarGrid2D Grid
    {
        get
        {
            if (_map is null)
                ApplyPending();
            return _grid;
        }
    }

    /// <summary>How long the last <see cref="Bake"/> took (milliseconds; 0 before the first).</summary>
    public double LastBakeMilliseconds { get; private set; }

    /// <summary>The map the grid is registered with (null outside the tree).</summary>
    public NavigationMap2D? Map => _map;

    internal AStarGrid2D LiveGrid => _grid;

    /// <summary>Makes <paramref name="cell"/> solid or walkable in the live grid (not <see cref="Data"/>); applied at the next sync.</summary>
    public void SetCellSolid(Vector2I cell, bool solid)
    {
        if (_map is null)
        {
            ApplyPending();
            _grid.SetPointSolid(cell, solid);
            return;
        }

        (_edits ??= new List<CellEdit>(16)).Add(new CellEdit(cell, solid ? 1f : 0f, IsWeight: false));
        _map.MarkDirty();
    }

    /// <summary>Sets <paramref name="cell"/>'s weight scale in the live grid (not <see cref="Data"/>); applied at the next sync.</summary>
    public void SetCellWeight(Vector2I cell, float weightScale)
    {
        if (!(weightScale >= 0f) || !float.IsFinite(weightScale))
            throw new ArgumentOutOfRangeException(nameof(weightScale), "A weight scale must be finite and not negative.");
        if (_map is null)
        {
            ApplyPending();
            _grid.SetPointWeightScale(cell, weightScale);
            return;
        }

        (_edits ??= new List<CellEdit>(16)).Add(new CellEdit(cell, weightScale, IsWeight: true));
        _map.MarkDirty();
    }

    /// <summary>The cell containing a global position (it may be outside <see cref="Region"/>).</summary>
    public Vector2I CellAt(Vector2 globalPosition)
    {
        var local = (globalPosition - GlobalPosition) / _cellSize;
        return new Vector2I((int)MathF.Floor(local.X), (int)MathF.Floor(local.Y));
    }

    /// <summary>The global centre of <paramref name="cell"/>.</summary>
    public Vector2 CellCenter(Vector2I cell) => GlobalPosition + new Vector2(cell.X + 0.5f, cell.Y + 0.5f) * _cellSize;

    /// <summary>
    /// Bakes the cells from <see cref="Source"/> and replaces <see cref="Data"/> (main thread; allocates). Needs the tree
    /// (physics reads the world). <see cref="NavigationGridSource.Manual"/> clears to all walkable. Returns false when the
    /// source could not be read (a warning says why; <see cref="Data"/> is unchanged).
    /// </summary>
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

    /// <summary>
    /// Bakes into a new <see cref="NavigationGridData"/> without assigning it (the editor assigns it as an undoable edit);
    /// null for <see cref="NavigationGridSource.Manual"/> (all walkable) or when <paramref name="error"/> is set.
    /// </summary>
    public NavigationGridData? BakeData(out string? error)
    {
        error = null;
        if (_source == NavigationGridSource.Manual)
            return null;
        if (!IsInsideTree)
        {
            error = "the grid is not in the tree";
            return null;
        }

        if (NavigationGridBakers.Get(_source) is not { } baker)
        {
            error = $"no baker is registered for the {_source} source (tile maps arrive with G3.6)";
            return null;
        }

        using var scope = NavigationMarkers.Bake.Auto();
        var started = Stopwatch.GetTimestamp();
        baker.PrepareLayout(this);
        var cells = new AStarGrid2D { Region = _region, CellSize = _cellSize, Offset = GlobalPosition };
        cells.Update();
        if (!baker.Bake(this, cells, out error))
            return null;
        var data = NavigationGridData.FromGrid(cells, _source);
        data.SourceHash = baker.SourceHash(this) ?? "";
        LastBakeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return data;
    }

    /// <summary>
    /// Whether the scene changed since <see cref="Data"/> was baked (a static body in the region moved, was added or was
    /// removed): null when unknown (no data, a manual source, not in the tree).
    /// </summary>
    public bool? IsBakeOutOfDate()
    {
        if (_data is null || _source == NavigationGridSource.Manual || !IsInsideTree || NavigationGridBakers.Get(_source) is not { } baker)
            return null;
        if (_data.Region != _region)
            return true;
        var hash = baker.SourceHash(this);
        return hash is null ? null : !string.Equals(hash, _data.SourceHash, StringComparison.Ordinal);
    }

    /// <summary>A live source changed (a tile layer's <c>Changed</c>): the live grid is re-baked at the next sync, at most once.</summary>
    public void MarkSourceChanged()
    {
        _sourceDirty = true;
        _map?.MarkDirty();
        if (_map is null)
            ApplyPending();
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        SetNotifyTransform(true);
        _map = NavigationServer2D.For(Tree!).MapOf(this);
        _map.AddRegion(this);
        _layoutDirty = true; // the offset follows the global position; re-lay out once in the map
        AttachSource();
    }

    protected override void OnReady()
    {
        base.OnReady();
        if (BakeOnReady && Tree is { EditMode: false } tree)
            tree.CallDeferred(static state => ((NavigationGrid2D)state!).BakeIfInTree(), this);
    }

    protected override void OnExitTree()
    {
        DetachSource();
        _map?.RemoveRegion(this);
        base.OnExitTree();
    }

    protected override void OnTransformChanged()
    {
        base.OnTransformChanged();
        _map?.MarkDirty(); // the offset is re-read at sync
    }

    // The map's sync: apply what changed since the last one. Returns whether the grid changed.
    internal bool ApplyPending()
    {
        var changed = false;
        if (_layoutDirty)
        {
            ApplyLayout();
            changed = true;
        }
        else if (_settingsDirty)
        {
            ApplySettings();
            changed = true;
        }

        if (_sourceDirty)
        {
            _sourceDirty = false;
            changed |= RebakeLive();
        }

        if (_edits is { Count: > 0 } edits)
        {
            foreach (var edit in edits)
            {
                if (!_grid.IsInBounds(edit.Cell))
                    continue;
                if (edit.IsWeight)
                    _grid.SetPointWeightScale(edit.Cell, edit.Value);
                else
                    _grid.SetPointSolid(edit.Cell, edit.Value != 0f);
            }

            edits.Clear();
            changed = true;
        }

        if (IsInsideTree)
        {
            var offset = GlobalPosition;
            if (_grid.Offset != offset)
            {
                _grid.Offset = offset;
                changed = true;
            }

            WarnIfTransformed();
        }

        return changed;
    }

    internal void LeftMap(NavigationMap2D map)
    {
        if (ReferenceEquals(_map, map) && !IsInsideTree)
            _map = null;
    }

    private void ApplyLayout()
    {
        _layoutDirty = false;
        ApplySettings();
        _grid.Region = _region;
        _grid.CellSize = _cellSize;
        if (IsInsideTree)
            _grid.Offset = GlobalPosition;
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

        if (_attached is not null)
            _sourceDirty = true; // a live source owns the cells: re-read once, below
    }

    private void ApplySettings()
    {
        _settingsDirty = false;
        _grid.DiagonalMode = _diagonalMode;
        _grid.DefaultComputeHeuristic = _heuristic;
        _grid.DefaultEstimateHeuristic = _heuristic;
        _grid.JumpingEnabled = _jumping;
    }

    // Re-reads a live source into the live grid (not Data).
    private bool RebakeLive()
    {
        if (_attached is not { } baker)
            return false;
        using var scope = NavigationMarkers.Bake.Auto();
        baker.PrepareLayout(this);
        _grid.Region = _region;
        _grid.CellSize = _cellSize;
        _grid.Update();
        _grid.ClearCells();
        if (!baker.Bake(this, _grid, out var error))
        {
            _grid.ClearCells();
            Log.Warning($"[Navigation] '{Name}' could not read its {_source} source: {error}.");
        }

        return true;
    }

    private void MarkLayout()
    {
        _layoutDirty = true;
        _map?.MarkDirty();
    }

    private void MarkSettings()
    {
        _settingsDirty = true;
        _map?.MarkDirty();
    }

    // Physics bakes into Data; other sources with a baker may be live (Attach subscribes to their changes).
    private void AttachSource()
    {
        if (_source is NavigationGridSource.Manual or NavigationGridSource.Physics || NavigationGridBakers.Get(_source) is not { } baker)
            return;
        _attached = baker;
        baker.Attach(this);
        MarkSourceChanged();
    }

    private void DetachSource()
    {
        if (_attached is not { } baker)
            return;
        _attached = null;
        baker.Detach(this);
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
        var t = GlobalTransform;
        if (MathF.Abs(t.X.Y) > 1e-4f || MathF.Abs(t.Y.X) > 1e-4f || MathF.Abs(t.X.X - 1f) > 1e-4f || MathF.Abs(t.Y.Y - 1f) > 1e-4f)
        {
            _warnedTransform = true;
            Log.Warning($"[Navigation] '{Name}' is rotated or scaled: navigation grids are axis-aligned and ignore both.");
        }
    }

    private readonly record struct CellEdit(Vector2I Cell, float Value, bool IsWeight);
}
